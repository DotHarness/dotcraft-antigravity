using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DotCraft.Tracing;
using Microsoft.Extensions.AI;

namespace DotCraft.Antigravity;

internal sealed class AntigravityRun : IAsyncDisposable
{
    public static readonly TimeSpan DefaultToolBatchWindow = TimeSpan.FromMilliseconds(35);

    private readonly AntigravitySettings _settings;
    private readonly AntigravityLimits _limits;
    private readonly string? _configDirectory;
    private readonly string? _resumeConversationId;
    private readonly TimeSpan _toolBatchWindow;
    private readonly CancellationTokenSource _stopping;
    private readonly object _state = new();
    private readonly AntigravityUsageBaseline _usageBaseline = new();
    private readonly HashSet<string> _allowedMediaPaths;

    private PrivateDirectory? _directory;
    private readonly AntigravitySteer? _steer;
    private AntigravityMcpServer? _mcp;
    private AntigravityProcess? _process;
    private Task? _reader;
    private Task? _watchdog;
    private CancellationTokenSource? _turnWatchdogCts;
    private Timer? _toolBatchTimer;

    private AntigravityRequest _request;
    private AntigravityStreamDecoder _decoder;
    private Channel<ChatResponseUpdate> _output = NewChannel();
    private readonly List<AIContent> _turnContents = [];
    private readonly List<FunctionCallContent> _stagedCalls = [];
    private readonly List<UsageContent> _deferredUsage = [];
    private string[] _awaitingCallIds = [];
    private JsonArray? _expected;
    private JsonArray? _completedHistory;
    private string _isolatedHome = string.Empty;
    private string _isolatedGeminiDir = string.Empty;
    private int _busy = 1;
    private int _disposed;

    public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool IsWaiting => _expected is not null && !_stopping.IsCancellationRequested && _process is { HasExited: false };
    public bool AwaitingTools => _awaitingCallIds.Length > 0;
    public ChannelReader<ChatResponseUpdate> Output => _output.Reader;
    private string? ConversationId { get; set; }

    public AntigravityRun(
        AntigravitySettings settings,
        string root,
        AntigravityLimits limits,
        AntigravityRequest request,
        CancellationToken stopping,
        PrivateDirectory? existingDirectory = null,
        string? resumeConversationId = null,
        IEnumerable<string>? existingMediaPaths = null,
        string? configDirectory = null,
        TimeSpan? toolBatchWindow = null)
    {
        _settings = settings;
        _limits = limits;
        _configDirectory = configDirectory;
        _resumeConversationId = resumeConversationId;
        ConversationId = resumeConversationId;
        _toolBatchWindow = toolBatchWindow ?? DefaultToolBatchWindow;
        _request = request;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _directory = existingDirectory ?? PrivateDirectory.CreateRun(root);
        _steer = request.Tools.Count > 0 ? new AntigravitySteer(_directory) : null;
        _allowedMediaPaths = new HashSet<string>(existingMediaPaths ?? [], StringComparer.OrdinalIgnoreCase);
        _decoder = new(request.Tools, _allowedMediaPaths, _usageBaseline, _limits, request.ModelSlug);
    }

