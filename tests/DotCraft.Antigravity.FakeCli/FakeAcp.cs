using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal sealed class FakeAcp(string[] args, string root, string cwd)
{
    private readonly object _output = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _permissions = new();
    private readonly int _pid = Environment.ProcessId;
    private readonly string _home = Environment.GetEnvironmentVariable("HOME") ?? "";
    private string? _resume;
    private string _conversation = "";
    private string _model = "gemini-4-neon";
    private string _effort = "high";
    private int _promptCount;
    private int _steerCount;
    private int _callCount;
    private int _pending;
    private int _serverCount;
    private Prompt? _active;
    private Task? _running;
    private McpClient? _mcp;
    private IList<McpClientTool> _tools = [];

    private string? _blockedRepeatTool;
    private bool _recoverNext;

    private string GeminiDir => Path.Combine(_home, ".gemini", "antigravity-cli");

    public async Task<int> RunAsync()
    {
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                var request = JsonNode.Parse(line)!;
                var id = request["id"];
                var input = request["params"];
                switch (request["method"]?.ToString())
                {
                    case "initialize":
                        Reply(id, new
                        {
                            protocolVersion = 1,
                            agentInfo = new { name = "antigravity", version = "1.5.18" },
                            agentCapabilities = new { mcpCapabilities = new { http = true },
                                loadSession = true, sessionCapabilities = new { resume = new { }, close = new { } } }
                        });
                        break;
                    case "session/new":
                    case "session/resume":
                        await OpenAsync(id, input!);
                        break;
                    case "session/set_config_option":
                        var configId = input!["configId"]!.ToString();
                        if (configId == "thought_level"
                            && File.Exists(Path.Combine(root, "fake-reject-effort")))
                        {
                            Save($"rejected-effort-{_pid}.json", new { model = _model });
                            Error(id, -32602, "The fixture rejected the selected thinking level.");
                            break;
                        }
                        if (configId == "model")
                        {
                            _model = input["value"]!.ToString();
                            _effort = "high";
                        }
                        else
                            _effort = input["value"]!.ToString();
                        Reply(id, new { configOptions = ConfigOptions() });
                        break;
                    case "session/prompt":
                        PromptInput(id!, input!);
                        break;
                    case "session/cancel":
                        await CancelAsync();
                        break;
                    case "session/close":
                        await CancelAsync();
                        Reply(id, new { });
                        break;
                    case null when id is not null:
                        if (_permissions.TryRemove(id.ToString(), out var decision))
                            decision.TrySetResult(request["result"]?["outcome"]?["optionId"]?.ToString() == "once");
                        break;
                    case not null:
                        Error(id, -32601, "Method not supported.");
                        break;
                }
            }
            return 0;
        }
        finally
        {
            await CancelAsync(always: true);
            if (_running is not null) await _running;
            if (_mcp is not null) await _mcp.DisposeAsync();
        }
    }

    private async Task OpenAsync(JsonNode? id, JsonNode input)
    {
        _resume = input["sessionId"]?.ToString();
        _conversation = _resume ?? $"conv-{_pid}";
        var nativeSession = Path.Combine(GeminiDir, "fake-native-session");
        var sources = input["mcpServers"]?.AsArray();
        _serverCount = sources?.Count ?? 0;
        Save($"session-{_pid}.json", new { pid = _pid, resume = _resume, cwd, mcpServerCount = _serverCount });
        if (_resume is not null && (!File.Exists(nativeSession) || File.ReadAllText(nativeSession) != _resume))
        {
            Error(id, -32002, "trajectory not found");
            return;
        }
        var mismatch = _resume is not null && File.Exists(Path.Combine(root, "fake-resume-new-id"));
        if (mismatch)
            _conversation = $"conv-{_pid}";
        Directory.CreateDirectory(GeminiDir);
        if (_resume is null)
            File.WriteAllText(nativeSession, _conversation);

        if (sources is { Count: > 0 })
        {
            var bridge = sources.Single()!;
            if (bridge["type"]?.ToString() != "http" || bridge["name"]?.ToString() != "dotcraft")
                throw new InvalidDataException("Expected the explicit DotCraft HTTP registration.");
            _mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new Uri(bridge["url"]!.ToString()),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = bridge["headers"]!.AsArray().ToDictionary(
                    header => header!["name"]!.ToString(), header => header!["value"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            }));
            _tools = await _mcp.ListToolsAsync();
            var listed = new ListToolsResult { Tools = _tools.Select(t => t.ProtocolTool).ToList() };
            Save($"tools-{_pid}.json", new { result = listed });
        }

        if (_resume is null || mismatch)
            Reply(id, new { sessionId = _conversation, configOptions = ConfigOptions() });
        else
            Reply(id, new { configOptions = ConfigOptions() });
    }

    private object[] ConfigOptions()
    {
        string[] gemini = ["gemini-4-neon", "gemini-3.8-flash"];
        var model = new
        {
            type = "select", id = "model", name = "Model", category = "model", currentValue = _model,
            options = gemini.Append("claude-sonnet-4-6").Append("gpt-oss-120b")
                .Select(value => new { value, name = value }).ToArray()
        };
        if (_model.StartsWith("claude", StringComparison.Ordinal))
            return [model];

        var thinking = new
        {
            type = "select", id = "thought_level", name = "Thinking level", category = "thought_level", currentValue = _effort,
            options = new[] { "low", "medium", "high", "xhigh", "max" }.Select(value => new { value, name = value }).ToArray()
        };
        return [model, thinking];
    }

    private void PromptInput(JsonNode id, JsonNode input)
    {
        var blocks = input["prompt"]!.AsArray();
        var textBlocks = blocks.Where(b => b?["type"]?.ToString() == "text").Select(b => b!["text"]!.ToString()).ToArray();
        string text = string.Join("\n\n", textBlocks);
        lock (_output)
        {
            if (input["sessionId"]?.ToString() != _conversation)
            {
                Error(id, -32002, "unknown session");
                return;
            }
            if (_active is { } current)
            {
                Save($"steer-{_pid}-{++_steerCount}.json", new { blocks, pending = Volatile.Read(ref _pending) });
                if (!current.InitialUsageReported && Volatile.Read(ref _pending) > 0)
                {
                    Reply(current.Id, new { stopReason = "end_turn", usage = Usage(60, 10, 0) });
                    current.InitialUsageReported = true;
                }
                else
                {
                    Reply(current.Id, new { stopReason = "end_turn" });
                }
                current.Id = id;
                return;
            }

            var number = ++_promptCount;
            var settingsPath = Path.Combine(GeminiDir, "settings.json");
            var rulesPath = Path.Combine(cwd, "AGENTS.md");
            var capture = new
            {
                pid = _pid, args, cwd, home = _home, resume = _resume, conversationId = _conversation,
                model = _model, effort = _model.StartsWith("claude", StringComparison.Ordinal) ? null : _effort,
                mcpServerCount = _serverCount,
                settings = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath)) : new JsonObject(),
                rulesMarkdown = File.Exists(rulesPath) ? File.ReadAllText(rulesPath) : "",
                hasCustomAgents = Directory.Exists(Path.Combine(cwd, ".agents", "agents")),
                input = new { message = new { content = text }, blocks },
                disableAutoUpdate = Environment.GetEnvironmentVariable("AGY_CLI_DISABLE_AUTO_UPDATE"),
                leakedSecret = Environment.GetEnvironmentVariable("FAKE_HOST_API_KEY")
            };
            if (number == 1) Save($"capture-{_pid}.json", capture);
            Save($"prompt-{_pid}-{number}.json", capture);
            var prompt = new Prompt(number, id);
            _active = prompt;
            _running = RunPromptAsync(prompt, text, blocks);
        }
    }

    private async Task RunPromptAsync(Prompt prompt, string text, JsonArray blocks)
    {
        try
        {
            if (text.Contains("exit-early", StringComparison.Ordinal))
            {
                Environment.Exit(0);
                return;
            }
            if (text.Contains("early-log-quota", StringComparison.Ordinal))
            {
                var logDir = Path.Combine(GeminiDir, "log");
                Directory.CreateDirectory(logDir);
                await File.WriteAllTextAsync(Path.Combine(logDir, "cli-1.log"),
                    "WARN RESOURCE_EXHAUSTED (Individual quota reached. Resets in 1h 20m.), retrying in 15s\n");
                await Task.Delay(Timeout.InfiniteTimeSpan, prompt.Stop.Token);
                return;
            }
            if (text.Contains("quota-error", StringComparison.Ordinal))
            {
                Fail(prompt, "RESOURCE_EXHAUSTED: Individual quota reached. Resets in 2h 30m.");
                return;
            }
            if (text.Contains("credits-error", StringComparison.Ordinal))
            {
                Fail(prompt, "AI credits balance is too low");
                return;
            }
            if (text.Contains("validation-error", StringComparison.Ordinal))
            {
                Fail(prompt, "403 VALIDATION_REQUIRED https://accounts.google.com/verify?session=xyz");
                return;
            }
            if (text.Contains("native-tool", StringComparison.Ordinal)
                || text.Contains("foreign-mcp", StringComparison.Ordinal)
                || (_blockedRepeatTool is not null && text.Contains("disabled by the DotCraft relay", StringComparison.Ordinal)))
            {
                if (_recoverNext && text.Contains("disabled by the DotCraft relay", StringComparison.Ordinal))
                {
                    _recoverNext = false;
                    _blockedRepeatTool = null;
                    var result = await ToolAsync(0, "recovered", prompt.Stop.Token);
                    var content = result.Content.OfType<TextContentBlock>().First().Text;
                    Text($"recovered:{content}");
                    Finish(prompt, "end_turn", Usage(30, 10, 20));
                    return;
                }

                bool completedBypass = text.Contains("native-tool-completed", StringComparison.Ordinal);
                bool recover = text.Contains("native-tool-recover", StringComparison.Ordinal);
                bool foreign = text.Contains("foreign-mcp", StringComparison.Ordinal);
                var toolCallId = $"undeclared-{prompt.Number}";
                var wrapped = text.Contains("foreign-mcp-wrapped", StringComparison.Ordinal);
                var name = _blockedRepeatTool ?? (recover ? "read_file" : wrapped ? "call_mcp_tool" : foreign ? "mcp_other_secret" : "run_command");
                var title = $"Run {name}";
                object rawInput = name == "read_file"
                    ? new { Path = Path.Combine(root, "unauthorized.txt") }
                    : foreign
                        ? new { ServerName = "other", ToolName = "secret" }
                        : new { CommandLine = "fixture-command" };
                var kind = name == "read_file" ? "read" : foreign ? "other" : "execute";
                if (completedBypass)
                {
                    Update(new { sessionUpdate = "tool_call", toolCallId, title, name, args = rawInput, status = "completed", kind, rawInput });
                    await Task.Delay(Timeout.InfiniteTimeSpan, prompt.Stop.Token);
                    return;
                }

                _blockedRepeatTool = name;
                _recoverNext = recover;
                Update(new { sessionUpdate = "tool_call", toolCallId, title, name, args = rawInput, status = "in_progress", kind, rawInput });
                Update(new { sessionUpdate = "tool_call_update", toolCallId, name, status = "pending" });
                var permissionId = $"permission-denied-{prompt.Number}";
                var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _permissions[permissionId] = permission;
                Emit(new { jsonrpc = "2.0", id = permissionId, method = "session/request_permission",
                    @params = new
                    {
                        sessionId = _conversation,
                        toolCall = new { toolCallId, title, name, status = "pending", kind, rawInput },
                        options = new[]
                        {
                            new { optionId = "once", name = "Allow once", kind = "allow_once" },
                            new { optionId = "deny", name = "Reject once", kind = "reject_once" }
                        }
                    } });
                try
                {
                    await permission.Task.WaitAsync(prompt.Stop.Token);
                }
                finally { _permissions.TryRemove(permissionId, out _); }
                Update(new { sessionUpdate = "tool_call_update", toolCallId, name, status = "failed" });
                Finish(prompt, "end_turn", Usage(40, 5, 0));
                return;
            }
            if (text == "hang")
            {
                Text("ready");
                await Task.Delay(Timeout.InfiniteTimeSpan, prompt.Stop.Token);
                return;
            }
            const string echo = "echo-only ";
            if (text.StartsWith(echo, StringComparison.Ordinal))
            {
                Update(new { sessionUpdate = "user_message_chunk", content = new { type = "text", text } });
                var replyId = "echo-barrier";
                var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _permissions[replyId] = reply;
                // The host processes the echo before replying to this request.
                Emit(new { jsonrpc = "2.0", id = replyId, method = "fixture/barrier" });
                await reply.Task.WaitAsync(prompt.Stop.Token);
                using var pipe = new NamedPipeClientStream(".", text[echo.Length..], PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(prompt.Stop.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, prompt.Stop.Token);
                return;
            }
            if (text.Contains("empty-success", StringComparison.Ordinal))
            {
                Finish(prompt, "end_turn");
                return;
            }

            var reportContext = text.Contains("context-metrics", StringComparison.Ordinal);
            if (reportContext)
                Update(new { sessionUpdate = "usage_update", used = 0, size = 1_048_576 });

            string message;
            int inputTokens = prompt.Number == 1 ? 100 : 25;
            int cached = prompt.Number == 1 ? 0 : 90;
            int outputTokens = 15;
            if (text.Contains("media-read", StringComparison.Ordinal))
            {
                var image = blocks.FirstOrDefault(b => b?["type"]?.ToString() == "image");
                var size = image?["data"] is { } data ? Convert.FromBase64String(data.ToString()).Length : -1;
                if (size < 0 && Regex.Match(text, @"\[Image\]\(([^)]+)\)") is { Success: true } match)
                {
                    var path = match.Groups[1].Value;
                    Update(new { sessionUpdate = "tool_call", toolCallId = "media-read",
                        title = $"File {Path.GetFileName(path)}", name = "read_file", kind = "read", status = "completed",
                        args = new { Path = path }, rawInput = new { Path = path } });
                    if (File.Exists(path)) size = (int)new FileInfo(path).Length;
                }
                message = $"saw-image:{size}";
            }
            else if (text.Contains("parallel", StringComparison.Ordinal) && !text.Contains("\"Result\"", StringComparison.Ordinal) && _tools.Count >= 2)
            {
                Text("Checking.");
                if (reportContext)
                    Update(new { sessionUpdate = "usage_update", used = 70, size = 1_048_576 });
                var wrapped = text.Contains("wrapped-legacy", StringComparison.Ordinal);
                var first = ToolAsync(0, "first", prompt.Stop.Token, wrapped);
                var second = ToolAsync(1, "second", prompt.Stop.Token, wrapped);
                var results = (await Task.WhenAll(first, second)).ToList();
                if (text.Contains("two-rounds", StringComparison.Ordinal))
                    results.Add(await ToolAsync(0, "third", prompt.Stop.Token));

                Save($"mcp-results-{_pid}-{prompt.Number}.json", results.Select(result => new
                {
                    text = result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToArray(),
                    images = result.Content.OfType<ImageContentBlock>().Select(image => new { image.MimeType, bytes = image.DecodedData.ToArray() }).ToArray(),
                    result.IsError
                }));
                message = string.Join("|", results.Select(r => r.Content.OfType<TextContentBlock>().First().Text
                    + string.Concat(r.Content.OfType<ImageContentBlock>().Select(i => $" [{i.MimeType}:{i.DecodedData.Length}]"))));
                if (!prompt.InitialUsageReported)
                {
                    inputTokens += 60;
                    outputTokens += 10;
                }
            }
            else
            {
                message = "完成 ✓";
            }

            Text(message);
            if (reportContext)
                Update(new { sessionUpdate = "usage_update", used = 115, size = 1_048_576 });
            Finish(prompt, "end_turn", Usage(inputTokens, outputTokens, cached));
        }
        catch (OperationCanceledException) when (prompt.Stop.IsCancellationRequested)
        {
            Save($"cancel-{_pid}-{prompt.Number}.json", new { pendingBefore = prompt.PendingBefore, pendingAfter = Volatile.Read(ref _pending) });
            Finish(prompt, "cancelled");
        }
        catch (Exception exception)
        {
            Fail(prompt, exception.Message);
        }
    }

    private async Task<CallToolResult> ToolAsync(int index, string value, CancellationToken ct, bool wrapped = false)
    {
        var name = _tools[index].Name;
        var call = Interlocked.Increment(ref _callCount);
        var toolCallId = $"call-{call}";
        var fullName = wrapped ? "call_mcp_tool" : $"mcp_dotcraft_{name}";
        var title = $"Run {fullName}";
        object rawInput = wrapped ? new { ServerName = "dotcraft", ToolName = name, Arguments = new { value } } : new { value };
        Update(new { sessionUpdate = "tool_call", toolCallId, title, kind = "other", status = "pending",
            name = fullName, args = rawInput, rawInput });
        var permissionId = $"permission-{call}";
        var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _permissions[permissionId] = permission;
        Emit(new { jsonrpc = "2.0", id = permissionId, method = "session/request_permission",
            @params = new
            {
                sessionId = _conversation, toolCall = new { toolCallId, title, kind = "other", status = "pending", rawInput },
                options = new[] { new { optionId = "once", name = "Allow once", kind = "allow_once" } }
            } });
        try
        {
            if (!await permission.Task.WaitAsync(ct))
                throw new InvalidOperationException("The fixture's DotCraft tool permission was denied.");
        }
        finally { _permissions.TryRemove(permissionId, out _); }

        Save($"call-{_pid}-{call}.json", new { name, value });
        Interlocked.Increment(ref _pending);
        try
        {
            return await _mcp!.CallToolAsync(name, new Dictionary<string, object?> { ["value"] = value }, cancellationToken: ct);
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private async Task CancelAsync(bool always = false)
    {
        Prompt? prompt;
        lock (_output)
        {
            prompt = _active;
            if (prompt is null) return;
            prompt.PendingBefore = Volatile.Read(ref _pending);
            if (!always && File.Exists(Path.Combine(root, "fake-ignore-cancel")))
            {
                Save($"cancel-ignored-{_pid}.json", new { pendingBefore = prompt.PendingBefore });
                return;
            }
        }
        await prompt.Stop.CancelAsync();
    }

    private void Finish(Prompt prompt, string stopReason, object? usage = null)
    {
        lock (_output)
        {
            _active = null;
            if (usage is null)
                Reply(prompt.Id, new { stopReason });
            else
                Reply(prompt.Id, new { stopReason, usage });
        }
    }

    private void Fail(Prompt prompt, string message)
    {
        lock (_output)
        {
            _active = null;
            Error(prompt.Id, -32603, message);
        }
    }

    private static object Usage(int inputTokens, int outputTokens, int cachedReadTokens) =>
        new { inputTokens, outputTokens, cachedReadTokens, thoughtTokens = 0, totalTokens = inputTokens + outputTokens + cachedReadTokens };

    private void Text(string text) => Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });
    private void Update(object update) => Emit(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = _conversation, update } });
    private void Reply(JsonNode? id, object result) => Emit(new { jsonrpc = "2.0", id, result });
    private void Error(JsonNode? id, int code, string message) => Emit(new { jsonrpc = "2.0", id, error = new { code, message } });

    private void Emit(object wire)
    {
        lock (_output)
        {
            Console.WriteLine(JsonSerializer.Serialize(wire));
            Console.Out.Flush();
        }
    }

    private void Save(string name, object value)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private sealed class Prompt(int number, JsonNode id)
    {
        public int Number { get; } = number;
        public JsonNode Id { get; set; } = id;
        public int PendingBefore { get; set; }
        public bool InitialUsageReported { get; set; }
        public CancellationTokenSource Stop { get; } = new();
    }
}
