using System.Text.RegularExpressions;
using System.Threading.Channels;
using DotCraft.Agents;
using DotCraft.Configuration;

namespace DotCraft.Antigravity;

internal sealed class AntigravityAuthStatusCache
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private ProviderAuthenticationStatus? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public void Clear()
    {
        lock (_gate)
        {
            _cached = null;
            _cachedAt = DateTimeOffset.MinValue;
        }
    }

    public async Task<ProviderAuthenticationStatus> GetOrProbeAsync(
        bool forceRefresh,
        TimeProvider clock,
        Func<CancellationToken, Task<ProviderAuthenticationStatus>> probe,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!forceRefresh)
        {
            lock (_gate)
            {
                if (_cached is { IsAuthenticated: true } && now - _cachedAt < CacheTtl)
                    return _cached;
            }
        }

        await _probeGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh)
            {
                lock (_gate)
                {
                    if (_cached is { IsAuthenticated: true } && clock.GetUtcNow() - _cachedAt < CacheTtl)
                        return _cached;
                }
            }

            var result = await probe(ct);
            lock (_gate)
            {
                if (result.IsAuthenticated)
                {
                    _cached = result;
                    _cachedAt = clock.GetUtcNow();
                }
                else
                {
                    _cached = null;
                    _cachedAt = DateTimeOffset.MinValue;
                }
            }
            return result;
        }
        finally
        {
            _probeGate.Release();
        }
    }
}