    public async Task<bool> StartAsync(JsonObject? initialPayload, IEnumerable<string>? initialMediaPaths, CancellationToken ct)
    {
        var directory = _directory ?? throw new ObjectDisposedException(nameof(AntigravityRun));
        var (executable, found) = AntigravityEnvironment.ResolveExecutable(_settings.ExecutablePath);
        if (!found)
            throw new InvalidOperationException($"Antigravity.CliNotFound: {AntigravityAccount.InstallHint}");

        var payload = initialPayload;
        if (payload is null)
        {
            var (cold, coldMedia) = await _request.BuildColdInputAsync(directory, ct);
            payload = cold;
            initialMediaPaths = coldMedia;
        }

        if (initialMediaPaths is not null)
        {
            foreach (var path in initialMediaPaths)
                _allowedMediaPaths.Add(path);
        }

        (_isolatedHome, _isolatedGeminiDir) = await AntigravityEnvironment.PrepareIsolatedHomeAsync(
            directory,
            _settings.AllowAiCredits,
            _allowedMediaPaths,
            ct,
            _configDirectory,
            enableJsonHooks: _steer is not null);

        var work = directory.EnsureSubdirectory("work");
        await directory.WriteAsync(
            Path.Combine("work", ".agents", "agents", AntigravityRequest.RelayAgentName, "agent.md"),
            _request.BuildRelayAgentMarkdown(),
            ct);

        if (_steer is not null)
        {
            await _steer.InstallAsync(ct);
            _mcp = new AntigravityMcpServer(_request.Tools, OnToolCallReceived, _stopping.Token);
            await _mcp.StartAsync(ct);
            await directory.WriteAsync(
                Path.Combine("work", ".agents", "mcp_config.json"),
                _mcp.Configuration.ToJsonString(AntigravityJson.Pretty),
                ct);
        }
        else
        {
            var staleMcp = Path.Combine(work, ".agents", "mcp_config.json");
            if (File.Exists(staleMcp)) File.Delete(staleMcp);
        }

        var args = new List<string>
        {
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--disable-slash-commands",
            "--agent", AntigravityRequest.RelayAgentName,
            "--model", _request.ModelSlug
        };
        if (!string.IsNullOrWhiteSpace(_request.EffortToken))
        {
            args.Add("--effort");
            args.Add(_request.EffortToken);
        }
        if (!string.IsNullOrWhiteSpace(_resumeConversationId))
        {
            args.Add("--conversation");
            args.Add(_resumeConversationId);
        }

        var env = AntigravityEnvironment.BuildChildEnvironment(_isolatedHome, explicitConfigDir: _configDirectory);
        env.Remove("ANTIGRAVITY_CONFIG_DIR");
        _process = new AntigravityProcess(executable, work, args, env, pidFileDirectory: directory.Path);
        StartTurnWatchdog();
        _reader = ReadLoopAsync();

        try
        {
            await _process.WriteJsonLineAsync(payload, ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<ResumeOutcome> TryResumeAsync(AntigravityRequest request, CancellationToken ct)
    {
        JsonNode?[]? added;
        bool awaitingTools;
        lock (_state)
        {
            if (!IsWaiting || _request.Fingerprint != request.Fingerprint)
                return ResumeOutcome.Incompatible;
            added = request.After(_expected!);
            if (added is null)
                return ResumeOutcome.Incompatible;
            awaitingTools = AwaitingTools;
        }

        if (awaitingTools)
        {
            var callCount = _awaitingCallIds.Length;
            if (_mcp is null || added.Length < callCount
                || !added.Take(callCount).All(n => n?["role"]?.GetValue<string>() == "tool" && n["content"]?["CallId"] is not null)
                || !request.TryBuildToolGuidance(added.AsSpan(callCount), out var guidance))
            {
                return ResumeOutcome.Incompatible;
            }

            var results = request.Messages
                .Where(m => m.Role == ChatRole.Tool)
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .TakeLast(callCount)
                .ToArray();

            var expectedIds = _awaitingCallIds.ToHashSet(StringComparer.Ordinal);
            if (results.Length != expectedIds.Count || !expectedIds.SetEquals(results.Select(r => r.CallId)))
                return ResumeOutcome.Incompatible;

            lock (_state)
            {
                if (!IsWaiting) return ResumeOutcome.Incompatible;
                Volatile.Write(ref _busy, 1);
                _request = request;
                _expected = null;
                _awaitingCallIds = [];
                _stagedCalls.Clear();
                _turnContents.Clear();
                _output = NewChannel();
                _decoder = new(request.Tools, _allowedMediaPaths, _usageBaseline, _limits, request.ModelSlug, resumedAfterToolCall: true);
                FlushDeferredUsageLocked();
            }

            if (guidance is not null)
                await _steer!.PublishAsync(guidance, ct);
            StartTurnWatchdog();
            foreach (var result in results)
                _mcp.Complete(result.CallId, result.Result, result.Exception is not null);
            return ResumeOutcome.Resumed;
        }

        var directory = _directory;
        if (directory is null) return ResumeOutcome.Incompatible;
        var incremental = await request.BuildIncrementalUserInputAsync(added, directory, ct);
        if (incremental is null) return ResumeOutcome.Incompatible;

        var (payload, newMedia) = incremental.Value;
        var needsMediaRespawn = newMedia.Any(p => !_allowedMediaPaths.Contains(p));
        if (needsMediaRespawn)
        {
            foreach (var path in newMedia)
                _allowedMediaPaths.Add(path);
            await AntigravityEnvironment.WriteSettingsAsync(directory, _settings.AllowAiCredits, _allowedMediaPaths, ct,
                enableJsonHooks: _steer is not null);
            return ResumeOutcome.NeedsConversationRespawn;
        }

        lock (_state)
        {
            if (!IsWaiting) return ResumeOutcome.Incompatible;
            Volatile.Write(ref _busy, 1);
            _request = request;
            _expected = null;
            _completedHistory = null;
            _awaitingCallIds = [];
            _stagedCalls.Clear();
            _deferredUsage.Clear();
            _turnContents.Clear();
            _output = NewChannel();
            _decoder = new(request.Tools, _allowedMediaPaths, _usageBaseline, _limits, request.ModelSlug);
        }

        if (!string.IsNullOrEmpty(_isolatedHome))
            AntigravityEnvironment.SyncIsolatedToken(_isolatedHome, _configDirectory);

        StartTurnWatchdog();
        try
        {
            await _process!.WriteJsonLineAsync(payload, ct);
            return ResumeOutcome.Resumed;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return ResumeOutcome.Incompatible;
        }
    }

    public void VerifySteerDelivered() => _steer?.VerifyDelivered();

    public void Release(DateTimeOffset now)
    {
        lock (_state)
            _completedHistory = AwaitingTools ? null : _expected;
        LastUsed = now;
        Volatile.Write(ref _busy, 0);
    }

    public AntigravityCheckpoint? GetCheckpoint() =>
        !IsBusy && _completedHistory is { } history && ConversationId is { Length: > 0 } id
            ? AntigravityCheckpoint.Capture(id, _request.Fingerprint, history, _allowedMediaPaths)
            : null;

    public async Task<PrivateDirectory?> DetachDirectoryForShelfAsync()
    {
        var dir = Interlocked.Exchange(ref _directory, null);
        await DisposeAsync();
        return dir;
    }

    private void OnToolCallReceived(FunctionCallContent call)
    {
        lock (_state)
        {
            if (_stopping.IsCancellationRequested || _expected is not null)
                return;

            StopTurnWatchdogLocked();
            _stagedCalls.Add(call);
            _toolBatchTimer?.Dispose();
            _toolBatchTimer = new Timer(_ => FlushStagedToolCalls(), null, _toolBatchWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushDeferredUsageLocked()
    {
        if (_deferredUsage.Count == 0)
            return;

        var items = _deferredUsage.Cast<AIContent>().ToList();
        _deferredUsage.Clear();
        _output.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, items)
        {
            ResponseId = ConversationId,
            MessageId = ConversationId,
            ModelId = _request.ModelSlug
        });
    }

    private void FlushStagedToolCalls()
    {
        lock (_state)
        {
            _toolBatchTimer?.Dispose();
            _toolBatchTimer = null;

            if (_stagedCalls.Count == 0 || _expected is not null || _stopping.IsCancellationRequested)
                return;

            var calls = _stagedCalls.ToArray();
            _stagedCalls.Clear();
            _awaitingCallIds = calls.Select(c => c.CallId).ToArray();

            var assistantContents = new List<AIContent>(_turnContents);
            assistantContents.AddRange(calls);

            var update = new ChatResponseUpdate(ChatRole.Assistant, calls.Cast<AIContent>().ToList())
            {
                ResponseId = ConversationId,
                MessageId = ConversationId,
                ModelId = _request.ModelSlug,
                FinishReason = ChatFinishReason.ToolCalls
            };

            _output.Writer.TryWrite(update);
            _expected = AntigravityRequest.Snapshot(
                _request.Messages.Append(new ChatMessage(ChatRole.Assistant, assistantContents)),
                reasoning: false);
            _output.Writer.TryComplete();
        }
    }

    private TokenUsageRequestTiming? ReadTiming(string? conversationId, int stepIndex, UsageDetails usage) =>
        AntigravityRequestTiming.Read(
            _isolatedGeminiDir,
            conversationId ?? ConversationId,
            stepIndex,
            usage.OutputTokenCount.GetValueOrDefault(),
            usage.ReasoningTokenCount);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process!.Output.ReadLineAsync(_stopping.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var trimmed = line.Trim();
                if (!trimmed.StartsWith('{')) continue;

                JsonDocument document;
                try { document = JsonDocument.Parse(trimmed); }
                catch (JsonException) { continue; }

                using (document)
                {
                    AntigravityStreamDecoder decoder;
                    Channel<ChatResponseUpdate> output;
                    lock (_state)
                    {
                        decoder = _decoder;
                        output = _output;
                    }

                    IEnumerable<ChatResponseUpdate> updates;
                    try
                    {
                        updates = decoder.Read(document.RootElement, () => _process.StderrTail, ReadTiming).ToArray();
                    }
                    catch (NativeToolBlockedException)
                    {
                        _ = _process.DisposeAsync();
                        throw;
                    }

                    if (decoder.ConversationId is { } id)
                    {
                        if (_resumeConversationId is not null && id != _resumeConversationId)
                            throw new InvalidDataException("Antigravity.ResumeMismatch: The CLI opened a different conversation.");
                        ConversationId = id;
                    }

                    if (decoder.SawProgressEvent)
                    {
                        lock (_state) StopTurnWatchdogLocked();
                    }

                    foreach (var update in updates)
                    {
                        lock (_state)
                        {
                            if (!ReferenceEquals(decoder, _decoder)) break;
                            foreach (var content in update.Contents)
                            {
                                if (content is TextContent tc) _turnContents.Add(tc);
                            }
                            if (!output.Writer.TryWrite(update))
                            {
                                foreach (var content in update.Contents)
                                {
                                    if (content is UsageContent usage)
                                    {
                                        TokenUsageRequestTiming.Attach(usage, null!);
                                        _deferredUsage.Add(usage);
                                    }
                                }
                            }
                        }
                    }

                    lock (_state)
                    {
                        if (!ReferenceEquals(decoder, _decoder)) continue;
                        if (decoder.FinalResult)
                        {
                            StopTurnWatchdogLocked();
                            _toolBatchTimer?.Dispose();
                            _toolBatchTimer = null;
                            _expected = AntigravityRequest.Snapshot(
                                _request.Messages.Append(new ChatMessage(ChatRole.Assistant, _turnContents.ToList())),
                                reasoning: false);
                            output.Writer.TryComplete();
                        }
                    }
                }
            }

            var stderr = _process!.StderrTail;
            throw _limits.ClassifyAndRecordError("Antigravity CLI closed its output unexpectedly.", stderr);
        }
        catch (Exception ex)
        {
            lock (_state)
            {
                StopTurnWatchdogLocked();
                _toolBatchTimer?.Dispose();
                _toolBatchTimer = null;
                _expected = null;
                _awaitingCallIds = [];
                _stagedCalls.Clear();
                _output.Writer.TryComplete(ex);
            }
        }
    }

    private void StartTurnWatchdog()
    {
        lock (_state)
        {
            StopTurnWatchdogLocked();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _turnWatchdogCts = cts;
            var startUtc = DateTimeOffset.UtcNow;
            var deadline = TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds);
            var geminiDir = _isolatedGeminiDir;
            _watchdog = RunWatchdogAsync(geminiDir, startUtc, deadline, cts.Token);
        }
    }

    private void StopTurnWatchdogLocked()
    {
        var cts = _turnWatchdogCts;
        _turnWatchdogCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private async Task RunWatchdogAsync(string geminiDir, DateTimeOffset startUtc, TimeSpan deadline, CancellationToken ct)
    {
        try
        {
            var elapsed = TimeSpan.Zero;
            var poll = TimeSpan.FromMilliseconds(300);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(poll, ct);
                elapsed += poll;

                if (elapsed >= TimeSpan.FromSeconds(1))
                {
                    var earlyQuota = AntigravityLimits.CheckEarlyQuotaLog(geminiDir, startUtc);
                    if (earlyQuota is not null)
                    {
                        var failure = _limits.ClassifyAndRecordError(earlyQuota);
                        FailTurn(failure);
                        return;
                    }
                }

                if (elapsed >= deadline)
                {
                    FailTurn(new TimeoutException($"Antigravity.Timeout: The CLI turn exceeded {_settings.RequestTimeoutSeconds}s."));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void FailTurn(Exception error)
    {
        lock (_state)
        {
            _expected = null;
            _awaitingCallIds = [];
            _stagedCalls.Clear();
            _output.Writer.TryComplete(error);
        }
        if (_process is not null)
            _ = _process.DisposeAsync();
    }

    private static Channel<ChatResponseUpdate> NewChannel() => Channel.CreateUnbounded<ChatResponseUpdate>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_state)
        {
            StopTurnWatchdogLocked();
            _toolBatchTimer?.Dispose();
            _toolBatchTimer = null;
        }
        await _stopping.CancelAsync();
        if (_process is not null) await _process.DisposeAsync();
        if (_reader is not null)
        {
            try { await _reader; }
            catch { }
        }
        if (_watchdog is not null)
        {
            try { await _watchdog; }
            catch { }
        }
        if (_mcp is not null) await _mcp.DisposeAsync();
        var dir = Interlocked.Exchange(ref _directory, null);
        if (dir is not null) await dir.DisposeAsync();
        _stopping.Dispose();
    }

    internal enum ResumeOutcome
    {
        Incompatible,
        Resumed,
        NeedsConversationRespawn
    }
}
