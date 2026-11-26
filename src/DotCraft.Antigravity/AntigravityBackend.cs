using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Contributions;
using Microsoft.Extensions.AI;

namespace DotCraft.Antigravity;

internal sealed class AntigravityBackend : IModelBackendSource, IModelBackend, IAsyncDisposable
{
    private const int MaxIdleRuns = 6;

    private readonly AntigravitySettings _settings;
    private readonly string _root;
    private readonly string? _configDirectory;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _toolBatchWindow;
    private readonly AntigravityModels _models = new();
    private readonly AntigravityAuthStatusCache _authCache = new();
    private readonly AntigravityLimits _limits;
    private readonly AntigravityUsage _usage;
    private readonly CancellationTokenSource _stopping;
    private readonly EventHandler _processExitHandler;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AntigravityRun> _runs = new(StringComparer.Ordinal);
    private readonly AntigravitySessions _sessions;
    private DateTimeOffset _nextSessionSweep;
    private int _disposed;

    public IModelBackend Backend => this;
    public CancellationToken Stopping => _stopping.Token;

    public ModelBackendDescriptor Descriptor { get; } = new(
        "antigravity",
        "Antigravity",
        "antigravity",
        new[] { "antigravity" },
        new[] { new ProviderAuthenticationMethod("cli", "Antigravity account", "cli") },
        new ModelProviderCapabilities
        {
            CachedInputUsageReporting = true,
            ExtendedThinking = true
        });

    public AntigravityBackend(
        AntigravitySettings settings,
        string root,
        CancellationToken stopping = default,
        TimeProvider? clock = null,
        string? configDirectory = null,
        TimeSpan? toolBatchWindow = null)
    {
        _settings = settings;
        _root = root;
        _configDirectory = configDirectory;
        _clock = clock ?? TimeProvider.System;
        _toolBatchWindow = toolBatchWindow ?? AntigravityRun.DefaultToolBatchWindow;
        _limits = new AntigravityLimits(_clock);
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _usage = new AntigravityUsage(settings, root, _limits, _stopping.Token, _clock, configDirectory);
        _processExitHandler = static (_, _) => AntigravityProcess.TerminateTrackedProcesses();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        Directory.CreateDirectory(root);
        _sessions = new AntigravitySessions(root, _clock);
        PrivateDirectory.SweepStale(root, "usage-");
        PrivateDirectory.SweepStale(root, "probe-");
    }

    public IModelProvider CreateProvider(ModelBackendContext context) => new Provider(this, context.Stopping);

    internal async Task CheckReadyAsync(AntigravityAccount account, AntigravityRequest request, CancellationToken ct)
    {
        _limits.ThrowIfBlocked();

        var identity = ProviderRequestContextScope.Current?.CurrentIdentity;
        if (identity is not null)
        {
            var key = BuildRunKey(request.Runtime, identity);
            await _gate.WaitAsync(ct);
            try
            {
                if (_runs.TryGetValue(key, out var existing) && existing.IsWaiting && existing.AwaitingTools)
                    return;
            }
            finally
            {
                _gate.Release();
            }
        }

        var status = await account.GetStatusAsync(ct);
        if (!status.IsAuthenticated)
        {
            var code = status.FailureCode;
            var message = status.FailureMessage;
            var needsLogin = code == AntigravityAccount.LoginRequiredCode;
            throw new ProviderFailureException(
                new ProviderFailure(needsLogin ? ProviderFailureKind.Unauthorized : ProviderFailureKind.Other,
                    HttpStatus: needsLogin ? 401 : null, ProviderErrorCode: code) { Message = message },
                new InvalidOperationException($"{code}: {message}"));
        }
    }

    private static string BuildRunKey(EffectiveModelRuntime runtime, ProviderConversationIdentity identity) =>
        string.Join('\0', runtime.ProviderId, identity.CurrentThreadId, identity.ContextWindowId, identity.RequestKind.ToString());

