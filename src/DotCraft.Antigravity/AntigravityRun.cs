using System.Text.Json.Nodes;
using System.Threading.Channels;
using DotCraft.Agents;
using Microsoft.Extensions.AI;

namespace DotCraft.Antigravity;

internal sealed class AntigravityRun : IAsyncDisposable
{
    public static readonly TimeSpan DefaultToolBatchWindow = TimeSpan.FromMilliseconds(35);

    private readonly AntigravitySettings _settings;
    private readonly AntigravityLimits _limits;
    private readonly string? _configDirectory;
    private readonly string? _resumeId;
    private readonly TimeSpan _toolBatchWindow;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stopping;
    private readonly object _state = new();
    private readonly HashSet<string> _readPaths;

    private PrivateDirectory? _directory;
    private string? _handoffPath;
    private string _geminiDir = "";
    private string _home = "";
    private AntigravityMcpServer? _mcp;
    private AntigravityProcess? _process;
    private AntigravityAcpConnection? _connection;
    private AntigravityAcpSession? _session;
    private AntigravityRequest _request;
    private ProviderConversationIdentity? _identity;
    private Channel<ChatResponseUpdate> _output = NewChannel();
    private readonly List<AIContent> _turnContents = [];
    private readonly List<FunctionCallContent> _stagedCalls = [];
    private string[] _awaitingCalls = [];
    private JsonArray? _expected;
    private AntigravityCheckpoint? _interrupted;
    private AntigravityCheckpoint? _lastFinishedInput;
    private PromptTurn? _turn;
    private Timer? _toolTimer;
    private CancellationTokenSource? _watchdog;
    private int _busy = 1;
    private int _disposed;

    public DateTimeOffset LastUsed { get; private set; }
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool IsWaiting => _session?.IsOpen == true && (_expected is not null || _interrupted is not null);
    public bool AwaitingTools => _awaitingCalls.Length > 0;
    public ChannelReader<ChatResponseUpdate> Output => _output.Reader;
    public ProviderConversationIdentity? Identity => _identity;
    public (string Text, List<string> MediaPaths, string? HandoffPath)? RespawnInput { get; private set; }

    public AntigravityRun(
        AntigravitySettings settings, string root, AntigravityLimits limits, AntigravityRequest request,
        CancellationToken stopping, ProviderConversationIdentity? identity, TimeProvider clock, PrivateDirectory? existingDirectory = null,
        string? resumeConversationId = null, IEnumerable<string>? existingMediaPaths = null,
        string? existingHandoffPath = null, string? configDirectory = null, TimeSpan? toolBatchWindow = null)
    {
        _settings = settings;
        _limits = limits;
        _request = request;
        _identity = identity;
        _clock = clock;
        _resumeId = resumeConversationId;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _directory = existingDirectory ?? PrivateDirectory.CreateRun(root);
        _configDirectory = configDirectory;
        _toolBatchWindow = toolBatchWindow ?? DefaultToolBatchWindow;
        _readPaths = new(existingMediaPaths ?? [], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _handoffPath = existingHandoffPath;
    }

    public async Task<bool> StartAsync(string? input, IEnumerable<string>? mediaPaths, CancellationToken ct)
    {
        var directory = _directory!;
        var (exe, found) = AntigravityEnvironment.ResolveExecutable(_settings.ExecutablePath);
        if (!found)
            throw new InvalidOperationException($"Antigravity.CliNotFound: {AntigravityAccount.InstallHint}");

        if (input is null)
            (input, mediaPaths, _handoffPath) = await _request.BuildColdInputAsync(directory, ct);
        if (_handoffPath is not null) _readPaths.Add(_handoffPath);
        if (mediaPaths is not null) _readPaths.UnionWith(mediaPaths);

        (_home, _geminiDir) = await AntigravityEnvironment.PrepareIsolatedHomeAsync(
            directory, _settings.AllowAiCredits, _readPaths, ct, _configDirectory);
        var work = directory.EnsureSubdirectory("work");
        await directory.WriteAsync(Path.Combine("work", ".agents", "agents", AntigravityRequest.RelayAgentName, "agent.md"),
            _request.BuildRelayAgentMarkdown(_readPaths.Count > 0), ct);
        File.Delete(Path.Combine(work, ".agents", "mcp_config.json"));

        if (_request.Tools.Count > 0)
        {
            _mcp = new(_request.Tools, OnToolCall, _stopping.Token);
            await _mcp.StartAsync(ct);
        }

        var env = AntigravityEnvironment.BuildChildEnvironment(_home, explicitConfigDir: _configDirectory);
        env.Remove("ANTIGRAVITY_CONFIG_DIR");
        _process = new(exe, work, ["acp", "--process-per-session", "--agent", AntigravityRequest.RelayAgentName], env, pidFileDirectory: directory.Path);
        _connection = new(_process, OnAcpMessageAsync, _stopping.Token);
        _session = new(_connection);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds));
        try
        {
            await _session.OpenAsync(work, AntigravityAcpSession.McpServers(_mcp), _resumeId, deadline.Token)
                .WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Antigravity.Timeout: The CLI did not open the ACP session.");
        }
        catch (Exception ex) when (_resumeId is not null && ex is IOException or InvalidDataException)
        {
            var classified = Classify(ex);
            if (classified is ProviderFailureException) throw classified;
            return false;
        }
        catch (Exception ex) when (ex is IOException)
        {
            throw Classify(ex);
        }

