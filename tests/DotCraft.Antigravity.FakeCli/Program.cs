using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Encoding.UTF8;

var cwd = Environment.CurrentDirectory;
var runDir = Directory.GetParent(cwd)!.FullName;
var root = Environment.GetEnvironmentVariable("ANTIGRAVITY_CONFIG_DIR") is { Length: > 0 } cfg
    ? cfg
    : Directory.GetParent(runDir)!.FullName;
Directory.CreateDirectory(root);

var pid = Environment.ProcessId;
var accountPath = Path.Combine(root, "fake-account.json");
await File.WriteAllTextAsync(Path.Combine(root, $"started-{pid}.json"), JsonSerializer.Serialize(new { pid, args }));

bool IsAuthenticated()
{
    if (!File.Exists(accountPath)) return true;
    using var doc = JsonDocument.Parse(File.ReadAllText(accountPath));
    return !doc.RootElement.TryGetProperty("authenticated", out var prop) || prop.GetBoolean();
}

if (args.FirstOrDefault() == "models")
{
    if (!IsAuthenticated())
    {
        await Console.Error.WriteLineAsync("Error: authentication required. Run `agy` to sign in.");
        return 1;
    }

    var modelsFile = Path.Combine(root, "fake-models.txt");
    if (File.Exists(modelsFile))
    {
        Console.WriteLine(await File.ReadAllTextAsync(modelsFile));
        return 0;
    }

    Console.WriteLine("""
        gemini-4-neon-xhigh          Gemini 4 Neon (XHigh)
        gemini-4-neon-high           Gemini 4 Neon (High)
        gemini-4-neon-medium         Gemini 4 Neon (Medium)
        gemini-4-neon-low            Gemini 4 Neon (Low)
        gemini-3.8-flash-high        Gemini 3.8 Flash (High)
        gemini-3.8-flash-medium      Gemini 3.8 Flash (Medium)
        claude-sonnet-4-6            Claude Sonnet 4.6
        gpt-oss-120b-medium          GPT-OSS 120B (Medium)
        """);
    return 0;
}

if (args.Length >= 2 && args[0] == "acp" && args.Contains("--login"))
{
    await File.WriteAllTextAsync(
        Path.Combine(root, $"login-{pid}.json"),
        JsonSerializer.Serialize(new { browser = Environment.GetEnvironmentVariable("BROWSER") }));
    var callbackMode = File.Exists(Path.Combine(root, "fake-login-callback"));
    if (callbackMode)
    {
        await Console.Error.WriteLineAsync("Visit: https://accounts.google.com/o/oauth2/auth?client=agy&mode=callback");
        await Console.Error.FlushAsync();
        await Task.Delay(150);
        await File.WriteAllTextAsync(accountPath, "{\"authenticated\":true}");
        Console.WriteLine("Sign-in complete.");
        return 0;
    }

    Console.WriteLine("Opening browser to sign in...");
    Console.WriteLine("If the browser did not open, visit: https://accounts.google.com/o/oauth2/auth?client=agy");
    Console.Out.Flush();
    while (true)
    {
        var code = await Console.In.ReadLineAsync();
        if (code is null) return 1;
        if (code.Trim() == "good-code") break;
        Console.WriteLine("Invalid code. Try again.");
        Console.Out.Flush();
    }
    Console.WriteLine("Sign-in complete.");
    await File.WriteAllTextAsync(accountPath, "{\"authenticated\":true}");
    return 0;
}