    private string BuildSessionKey(string runKey) =>
        AntigravityRequest.Hash(string.Join('\0', runKey, _settings.ExecutablePath,
            _configDirectory ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_CONFIG_DIR")));

    internal async IAsyncEnumerable<ChatResponseUpdate> SampleAsync(
        AntigravityRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var identity = ProviderRequestContextScope.Current?.CurrentIdentity;
        var key = identity is null ? Guid.NewGuid().ToString("N") : BuildRunKey(request.Runtime, identity);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Stopping);
        var token = linked.Token;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            AntigravityRun run;
            bool resumedFromShelf;
            await _gate.WaitAsync(token);
            try
            {
                _runs.TryGetValue(key, out var prior);
                if (prior?.IsBusy == true)
                    throw new InvalidOperationException("Antigravity.ConcurrentSampling: This conversation already has an active sampling request.");

                var resumed = false;
                resumedFromShelf = false;
                if (prior is not null)
                {
                    AntigravityRun.ResumeOutcome outcome;
                    try
                    {
                        outcome = await prior.TryResumeAsync(request, token);
                    }
                    catch
                    {
                        _runs.Remove(key);
                        await RetireAsync(key, prior, shelve: false);
                        throw;
                    }

                    if (outcome == AntigravityRun.ResumeOutcome.Resumed)
                    {
                        resumed = true;
                    }
                    else if (outcome == AntigravityRun.ResumeOutcome.NeedsConversationRespawn)
                    {
                        _runs.Remove(key);
                        await RetireAsync(key, prior, shelve: true);
                    }
                    else
                    {
                        _runs.Remove(key);
                        await RetireAsync(key, prior, shelve: false);
                    }
                }

                if (resumed)
                {
                    run = prior!;
                }
                else
                {
                    (run, resumedFromShelf) = await StartAsync(key, request,
                        allowShelf: attempt == 0 && identity?.RequestKind == ProviderRequestKind.Turn, token);
                }
            }
            finally
            {
                _gate.Release();
            }

            var completed = false;
            var yieldedAny = false;
            var fallbackToCold = false;
            try
            {
                var reader = run.Output;
                while (true)
                {
                    bool hasMore;
                    try
                    {
                        hasMore = await reader.WaitToReadAsync(token);
                    }
                    catch (ProviderFailureException ex) when (ex.Failure.Kind == ProviderFailureKind.Unauthorized)
                    {
                        if (attempt == 0 && !yieldedAny && AntigravityEnvironment.ResolveRealTokenPath(_configDirectory) is not null)
                        {
                            fallbackToCold = true;
                            break;
                        }
                        _authCache.Clear();
                        throw;
                    }
                    catch (Exception ex) when (
                        resumedFromShelf
                        && attempt == 0
                        && !yieldedAny
                        && ex is not OperationCanceledException
                        && ex is not ProviderFailureException
                        && ex is not NativeToolBlockedException)
                    {
                        fallbackToCold = true;
                        break;
                    }

                    if (!hasMore) break;
                    while (reader.TryRead(out var update))
                    {
                        yieldedAny = true;
                        yield return update;
                    }
                }

                if (!fallbackToCold)
                {
                    run.VerifySteerDelivered();
                    completed = true;
                    _limits.ClearOnSuccess();
                    yield break;
                }
            }
            finally
            {
                await _gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (completed && identity is not null && run.IsWaiting &&
                        (run.AwaitingTools || identity.RequestKind == ProviderRequestKind.Turn))
                    {
                        run.Release(_clock.GetUtcNow());
                        var idle = _runs.Where(pair => !pair.Value.IsBusy).OrderBy(pair => pair.Value.LastUsed).ToArray();
                        foreach (var (idleKey, idleRun) in idle.Take(Math.Max(0, idle.Length - MaxIdleRuns)))
                        {
                            _runs.Remove(idleKey);
                            await RetireAsync(idleKey, idleRun, shelve: true);
                        }
                    }
                    else
                    {
                        if (_runs.TryGetValue(key, out var current) && ReferenceEquals(current, run))
                            _runs.Remove(key);
                        await RetireAsync(key, run, shelve: false);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
    }

    private async Task<(AntigravityRun Run, bool ResumedFromShelf)> StartAsync(
        string key,
        AntigravityRequest request,
        bool allowShelf,
        CancellationToken ct)
    {
        if (allowShelf && await _sessions.TakeAsync(BuildSessionKey(key)) is { } saved)
        {
            var (directory, checkpoint) = saved;
            var attached = false;
            try
            {
                var media = checkpoint.MediaFiles.Select(file => Path.Combine(directory.Path, "media", file)).ToArray();
                var handoff = checkpoint.HasHandoffFile ? Path.Combine(directory.Path, AntigravityHandoff.RelativePath) : null;
                if (request.After(checkpoint) is { } added && media.All(File.Exists)
                    && (handoff is null || File.Exists(handoff))
                    && await request.BuildIncrementalUserInputAsync(added, directory, ct) is { } incremental)
                {
                    var resumedRun = new AntigravityRun(
                        _settings,
                        _root,
                        _limits,
                        request,
                        Stopping,
                        existingDirectory: directory,
                        resumeConversationId: checkpoint.ConversationId,
                        existingMediaPaths: media.Concat(incremental.MediaPaths),
                        existingHandoffPath: handoff,
                        configDirectory: _configDirectory,
                        toolBatchWindow: _toolBatchWindow);

                    attached = true;
                    var launched = await LaunchAsync(key, resumedRun, incremental.Payload, incremental.MediaPaths, ct);
                    if (launched is not null)
                        return (launched, true);
                }
            }
            finally
            {
                if (!attached)
                    await directory.DisposeAsync();
            }
        }

        var coldRun = new AntigravityRun(
            _settings,
            _root,
            _limits,
            request,
            Stopping,
            configDirectory: _configDirectory,
            toolBatchWindow: _toolBatchWindow);

        var cold = await LaunchAsync(key, coldRun, initialPayload: null, initialMediaPaths: null, ct)
            ?? throw new IOException("Antigravity.StartFailed: The Antigravity CLI ended before accepting the request.");
        return (cold, false);
    }

    private async Task<AntigravityRun?> LaunchAsync(
        string key,
        AntigravityRun run,
        JsonObject? initialPayload,
        IEnumerable<string>? initialMediaPaths,
        CancellationToken ct)
    {
        _runs.Add(key, run);
        try
        {
            if (await run.StartAsync(initialPayload, initialMediaPaths, ct))
                return run;
        }
        catch
        {
            _runs.Remove(key);
            await RetireAsync(key, run, shelve: false);
            throw;
        }

        _runs.Remove(key);
        await RetireAsync(key, run, shelve: false);
        return null;
    }

    private async Task RetireAsync(string key, AntigravityRun run, bool shelve)
    {
        if (shelve && run.GetCheckpoint() is { } checkpoint
            && await run.DetachDirectoryForShelfAsync() is { } directory)
        {
            await _sessions.SaveAsync(BuildSessionKey(key), directory, checkpoint);
            await _sessions.PruneAsync();
        }
        else
        {
            await run.DisposeAsync();
        }
    }

    private async Task RetireIdleAsync(TimeSpan idleFor)
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, run) in _runs.ToArray())
        {
            if (run.IsBusy || now - run.LastUsed < idleFor) continue;
            _runs.Remove(key);
            await RetireAsync(key, run, shelve: true);
        }

        if (now >= _nextSessionSweep)
        {
            await _sessions.PruneAsync();
            _nextSessionSweep = now.AddHours(1);
        }
    }