internal sealed partial class AntigravityAccount(
    AntigravitySettings settings,
    string root,
    AntigravityModels models,
    AntigravityUsage usage,
    AntigravityLimits limits,
    CancellationToken backendStopping,
    CancellationToken providerStopping,
    Func<Task> stopRuns,
    TimeProvider? clock = null,
    string? configDirectory = null,
    AntigravityAuthStatusCache? authCache = null)
{
    public const string LoginHint =
        "Sign in to Antigravity from provider settings, or run `agy` (or `agy acp --login`) in your terminal, then try again.";

    public const string InstallHint =
        "Antigravity CLI (`agy`) was not found. Install it from https://antigravity.google/download or set executablePath in plugin settings.";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly AntigravityAuthStatusCache _authCache = authCache ?? new AntigravityAuthStatusCache();

    public Task<ProviderAuthenticationStatus> GetStatusAsync(CancellationToken ct, bool forceRefresh = false) =>
        _authCache.GetOrProbeAsync(forceRefresh, _clock, ProbeStatusAsync, ct);

    private async Task<ProviderAuthenticationStatus> ProbeStatusAsync(CancellationToken ct)
    {
        var (executable, found) = AntigravityEnvironment.ResolveExecutable(settings.ExecutablePath);
        if (!found)
        {
            return new(
                IsAuthenticated: false,
                FailureCode: "Antigravity.CliNotFound",
                FailureMessage: InstallHint);
        }

        using var timeout = Budget(ct, TimeSpan.FromSeconds(15));
        await using var dir = new PrivateDirectory(root, "probe-");
        var (isolatedHome, _) = await AntigravityEnvironment.PrepareIsolatedHomeAsync(
            dir, settings.AllowAiCredits, null, timeout.Token, configDirectory);
        var env = AntigravityEnvironment.BuildChildEnvironment(isolatedHome, explicitConfigDir: configDirectory);
        var work = dir.EnsureSubdirectory("work");

        try
        {
            await using var process = new AntigravityProcess(executable, work, ["models"], env);
            var (exitCode, stdout, stderr) = await process.ReadToExitAsync(timeout.Token);
            if (exitCode == 0)
            {
                var parsed = models.UpdateFromCliOutput(stdout, _clock.GetUtcNow());
                if (parsed.Entries.Count > 0 && parsed.Source == "cli")
                {
                    return new(
                        IsAuthenticated: true,
                        AccountId: "agy-cli",
                        DisplayName: "Antigravity CLI");
                }
            }

            var detail = $"{stderr}\n{stdout}".Trim();
            if (AuthPromptRegex().IsMatch(detail) || exitCode != 0)
            {
                return new(
                    IsAuthenticated: false,
                    FailureCode: "Antigravity.LoginRequired",
                    FailureMessage: LoginHint);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Antigravity.CliNotFound", StringComparison.Ordinal))
        {
            return new(
                IsAuthenticated: false,
                FailureCode: "Antigravity.CliNotFound",
                FailureMessage: InstallHint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(
                IsAuthenticated: false,
                FailureCode: "Antigravity.LoginRequired",
                FailureMessage: LoginHint);
        }

        return new(
            IsAuthenticated: false,
            FailureCode: "Antigravity.LoginRequired",
            FailureMessage: LoginHint);
    }

    public async Task<ProviderAuthenticationStatus> LoginAsync(
        string method,
        IProviderLoginInteraction interaction,
        CancellationToken ct)
    {
        if (!string.Equals(method, "cli", StringComparison.Ordinal))
            throw new ProviderAuthenticationException("Antigravity.AuthMethod", "Unknown authentication method.");

        var status = await GetStatusAsync(ct, forceRefresh: true);
        if (status.FailureCode == "Antigravity.CliNotFound")
            throw new ProviderAuthenticationException("Antigravity.CliNotFound", InstallHint);

        if (!status.IsAuthenticated)
        {
            await SignInAsync(interaction, ct);
            status = await GetStatusAsync(ct, forceRefresh: true);
            if (!status.IsAuthenticated)
                throw new ProviderAuthenticationException("Antigravity.LoginRequired", LoginHint);
        }

        limits.Clear();
        usage.ClearCache();
        await stopRuns();
        return status;
    }

    private async Task SignInAsync(IProviderLoginInteraction interaction, CancellationToken ct)
    {
        var (executable, found) = AntigravityEnvironment.ResolveExecutable(settings.ExecutablePath);
        if (!found)
            throw new ProviderAuthenticationException("Antigravity.CliNotFound", InstallHint);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, backendStopping, providerStopping);
        var token = stopping.Token;
        await using var dir = new PrivateDirectory(root, "login-");
        var work = dir.EnsureSubdirectory("work");

        // Run against the user's real HOME so `agy acp --login` writes credentials into the real CLI store/keyring.
        var overrides = new Dictionary<string, string> { ["BROWSER"] = "dotcraft-no-browser" };
        if (!string.IsNullOrWhiteSpace(configDirectory))
            overrides["ANTIGRAVITY_CONFIG_DIR"] = configDirectory;
        var env = AntigravityEnvironment.BuildChildEnvironment(isolatedHome: null, overrides, configDirectory);

        var urlFound = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void TryCaptureUrl(string rawLine)
        {
            var clean = AnsiRegex().Replace(rawLine, string.Empty);
            var match = LinkRegex().Match(clean);
            if (match.Success) urlFound.TrySetResult(match.Value);
        }

        AntigravityProcess process;
        try
        {
            process = new AntigravityProcess(executable, work, ["acp", "--login"], env, onStderrLine: TryCaptureUrl);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Antigravity.CliNotFound", StringComparison.Ordinal))
        {
            throw new ProviderAuthenticationException("Antigravity.CliNotFound", InstallHint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ProviderAuthenticationException(
                "Antigravity.LoginFailed",
                $"Could not start Antigravity CLI for sign-in: {ex.Message}");
        }

        await using (process)
        {
            var stdoutLines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
            var last = string.Empty;
            var stdoutPump = Task.Run(async () =>
            {
                try
                {
                    while (await process.Output.ReadLineAsync(token) is { } line)
                    {
                        var clean = AnsiRegex().Replace(line, string.Empty).Trim();
                        if (clean.Length > 0)
                        {
                            last = clean;
                            TryCaptureUrl(clean);
                        }
                        await stdoutLines.Writer.WriteAsync(clean, token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                }
                finally
                {
                    stdoutLines.Writer.TryComplete();
                }
            }, CancellationToken.None);

            var exitTask = process.ExitAsync(token);

            string? url = null;
            using (var linkWait = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                linkWait.CancelAfter(TimeSpan.FromSeconds(30));
                var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, linkWait.Token);
                var winner = await Task.WhenAny(urlFound.Task, exitTask, timeoutTask);
                if (winner == urlFound.Task)
                    url = await urlFound.Task;
                else if (winner == exitTask)
                {
                    var earlyExitCode = await exitTask;
                    await stdoutPump;
                    if (earlyExitCode == 0)
                        return;
                    var detail = last.Length > 0 ? last : process.StderrTail.Trim();
                    throw new ProviderAuthenticationException(
                        "Antigravity.LoginFailed",
                        detail.Length > 0 ? detail : "Antigravity CLI exited before completing sign-in.");
                }
                else
                {
                    token.ThrowIfCancellationRequested();
                }
            }

            if (url is null)
            {
                var stderrMatch = LinkRegex().Match(AnsiRegex().Replace(process.StderrTail, string.Empty));
                if (stderrMatch.Success) url = stderrMatch.Value;
            }

            if (url is null)
            {
                throw new ProviderAuthenticationException(
                    "Antigravity.LoginFailed",
                    "Antigravity CLI did not emit a sign-in URL. Run `agy` or `agy acp --login` in your terminal to sign in, then retry.");
            }

            await interaction.NotifyAsync(new(
                "auth_url",
                "Sign in to Google Antigravity in your browser, then paste the authorization code below.",
                url), token);

            while (stdoutLines.Reader.TryRead(out _)) { }

            while (!exitTask.IsCompleted)
            {
                string code;
                var promptExited = false;
                using (var promptCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var promptGate = new object();
                    var promptDisposed = false;
                    _ = exitTask.ContinueWith(_ =>
                    {
                        lock (promptGate)
                        {
                            if (!promptDisposed)
                                promptCts.Cancel();
                        }
                    }, TaskScheduler.Default);

                    try
                    {
                        code = await interaction.PromptAsync(new(
                            "manual_code",
                            "Paste the authorization code shown after signing in to Antigravity."), promptCts.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && exitTask.IsCompleted)
                    {
                        promptExited = true;
                        code = string.Empty;
                    }
                    finally
                    {
                        lock (promptGate) promptDisposed = true;
                    }
                }

                if (promptExited || exitTask.IsCompleted) break;

                try
                {
                    await process.Input.WriteLineAsync(code.Trim().AsMemory(), token);
                    await process.Input.FlushAsync(token);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    break;
                }

                var reply = await ReadNextNonEmptyLineOrExitAsync(stdoutLines.Reader, exitTask, token);
                if (reply is null) break;
                last = reply;
                if (!last.Contains("Invalid code", StringComparison.OrdinalIgnoreCase)
                    && !last.Contains("Try again", StringComparison.OrdinalIgnoreCase))
                    break;

                await interaction.NotifyAsync(new(
                    "info",
                    "That authorization code was not accepted. Copy the complete code and paste it again."), token);
            }

            var exitCode = await exitTask;
            await stdoutPump;
            if (exitCode != 0)
            {
                var detail = last.Length > 0 ? last : process.StderrTail.Trim();
                throw new ProviderAuthenticationException(
                    "Antigravity.LoginFailed",
                    detail.Length > 0 ? detail : "Antigravity CLI could not complete sign-in.");
            }
        }
    }

    private static async Task<string?> ReadNextNonEmptyLineOrExitAsync(
        ChannelReader<string> reader,
        Task<int> exitTask,
        CancellationToken ct)
    {
        while (true)
        {
            while (reader.TryRead(out var immediate))
            {
                if (immediate.Length > 0) return immediate;
            }

            var waitTask = reader.WaitToReadAsync(ct).AsTask();
            var completed = await Task.WhenAny(waitTask, exitTask);
            if (completed == waitTask)
            {
                if (!await waitTask) return null;
                continue;
            }

            while (reader.TryRead(out var drained))
            {
                if (drained.Length > 0) return drained;
            }
            return null;
        }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        _authCache.Clear();
        await stopRuns();
        limits.Clear();
        usage.ClearCache();
        models.Reset();

        var (executable, found) = AntigravityEnvironment.ResolveExecutable(settings.ExecutablePath);
        if (!found) return;

        using var timeout = Budget(ct, TimeSpan.FromSeconds(15));
        await using var dir = new PrivateDirectory(root, "logout-");
        var work = dir.EnsureSubdirectory("work");
        var overrides = !string.IsNullOrWhiteSpace(configDirectory)
            ? new Dictionary<string, string> { ["ANTIGRAVITY_CONFIG_DIR"] = configDirectory }
            : null;
        var env = AntigravityEnvironment.BuildChildEnvironment(isolatedHome: null, overrides, configDirectory);

        await using var process = new AntigravityProcess(executable, work, ["-p", "/logout", "--output-format", "json"], env);
        var (exitCode, _, stderr) = await process.ReadToExitAsync(timeout.Token);
        _authCache.Clear();
        if (exitCode != 0 && !AuthPromptRegex().IsMatch(stderr))
            throw new InvalidOperationException("Antigravity.LogoutFailed: Antigravity CLI could not sign out.");
    }

    public async Task<ModelCatalogResult> FetchModelsAsync(EffectiveModelRuntime runtime, CancellationToken ct)
    {
        var status = await GetStatusAsync(ct, forceRefresh: true);
        if (!status.IsAuthenticated)
        {
            return new ModelCatalogResult
            {
                Success = false,
                ErrorCode = ModelCatalogErrorCode.Unauthorized,
                ErrorMessage = status.FailureMessage ?? LoginHint,
                ProviderId = runtime.ProviderId,
                Protocol = runtime.Protocol,
                Models = models.Current.Entries.ToList(),
                Source = models.Current.Source
            };
        }

        var snapshot = models.Current;
        return new ModelCatalogResult
        {
            Success = true,
            ProviderId = runtime.ProviderId,
            Protocol = runtime.Protocol,
            Models = snapshot.Entries.ToList(),
            Source = snapshot.Source,
            RefreshedAt = snapshot.RefreshedAt
        };
    }

    public Task<ProviderUsageSnapshot?> ReadUsageAsync(CancellationToken ct) => usage.ReadAsync(ct);

    private CancellationTokenSource Budget(CancellationToken ct, TimeSpan timeout)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, backendStopping, providerStopping);
        linked.CancelAfter(timeout);
        return linked;
    }

    [GeneratedRegex(@"https://\S+")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"authentication required|not logged in|sign in|unauthorized|login required", RegexOptions.IgnoreCase)]
    private static partial Regex AuthPromptRegex();
}