        await SelectModelAsync(_request, ct);
        lock (_state) StartPromptLocked(AntigravityAcpSession.TextPrompt(input));
        return true;
    }

    public async Task<ResumeOutcome> TryResumeAsync(AntigravityRequest request, ProviderConversationIdentity? identity, CancellationToken ct)
    {
        JsonNode?[]? added;
        AntigravityCheckpoint? interrupted;
        PromptTurn? pending;
        string[] callIds;
        lock (_state)
        {
            if (!IsWaiting || _request.ResumeFingerprint != request.ResumeFingerprint)
                return ResumeOutcome.Incompatible;
            interrupted = _interrupted;
            added = _expected is null ? null : request.After(_expected);
            pending = AwaitingTools ? _turn : null;
            callIds = _awaitingCalls;
        }

        if (pending is not null)
        {
            if (_identity?.TurnId != identity?.TurnId || added is null
                || added.Length < callIds.Length
                || !added.Take(callIds.Length).All(n => n?["role"]?.GetValue<string>() == "tool" && n["content"]?["CallId"] is not null)
                || !request.TryBuildSteerPrompt(added.AsSpan(callIds.Length), out var guidance)
                || _request.ModelSlug != request.ModelSlug || _request.EffortToken != request.EffortToken)
                return ResumeOutcome.Incompatible;

            var results = request.Messages.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>().TakeLast(callIds.Length).ToArray();
            if (results.Length != callIds.Length || !callIds.ToHashSet(StringComparer.Ordinal).SetEquals(results.Select(r => r.CallId)))
                return ResumeOutcome.Incompatible;

            Task<JsonObject>? superseded = null;
            lock (_state)
            {
                if (!IsWaiting || _turn != pending) return ResumeOutcome.Incompatible;
                BeginSampleLocked(request, identity);
                if (guidance is not null)
                {
                    superseded = pending.Reply;
                    SendPromptLocked(pending, guidance);
                }
                StartWatchdogLocked();
            }

            if (superseded is not null)
            {
                var reply = await superseded.WaitAsync(TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds), ct);
                if (reply["stopReason"]?.GetValue<string>() != "end_turn")
                    throw new InvalidDataException("Antigravity.SteerNotDelivered: The CLI did not hand over the active prompt.");
                lock (_state) WriteUsageLocked(pending, reply);
            }
            foreach (var result in results)
                _mcp!.Complete(result.CallId, result.Result, result.Exception is not null);
            return ResumeOutcome.Resumed;
        }

        (string Text, List<string> MediaPaths, string? HandoffPath)? next = null;
        if (interrupted is not null)
            next = await request.BuildResumeInputAsync(interrupted, _directory!, ct);
        else if (added is not null && await request.BuildIncrementalUserInputAsync(added, _directory!, ct) is { } incremental)
            next = (incremental.Text, incremental.MediaPaths, null);
        if (next is not { } input) return ResumeOutcome.Incompatible;

        if (input.MediaPaths.Any(path => !_readPaths.Contains(path))
            || input.HandoffPath is not null && _handoffPath is null)
        {
            RespawnInput = input;
            return ResumeOutcome.NeedsConversationRespawn;
        }

        await SelectModelAsync(request, ct);
        AntigravityEnvironment.SyncIsolatedToken(_home, _configDirectory);
        lock (_state)
        {
            if (!IsWaiting) return ResumeOutcome.Incompatible;
            BeginSampleLocked(request, identity);
            StartPromptLocked(AntigravityAcpSession.TextPrompt(input.Text));
        }
        return ResumeOutcome.Resumed;
    }

    private async Task SelectModelAsync(AntigravityRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds));
        try
        {
            await _session!.SelectModelAsync(request, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Antigravity.Timeout: The CLI did not confirm the model configuration.");
        }
    }

    public async Task<bool> CancelTurnAsync(TimeSpan timeout)
    {
        PromptTurn? turn;
        Task<JsonObject>? reply;
        lock (_state)
        {
            turn = _turn;
            if (turn is null)
            {
                if (IsBusy && _lastFinishedInput is not null)
                {
                    _interrupted = _lastFinishedInput;
                    _expected = null;
                }
                return _session?.IsOpen == true;
            }
            turn.Stopping = true;
            reply = turn.Reply;
            StopTimersLocked();
        }

        if (reply is not null)
        {
            using var deadline = new CancellationTokenSource(timeout);
            try
            {
                if (!reply.IsCompleted)
                    await _session!.CancelAsync(deadline.Token).WaitAsync(deadline.Token);
                var result = await reply.WaitAsync(deadline.Token);
                if (result["stopReason"]?.GetValue<string>() is not ("cancelled" or "end_turn" or "max_tokens" or "refusal"))
                    return false;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException)
            {
                return false;
            }
        }

        lock (_state)
        {
            _interrupted = CaptureInterruptedLocked(turn) ?? _interrupted;
            _turn = null;
            _expected = null;
            _awaitingCalls = [];
            _stagedCalls.Clear();
            _output.Writer.TryComplete();
        }
        _mcp?.CancelPending();
        return _session?.IsOpen == true;
    }

    public void Release(DateTimeOffset now)
    {
        LastUsed = now;
        Volatile.Write(ref _busy, 0);
    }

    public AntigravityCheckpoint? GetCheckpoint()
    {
        lock (_state)
        {
            if (_turn is not null) return null;
            if (_interrupted is not null) return _interrupted;
            return _expected is { } history && _session?.Id is { Length: > 0 } id
                ? AntigravityCheckpoint.Capture(id, _request.ResumeFingerprint, history, _readPaths, _handoffPath)
                : null;
        }
    }

    public AntigravityCheckpoint? GetInterruptedCheckpoint()
    {
        lock (_state)
            return _turn is { } turn ? CaptureInterruptedLocked(turn) ?? _interrupted
                : IsBusy ? _lastFinishedInput ?? GetCheckpoint() : GetCheckpoint();
    }

    private AntigravityCheckpoint? CaptureInterruptedLocked(PromptTurn turn) =>
        turn.Accepted && _session?.Id is { Length: > 0 } id
            ? AntigravityCheckpoint.Capture(id, turn.Submitted.ResumeFingerprint, turn.Submitted.History,
                _readPaths, _handoffPath, submittedMessageCount: turn.Submitted.Messages.Length)
            : null;

    public async Task<PrivateDirectory?> DetachDirectoryForShelfAsync()
    {
        var directory = Interlocked.Exchange(ref _directory, null);
        await DisposeAsync();
        return directory;
    }

    private void BeginSampleLocked(AntigravityRequest request, ProviderConversationIdentity? identity)
    {
        Volatile.Write(ref _busy, 1);
        _request = request;
        _identity = identity;
        _expected = null;
        _awaitingCalls = [];
        _stagedCalls.Clear();
        _turnContents.Clear();
        _output = NewChannel();
    }

    private void StartPromptLocked(JsonArray blocks)
    {
        _turn = new(_request, new(_geminiDir, _session!.Id));
        SendPromptLocked(_turn, blocks);
        StartWatchdogLocked();
    }

    private void SendPromptLocked(PromptTurn turn, JsonArray blocks)
    {
        var reply = _session!.PromptAsync(blocks, _stopping.Token);
        turn.Reply = reply;
        _ = ObservePromptAsync(turn, reply);
    }

    private async Task ObservePromptAsync(PromptTurn turn, Task<JsonObject> reply)
    {
        try
        {
            var result = await reply;
            lock (_state)
            {
                if (turn != _turn || reply != turn.Reply || turn.Stopping) return;
                StopTimersLocked();
                WriteUsageLocked(turn, result);
                var reason = result["stopReason"]?.GetValue<string>();
                var finish = reason switch
                {
                    "end_turn" or "refusal" => ChatFinishReason.Stop,
                    "max_tokens" or "max_turn_requests" => ChatFinishReason.Length,
                    _ => throw new InvalidDataException($"Antigravity.AcpPromptFailed: Unexpected stop reason '{reason}'.")
                };
                if (!turn.SawText && !turn.SawTools && reason == "end_turn")
                    throw new InvalidDataException("Antigravity.EmptyResponse: The CLI finished without a response.");
                _expected = SnapshotWithAssistant(_turnContents);
                turn.Accepted = true;
                _lastFinishedInput = CaptureInterruptedLocked(turn);
                _interrupted = null;
                _turn = null;
                _output.Writer.TryWrite(Update([], finish));
                _output.Writer.TryComplete();
            }
        }
        catch (Exception ex)
        {
            lock (_state)
                if (turn == _turn && reply == turn.Reply && !turn.Stopping)
                    FailLocked(Classify(ex));
        }
    }

    private void WriteUsageLocked(PromptTurn turn, JsonObject result)
    {
        if (turn.Usage.ReadResult(result) is { } usage)
            _output.Writer.TryWrite(Update([usage]));
    }

    private async Task OnAcpMessageAsync(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var args = message["params"];
        if (message["id"] is { } id)
        {
            if (method == "session/request_permission")
            {
                bool approved;
                lock (_state) approved = _turn is { Stopping: false } && args?["sessionId"]?.GetValue<string>() == _session?.Id
                    && args?["toolCall"] is JsonObject tool && AntigravityAcpToolPolicy.Allows(tool, _request.Tools, _readPaths);
                var allow = approved ? (args?["options"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(option => option["kind"]?.GetValue<string>() == "allow_once")?["optionId"] : null;
                await _connection!.RespondAsync(id, new()
                {
                    ["outcome"] = allow is null ? new JsonObject { ["outcome"] = "cancelled" }
                        : new JsonObject { ["outcome"] = "selected", ["optionId"] = allow.DeepClone() }
                }, _stopping.Token);
            }
            else
                await _connection!.RespondErrorAsync(id, -32601, "The DotCraft relay does not provide this capability.", _stopping.Token);
            return;
        }

        if (method != "session/update" || args?["sessionId"]?.GetValue<string>() != _session?.Id
            || args?["update"] is not JsonObject update) return;
        lock (_state)
        {
            if (_turn is not { } turn) return;
            var type = update["sessionUpdate"]?.GetValue<string>();
            if (type is "user_message_chunk" or "agent_message_chunk" or "agent_thought_chunk" or "tool_call")
                turn.Accepted = true;
            if (turn.Stopping) return;

            if (type == "usage_update")
            {
                if (update["used"] is JsonValue value && value.TryGetValue<long>(out var occupied) && occupied > 0)
                {
                    var usage = new UsageContent(new UsageDetails());
                    ProviderContextUsage.Attach(usage, new(occupied));
                    _output.Writer.TryWrite(Update([usage]));
                }
                return;
            }

            if (type is "tool_call" or "tool_call_update")
            {
                if (update["name"] is not null)
                {
                    if (!AntigravityAcpToolPolicy.Allows(update, _request.Tools, _readPaths))
                        throw new NativeToolBlockedException($"Antigravity.NativeToolBlocked: The CLI attempted an unauthorized tool ({update["name"]}).");
                    StopWatchdogLocked();
                }
                return;
            }
            if (type is "agent_message_chunk" or "agent_thought_chunk"
                && update["content"]?["text"]?.GetValue<string>() is { Length: > 0 } text)
            {
                StopWatchdogLocked();
                AIContent content = type == "agent_message_chunk" ? new TextContent(text) : new TextReasoningContent(text);
                if (content is TextContent)
                {
                    turn.SawText = true;
                    _turnContents.Add(content);
                }
                _output.Writer.TryWrite(Update([content]));
            }
        }
    }

    private void OnToolCall(FunctionCallContent call)
    {
        lock (_state)
        {
            if (_turn is not { Stopping: false } turn || _stopping.IsCancellationRequested) return;
            turn.Accepted = true;
            turn.SawTools = true;
            StopWatchdogLocked();
            _stagedCalls.Add(call);
            _toolTimer?.Dispose();
            _toolTimer = new Timer(_ => FlushTools(), null, _toolBatchWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushTools()
    {
        lock (_state)
        {
            _toolTimer?.Dispose();
            _toolTimer = null;
            if (_turn is not { Stopping: false } turn || _stagedCalls.Count == 0 || _expected is not null) return;
            var calls = _stagedCalls.ToArray();
            _stagedCalls.Clear();
            _awaitingCalls = calls.Select(call => call.CallId).ToArray();
            _expected = SnapshotWithAssistant(_turnContents.Concat(calls));
            if (turn.Usage.ReadToolBoundary() is { } usage)
                _output.Writer.TryWrite(Update([usage]));
            _output.Writer.TryWrite(Update(calls.Cast<AIContent>().ToList(), ChatFinishReason.ToolCalls));
            _output.Writer.TryComplete();
        }
    }

    private JsonArray SnapshotWithAssistant(IEnumerable<AIContent> content) =>
        AntigravityRequest.Snapshot(_request.Messages.Append(new ChatMessage(ChatRole.Assistant, content.ToList())), reasoning: false);

    private ChatResponseUpdate Update(IList<AIContent> contents, ChatFinishReason? finish = null) =>
        new(ChatRole.Assistant, contents)
        {
            ResponseId = _session?.Id,
            MessageId = _session?.Id,
            ModelId = _request.ModelSlug,
            FinishReason = finish
        };

    private void StartWatchdogLocked()
    {
        StopWatchdogLocked();
        _watchdog = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        _ = WatchAsync(_watchdog.Token);
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        var logStart = _clock.GetUtcNow();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300), _clock);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var elapsed = _clock.GetElapsedTime(started);
                var quota = elapsed >= TimeSpan.FromSeconds(1) ? AntigravityLimits.CheckEarlyQuotaLog(_geminiDir, logStart) : null;
                var failure = quota is not null ? _limits.ClassifyAndRecordError(quota)
                    : elapsed >= TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds)
                        ? new TimeoutException($"Antigravity.Timeout: The CLI produced no model output or tool activity in {_settings.RequestTimeoutSeconds}s.")
                        : null;
                if (failure is null) continue;
                lock (_state)
                    if (!ct.IsCancellationRequested)
                        FailLocked(failure);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void StopWatchdogLocked()
    {
        var watchdog = _watchdog;
        _watchdog = null;
        watchdog?.Cancel();
        watchdog?.Dispose();
    }

    private void StopTimersLocked()
    {
        StopWatchdogLocked();
        _toolTimer?.Dispose();
        _toolTimer = null;
    }

    private void FailLocked(Exception error)
    {
        StopTimersLocked();
        _expected = null;
        _output.Writer.TryComplete(error);
    }

    private Exception Classify(Exception error) => error is NativeToolBlockedException or OperationCanceledException or ProviderFailureException
        ? error : _limits.ClassifyAndRecordError(error.Message, _process?.StderrTail);

    private static Channel<ChatResponseUpdate> NewChannel() => Channel.CreateUnbounded<ChatResponseUpdate>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        bool idle;
        lock (_state)
        {
            StopTimersLocked();
            idle = _turn is null;
        }
        if (idle && _process is { HasExited: false } process)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                process.Input.Close();
                await process.ExitAsync(deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
        }

        if (_connection is not null) await _connection.DisposeAsync();
        else if (_process is not null) await _process.DisposeAsync();
        await _stopping.CancelAsync();
        if (_mcp is not null) await _mcp.DisposeAsync();
        var directory = Interlocked.Exchange(ref _directory, null);
        if (directory is not null) await directory.DisposeAsync();
        _stopping.Dispose();
    }

    private sealed class PromptTurn(AntigravityRequest submitted, AntigravityAcpUsage usage)
    {
        public AntigravityRequest Submitted { get; } = submitted;
        public AntigravityAcpUsage Usage { get; } = usage;
        public Task<JsonObject>? Reply { get; set; }
        public bool Accepted { get; set; }
        public bool SawText { get; set; }
        public bool SawTools { get; set; }
        public bool Stopping { get; set; }
    }

    internal enum ResumeOutcome { Incompatible, Resumed, NeedsConversationRespawn }
}