    internal async Task RetireIdleNowAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try { await RetireIdleAsync(TimeSpan.Zero); }
        finally { _gate.Release(); }
    }

    public async Task SuperviseAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Stopping);
        try
        {
            await _gate.WaitAsync(linked.Token);
            try
            {
                await _sessions.PruneAsync(sweepOrphans: true);
                _nextSessionSweep = _clock.GetUtcNow().AddHours(1);
            }
            finally { _gate.Release(); }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(linked.Token))
            {
                await _gate.WaitAsync(linked.Token);
                try { await RetireIdleAsync(TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds)); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            await StopRunsAsync(preserveSessions: true);
        }
    }

    private Task StopRunsAsync() => StopRunsAsync(preserveSessions: false);

    private async Task StopRunsAsync(bool preserveSessions)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var (key, run) in _runs.ToArray())
                await RetireAsync(key, run, shelve: preserveSessions);
            _runs.Clear();

            if (!preserveSessions)
                await _sessions.PruneAsync(clear: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        await _stopping.CancelAsync();
        await StopRunsAsync(preserveSessions: true);
        _stopping.Dispose();
        _gate.Dispose();
    }

    private sealed class Provider(AntigravityBackend backend, CancellationToken stopping)
        : IModelProvider, IProviderModelRuntimeResolver, IModelCatalogProvider, IProviderAuthenticator, IProviderUsageReader
    {
        private readonly AntigravityAccount _account = new(
            backend._settings,
            backend._root,
            backend._models,
            backend._usage,
            backend._limits,
            backend.Stopping,
            stopping,
            backend.StopRunsAsync,
            backend._clock,
            backend._configDirectory,
            backend._authCache);

        public IReadOnlyCollection<string> Protocols => new[] { "antigravity" };

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public EffectiveModelRuntime ResolveRuntime(EffectiveModelRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(runtime.Model))
                return runtime;

            var metadata = backend._models.FindModel(runtime.Model);
            var maxOutput = runtime.ConfiguredMaxOutputTokens is > 0
                ? Math.Min(runtime.ConfiguredMaxOutputTokens.Value, metadata?.MaxOutputTokens ?? 65_536)
                : metadata?.MaxOutputTokens;

            return runtime with
            {
                ModelMetadata = metadata,
                MaxOutputTokens = maxOutput
            };
        }

        public IChatClient CreateChatClient(EffectiveModelRuntime runtime) => new Client(backend, runtime, stopping, _account);
        public Task<ProviderAuthenticationStatus> GetStatusAsync(CancellationToken ct) => _account.GetStatusAsync(ct);
        public Task<ProviderAuthenticationStatus> LoginAsync(string method, IProviderLoginInteraction interaction, CancellationToken ct) =>
            _account.LoginAsync(method, interaction, ct);
        public Task LogoutAsync(CancellationToken ct) => _account.LogoutAsync(ct);
        public Task<ModelCatalogResult> FetchModelsAsync(EffectiveModelRuntime runtime, CancellationToken ct) =>
            _account.FetchModelsAsync(runtime, ct);
        public Task<ProviderUsageSnapshot?> ReadAsync(CancellationToken ct) => _account.ReadUsageAsync(ct);
    }

    private sealed class Client(
        AntigravityBackend backend,
        EffectiveModelRuntime runtime,
        CancellationToken stopping,
        AntigravityAccount account) : IChatClient
    {
        private static readonly ModelSteeringPolicy SteeringPolicy = new(ModelSteeringMode.AtNextBoundary);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
            var request = new AntigravityRequest(runtime, messages, options, backend._models, backend._settings.AllowAiCredits);
            await backend.CheckReadyAsync(account, request, linked.Token);
            await foreach (var update in backend.SampleAsync(request, linked.Token))
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey is not null)
                return null;

            if (serviceType == typeof(ModelSteeringPolicy))
                return SteeringPolicy;

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose() { }
    }
}