if (args.Contains("-p"))
{
    var pIdx = Array.IndexOf(args, "-p");
    var prompt = pIdx >= 0 && pIdx + 1 < args.Length ? args[pIdx + 1] : string.Empty;
    if (prompt == "/logout")
    {
        await File.WriteAllTextAsync(accountPath, "{\"authenticated\":false}");
        Console.WriteLine("{\"status\":\"SUCCESS\",\"response\":\"Signed out\"}");
        return 0;
    }

    if (prompt == "/usage")
    {
        if (!IsAuthenticated())
        {
            await Console.Error.WriteLineAsync("authentication required");
            return 1;
        }
        var usageHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        var usageSettingsPath = Path.Combine(usageHome, ".gemini", "antigravity-cli", "settings.json");
        if (File.Exists(usageSettingsPath))
        {
            using var settingsDoc = JsonDocument.Parse(await File.ReadAllTextAsync(usageSettingsPath));
            if (settingsDoc.RootElement.TryGetProperty("disableSlashCommands", out var disableProp)
                && disableProp.ValueKind == JsonValueKind.True)
            {
                Console.WriteLine("{\"status\":\"SUCCESS\",\"response\":\"\",\"num_turns\":1,\"usage\":{\"total_tokens\":4738}}");
                return 0;
            }
        }
        var usagePath = Path.Combine(root, "fake-usage.json");
        if (File.Exists(usagePath))
        {
            Console.WriteLine(await File.ReadAllTextAsync(usagePath));
            return 0;
        }
        Console.WriteLine("""
            {
              "command": {
                "name": "usage",
                "data": {
                  "plan": "Google AI Pro",
                  "credits": 420,
                  "groups": [
                    {
                      "name": "Gemini Models",
                      "buckets": [
                        { "id": "gemini-5h", "window": "5h", "remaining_fraction": 0.8, "reset_time": "2099-01-01T05:00:00Z" },
                        { "id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0.5, "reset_time": "2099-01-08T00:00:00Z" }
                      ]
                    },
                    {
                      "name": "Claude and GPT models",
                      "buckets": [
                        { "id": "claude-5h", "window": "5h", "remaining_fraction": 0.35, "reset_time": "2099-01-01T06:00:00Z" },
                        { "id": "claude-weekly", "window": "weekly", "remaining_fraction": 0.42, "reset_time": "2099-01-08T02:00:00Z" }
                      ]
                    }
                  ]
                }
              }
            }
            """);
        return 0;
    }
}

var firstLine = await Console.In.ReadLineAsync();
if (firstLine is null) return 0;

var resume = Array.IndexOf(args, "--conversation") is var rIdx and >= 0 && rIdx + 1 < args.Length
    ? args[rIdx + 1]
    : null;
var hasTimingFixture = firstLine.Contains("native-timing-fixture", StringComparison.Ordinal);
var conversationId = resume ?? (hasTimingFixture ? Guid.NewGuid().ToString("D") : $"conv-{pid}");

var home = Environment.GetEnvironmentVariable("HOME") ?? "";
var geminiDir = Path.Combine(home, ".gemini", "antigravity-cli");
var timingFixture = Path.Combine(root, "fake-timing.db");
if (hasTimingFixture && File.Exists(timingFixture))
{
    var conversations = Directory.CreateDirectory(Path.Combine(geminiDir, "conversations"));
    File.Copy(timingFixture, Path.Combine(conversations.FullName, $"{conversationId}.db"));
}
var settingsPath = Path.Combine(geminiDir, "settings.json");
var settingsJson = File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath) : "{}";
var agentPath = Path.Combine(cwd, ".agents", "agents", "dotcraft-relay", "agent.md");
var agentMarkdown = File.Exists(agentPath) ? await File.ReadAllTextAsync(agentPath) : "";
var mcpPath = Path.Combine(cwd, ".agents", "mcp_config.json");

await File.WriteAllTextAsync(
    Path.Combine(root, $"capture-{pid}.json"),
    JsonSerializer.Serialize(new
    {
        pid,
        args,
        cwd,
        home,
        settings = JsonNode.Parse(settingsJson),
        agentMarkdown,
        input = JsonNode.Parse(firstLine),
        disableAutoUpdate = Environment.GetEnvironmentVariable("AGY_CLI_DISABLE_AUTO_UPDATE"),
        leakedSecret = Environment.GetEnvironmentVariable("FAKE_HOST_API_KEY")
    }));

if (resume is not null && File.Exists(Path.Combine(root, "fake-resume-fails")))
{
    Emit(new
    {
        @event = "result",
        result = new { conversation_id = conversationId, status = "ERROR", response = "", error = "trajectory not found" }
    });
    return 1;
}

McpClient? mcp = null;
IList<McpClientTool> listed = [];
if (File.Exists(mcpPath))
{
    var config = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!;
    var server = config["mcpServers"]!["dotcraft"]!;
    var address = server["serverUrl"]!.GetValue<string>();
    mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = new Uri(address),
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = server["headers"]!["Authorization"]!.GetValue<string>()
        }
    }));
    listed = await mcp.ListToolsAsync();
    var listResult = JsonSerializer.SerializeToNode(new ListToolsResult { Tools = listed.Select(t => t.ProtocolTool).ToList() });
    await File.WriteAllTextAsync(Path.Combine(root, $"tools-{pid}.json"), new JsonObject { ["result"] = listResult }.ToJsonString());
}

