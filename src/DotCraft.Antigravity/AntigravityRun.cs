using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DotCraft.Agents;
using DotHarness.Acp.Rpc;
using DotHarness.Acp.V1;
using Microsoft.Extensions.AI;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace DotCraft.Antigravity;

internal sealed class AntigravityRun : IAsyncDisposable, IAcpClientHandler
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
    private string _workDir = "";
    private AntigravityMcpServer? _mcp;
    private AntigravityProcess? _process;
    private AcpV1Client? _client;
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
        _workDir = work;
        await directory.WriteAsync(Path.Combine("work", AntigravityRequest.RulesFileName),
            _request.BuildRelayRulesMarkdown(_readPaths.Count > 0), ct);
        var staleAgents = Path.Combine(work, ".agents");
        if (Directory.Exists(staleAgents))
            Directory.Delete(staleAgents, recursive: true);

        if (_request.Tools.Count > 0)
        {
            _mcp = new(_request.Tools, OnToolCall, _stopping.Token);
            await _mcp.StartAsync(ct);
        }

        var env = AntigravityEnvironment.BuildChildEnvironment(_home, explicitConfigDir: _configDirectory);
        env.Remove("ANTIGRAVITY_CONFIG_DIR");
        _process = new(exe, work, ["acp", "--process-per-session"], env, pidFileDirectory: directory.Path);
        _client = new AcpV1Client(
            new AntigravityAcpTransport(_process),
            this,
            options: new AcpConnectionOptions
            {
                OrderHandlerAdmission = true,
                NotificationError = OnNotificationError
            });
        _session = new(_client);
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
        catch (Exception ex) when (_resumeId is not null && ex is IOException or InvalidDataException or AcpRpcException or JsonException)
        {
            var classified = Classify(ex);
            if (classified is ProviderFailureException) throw classified;
            return false;
        }
        catch (Exception ex) when (ex is IOException or AcpRpcException or JsonException)
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
                || !request.TryBuildSteerPrompt(added.AsSpan(callIds.Length), _session?.SupportsImagePrompt == true, out var guidance)
                || _request.ModelSlug != request.ModelSlug || _request.EffortToken != request.EffortToken)
                return ResumeOutcome.Incompatible;

            var results = request.Messages.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>().TakeLast(callIds.Length).ToArray();
            if (results.Length != callIds.Length || !callIds.ToHashSet(StringComparer.Ordinal).SetEquals(results.Select(r => r.CallId)))
                return ResumeOutcome.Incompatible;

            Task<PromptResponse>? superseded = null;
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
                if (reply.StopReason != StopReason.EndTurn)
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
        Task<PromptResponse>? reply;
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
                if (result.StopReason != StopReason.Cancelled
                    && result.StopReason != StopReason.EndTurn
                    && result.StopReason != StopReason.MaxTokens
                    && result.StopReason != StopReason.Refusal)
                    return false;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException or AcpRpcException or JsonException)
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

    private void StartPromptLocked(ContentBlock[] blocks)
    {
        _turn = new(_request, new(_geminiDir, _session!.Id));
        SendPromptLocked(_turn, blocks);
        StartWatchdogLocked();
    }

    private void SendPromptLocked(PromptTurn turn, ContentBlock[] blocks)
    {
        var reply = _session!.PromptAsync(blocks, _stopping.Token);
        turn.Reply = reply;
        _ = ObservePromptAsync(turn, reply);
    }

    private async Task ObservePromptAsync(PromptTurn turn, Task<PromptResponse> reply)
    {
        try
        {
            var result = await reply;
            lock (_state)
            {
                if (turn != _turn || reply != turn.Reply || turn.Stopping) return;
                StopTimersLocked();
                WriteUsageLocked(turn, result);
                var reason = result.StopReason;
                if (reason == StopReason.EndTurn && turn.BlockedNativeTools.Count > 0)
                {
                    var blocked = turn.BlockedNativeTools.ToArray();
                    if (!turn.RecoveredNativeTool)
                    {
                        turn.RecoveredNativeTool = true;
                        turn.BlockedNativeTools.Clear();
                        var summary = blocked.Length == 1
                            ? $"The built-in tool '{blocked[0]}' is disabled"
                            : $"The built-in tools {string.Join(", ", blocked.Select(name => $"'{name}'"))} are disabled";
                        SendPromptLocked(turn, AntigravityAcpSession.TextPrompt(
                            $"{summary} by the DotCraft relay (this is not a user denial of the task or target). " +
                            "Continue using only the declared dotcraft MCP tools, or answer directly if no tool is needed."));
                        StartWatchdogLocked();
                        return;
                    }

                    throw new NativeToolBlockedException(
                        $"Antigravity.NativeToolBlocked: The CLI attempted an unauthorized tool ({string.Join(", ", blocked)}).");
                }
                var finish = reason == StopReason.EndTurn || reason == StopReason.Refusal
                    ? ChatFinishReason.Stop
                    : reason == StopReason.MaxTokens || reason == StopReason.MaxTurnRequests
                        ? ChatFinishReason.Length
                        : throw new InvalidDataException($"Antigravity.AcpPromptFailed: Unexpected stop reason '{reason.Value}'.");
                if (!turn.SawText && !turn.SawTools && reason == StopReason.EndTurn)
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

    private void WriteUsageLocked(PromptTurn turn, PromptResponse result)
    {
        if (turn.Usage.ReadResult(result) is { } usage)
            _output.Writer.TryWrite(Update([usage]));
    }

    ValueTask<RequestPermissionResponse> IAcpClientHandler.SessionRequestPermissionAsync(
        RequestPermissionRequest parameters, CancellationToken ct)
    {
        bool approved;
        lock (_state)
        {
            approved = _turn is { Stopping: false }
                && parameters.SessionId.Value == _session?.Id
                && AntigravityAcpToolPolicy.Allows(parameters.ToolCall, _request.Tools, _readPaths, _workDir);
            if (!approved && _turn is { Stopping: false } turn && parameters.SessionId.Value == _session?.Id)
                turn.RecordBlockedNativeTool(parameters.ToolCall.Name.GetValueOrDefault()
                    ?? parameters.ToolCall.Title.GetValueOrDefault()
                    ?? "unknown");
        }

        var desired = approved ? PermissionOptionKind.AllowOnce : PermissionOptionKind.RejectOnce;
        var selected = parameters.Options.FirstOrDefault(option => option.Kind == desired)?.OptionId;
        return ValueTask.FromResult(new RequestPermissionResponse
        {
            Outcome = selected is { } optionId
                ? RequestPermissionOutcome.FromSelected(optionId)
                : RequestPermissionOutcome.FromCancelled()
        });
    }

    ValueTask IAcpClientHandler.SessionUpdateAsync(SessionNotification parameters, CancellationToken ct)
    {
        if (parameters.SessionId.Value != _session?.Id)
            return ValueTask.CompletedTask;
        lock (_state)
        {
            if (_turn is not { } turn) return ValueTask.CompletedTask;
            var update = parameters.Update;
            if (update is SessionUpdate.UserMessageChunk or SessionUpdate.AgentMessageChunk
                or SessionUpdate.AgentThoughtChunk or SessionUpdate.ToolCall)
                turn.Accepted = true;
            if (turn.Stopping) return ValueTask.CompletedTask;

            switch (update)
            {
                case SessionUpdate.UsageUpdate usageUpdate:
                    if (usageUpdate.Value.Used is > 0 and <= long.MaxValue)
                    {
                        var usage = new UsageContent(new UsageDetails());
                        ProviderContextUsage.Attach(usage, new((long)usageUpdate.Value.Used));
                        _output.Writer.TryWrite(Update([usage]));
                    }
                    return ValueTask.CompletedTask;

                case SessionUpdate.ToolCall toolCall when toolCall.Value.Name.GetValueOrDefault() is { } callName:
                    HandleNamedToolLocked(turn, callName,
                        AntigravityAcpToolPolicy.Allows(toolCall.Value, _request.Tools, _readPaths, _workDir),
                        toolCall.Value.Status.GetValueOrDefault() == ToolCallStatus.Completed);
                    return ValueTask.CompletedTask;

                case SessionUpdate.ToolCallUpdate toolCallUpdate when toolCallUpdate.Value.Name.GetValueOrDefault() is { } updateName:
                    HandleNamedToolLocked(turn, updateName,
                        AntigravityAcpToolPolicy.Allows(toolCallUpdate.Value, _request.Tools, _readPaths, _workDir),
                        toolCallUpdate.Value.Status.GetValueOrDefault() == ToolCallStatus.Completed);
                    return ValueTask.CompletedTask;

                case SessionUpdate.AgentMessageChunk { Value.Content: ContentBlock.Text { Value.Text: { Length: > 0 } text } }:
                    StopWatchdogLocked();
                    turn.BlockedNativeTools.Clear();
                    var message = new TextContent(text);
                    turn.SawText = true;
                    _turnContents.Add(message);
                    _output.Writer.TryWrite(Update([message]));
                    return ValueTask.CompletedTask;

                case SessionUpdate.AgentThoughtChunk { Value.Content: ContentBlock.Text { Value.Text: { Length: > 0 } thought } }:
                    StopWatchdogLocked();
                    _output.Writer.TryWrite(Update([new TextReasoningContent(thought)]));
                    return ValueTask.CompletedTask;
            }
        }
        return ValueTask.CompletedTask;
    }

    private void HandleNamedToolLocked(PromptTurn turn, string name, bool allowed, bool completed)
    {
        if (allowed)
        {
            StopWatchdogLocked();
            return;
        }

        if (completed)
            FailLocked(new NativeToolBlockedException(
                $"Antigravity.NativeToolBlocked: The CLI attempted an unauthorized tool ({name})."));
        else
            turn.RecordBlockedNativeTool(name);
    }

    private void OnNotificationError(Exception error)
    {
        lock (_state)
            if (_turn is { Stopping: false })
                FailLocked(Classify(error));
    }

    private void OnToolCall(FunctionCallContent call)
    {
        lock (_state)
        {
            if (_turn is not { Stopping: false } turn || _stopping.IsCancellationRequested) return;
            turn.Accepted = true;
            turn.SawTools = true;
            turn.BlockedNativeTools.Clear();
            turn.RecoveredNativeTool = false;
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

        if (_process is not null) await _process.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
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
        public Task<PromptResponse>? Reply { get; set; }
        public List<string> BlockedNativeTools { get; } = [];
        public bool RecoveredNativeTool { get; set; }
        public bool Accepted { get; set; }
        public bool SawText { get; set; }
        public bool SawTools { get; set; }
        public bool Stopping { get; set; }

        public void RecordBlockedNativeTool(string name)
        {
            if (!BlockedNativeTools.Contains(name, StringComparer.Ordinal))
                BlockedNativeTools.Add(name);
        }
    }

    internal enum ResumeOutcome { Incompatible, Resumed, NeedsConversationRespawn }
}