await using var _ = mcp;

Emit(new
{
    @event = "init",
    conversation_id = conversationId,
    init = new { cwd, permission_mode = "request-review", tools = listed.Select(t => t.Name).ToArray() }
});

var turnNumber = 0;
long cumulativeInput = 0;
long cumulativeOutput = 0;
long cumulativeCache = 0;
var line = firstLine;

while (true)
{
    turnNumber++;
    var msg = JsonNode.Parse(line)!;
    var text = msg["message"]?["content"]?.GetValue<string>() ?? string.Empty;

    if (turnNumber > 1)
        await File.WriteAllTextAsync(Path.Combine(root, $"turn-{pid}-{turnNumber}.json"), line);

    if (text.Contains("exit-early", StringComparison.Ordinal))
        return 0;

    if (text.Contains("early-log-quota", StringComparison.Ordinal))
    {
        var logDir = Path.Combine(home, ".gemini", "antigravity-cli", "log");
        Directory.CreateDirectory(logDir);
        await File.WriteAllTextAsync(
            Path.Combine(logDir, "cli-1.log"),
            "2026-11-23T00:00:00Z WARN RESOURCE_EXHAUSTED (Individual quota reached. Resets in 1h 20m.), retrying in 15s\n");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    if (text.Contains("quota-error", StringComparison.Ordinal))
    {
        Emit(new
        {
            @event = "result",
            result = new
            {
                conversation_id = conversationId,
                status = "ERROR",
                response = "",
                error = "RESOURCE_EXHAUSTED: Individual quota reached. Resets in 2h 30m."
            }
        });
        return 1;
    }

    if (text.Contains("credits-error", StringComparison.Ordinal))
    {
        Emit(new
        {
            @event = "result",
            result = new
            {
                conversation_id = conversationId,
                status = "ERROR",
                response = "",
                error = "AI credits balance is too low"
            }
        });
        return 1;
    }

    if (text.Contains("validation-error", StringComparison.Ordinal))
    {
        Emit(new
        {
            @event = "result",
            result = new
            {
                conversation_id = conversationId,
                status = "ERROR",
                response = "",
                error = "403 VALIDATION_REQUIRED https://accounts.google.com/verify?session=xyz"
            }
        });
        return 1;
    }

    if (text.Contains("native-tool", StringComparison.Ordinal))
    {
        Emit(new
        {
            @event = "step_update",
            step_update = new
            {
                conversation_id = conversationId,
                step_index = 1,
                state = "DONE",
                step_type = "tool",
                tool_name = "run_command",
                tool_info = new { name = "run_command", parameters = new { CommandLine = "whoami" } }
            }
        });
        return 0;
    }

    if (text.Contains("foreign-mcp", StringComparison.Ordinal))
    {
        Emit(new
        {
            @event = "step_update",
            step_update = new
            {
                conversation_id = conversationId,
                step_index = 1,
                state = "ACTIVE",
                step_type = "tool",
                tool_name = "call_mcp_tool",
                tool_info = new
                {
                    name = "call_mcp_tool",
                    parameters = new { ServerName = "other", ToolName = "secret" }
                }
            }
        });
        return 0;
    }

    if (text.Contains("empty-success", StringComparison.Ordinal))
    {
        Emit(new
        {
            @event = "result",
            result = new
            {
                conversation_id = conversationId,
                status = "SUCCESS",
                response = "",
                num_turns = turnNumber
            }
        });
        line = await Console.In.ReadLineAsync();
        if (line is null) return 0;
        continue;
    }

    if (Regex.IsMatch(text, @"\bhang\b"))
    {
        Emit(new
        {
            @event = "step_update",
            step_update = new
            {
                conversation_id = conversationId,
                step_index = 1,
                state = "ACTIVE",
                step_type = "agent_response",
                text_delta = "ready"
            }
        });
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    if (text.Contains("media-read", StringComparison.Ordinal))
    {
        var imgMatch = Regex.Match(text, @"\[Image\]\(([^)]+)\)");
        var imgPath = imgMatch.Success ? imgMatch.Groups[1].Value : "";
        Emit(new
        {
            @event = "step_update",
            step_update = new
            {
                conversation_id = conversationId,
                step_index = 1,
                state = "DONE",
                step_type = "tool",
                tool_name = "read_file",
                tool_info = new { name = "read_file", parameters = new { Path = imgPath } }
            }
        });
        EmitSuccess("saw-image:" + (File.Exists(imgPath) ? new FileInfo(imgPath).Length : -1));
    }
    else if (text.Contains("parallel", StringComparison.Ordinal) && !text.Contains("\"Result\"", StringComparison.Ordinal) && mcp is not null && listed.Count >= 2)
    {
        Emit(new
        {
            @event = "step_update",
            step_update = new
            {
                conversation_id = conversationId,
                step_index = 1,
                state = "DONE",
                step_type = "agent_response",
                text_delta = "Checking.",
                usage = new
                {
                    input_tokens = 60,
                    output_tokens = 10,
                    cache_read_tokens = 0,
                    total_tokens = 70
                }
            }
        });

        var a = mcp.CallToolAsync(listed[0].Name, new Dictionary<string, object?> { ["value"] = "first" }).AsTask();
        var b = mcp.CallToolAsync(listed[1].Name, new Dictionary<string, object?> { ["value"] = "second" }).AsTask();
        var results = await Task.WhenAll(a, b);
        var reply = string.Join("|", results.Select(r =>
            r.Content.OfType<TextContentBlock>().First().Text +
            string.Concat(r.Content.OfType<ImageContentBlock>().Select(image => $" [{image.MimeType}:{image.DecodedData.Length}]"))));

        Emit(new
        {
            @event = "step_update",
            step_update = new
            {
                conversation_id = conversationId,
                step_index = 2,
                state = "DONE",
                step_type = "tool",
                tool_name = "call_mcp_tool",
                tool_info = new
                {
                    name = "call_mcp_tool",
                    parameters = new { ServerName = "dotcraft", ToolName = listed[0].Name }
                }
            }
        });

        if (text.Contains("two-rounds", StringComparison.Ordinal))
        {
            var third = await mcp.CallToolAsync(listed[0].Name, new Dictionary<string, object?> { ["value"] = "third" });
            reply += "|" + third.Content.OfType<TextContentBlock>().First().Text;
        }

        EmitSuccess(reply);
    }
    else
    {
        EmitSuccess("完成 ✓");
    }

    line = await Console.In.ReadLineAsync();
    if (line is null) return 0;
}

void EmitSuccess(string response)
{
    var stepInput = turnNumber == 1 ? 100 : 25;
    var stepOutput = 15;
    var stepCache = turnNumber == 1 ? 0 : 90;
    cumulativeInput += stepInput;
    cumulativeOutput += stepOutput;
    cumulativeCache += stepCache;
    var stepTotal = stepInput + stepOutput;
    var total = cumulativeInput + cumulativeOutput;

    Emit(new
    {
        @event = "step_update",
        step_update = new
        {
            conversation_id = conversationId,
            step_index = turnNumber * 10,
            state = "DONE",
            step_type = "agent_response",
            text_delta = response,
            usage = new
            {
                input_tokens = stepInput,
                output_tokens = stepOutput,
                cache_read_tokens = stepCache,
                total_tokens = stepTotal
            }
        }
    });

    Emit(new
    {
        @event = "step_update",
        step_update = new
        {
            conversation_id = conversationId,
            step_index = turnNumber * 10 + 1,
            state = "DONE",
            step_type = "checkpoint",
            usage = new
            {
                input_tokens = 12,
                output_tokens = 3,
                cache_read_tokens = 0,
                total_tokens = 15
            }
        }
    });

    Emit(new
    {
        @event = "result",
        result = new
        {
            conversation_id = conversationId,
            status = "SUCCESS",
            response,
            num_turns = turnNumber,
            usage = new
            {
                input_tokens = cumulativeInput,
                output_tokens = cumulativeOutput,
                cache_read_tokens = cumulativeCache,
                total_tokens = total
            }
        }
    });
}

static void Emit(object value)
{
    Console.WriteLine(JsonSerializer.Serialize(value));
    Console.Out.Flush();
}
