using System.Text.RegularExpressions;
using DotCraft.Agents;
using DotCraft.Configuration;

namespace DotCraft.Antigravity;

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
    string? configDirectory = null)
{
    public const string LoginHint =
        "Sign in to Antigravity from provider settings, or run `agy` (or `agy acp --login`) in your terminal, then try again.";

    public const string InstallHint =
        "Antigravity CLI (`agy`) was not found. Install it from https://antigravity.google/download or set executablePath in plugin settings.";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ProviderAuthenticationStatus> GetStatusAsync(CancellationToken ct)
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
            throw new ArgumentException("Antigravity.AuthMethod: Unknown authentication method.");

        var status = await GetStatusAsync(ct);
        if (status.FailureCode == "Antigravity.CliNotFound")
            throw new ProviderAuthenticationException("Antigravity.CliNotFound", InstallHint);

        if (!status.IsAuthenticated)
        {
            await SignInAsync(interaction, ct);
            status = await GetStatusAsync(ct);
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

        await using var process = new AntigravityProcess(executable, work, ["acp", "--login"], env);
        string? url = null;
        using (var linkWait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            linkWait.CancelAfter(TimeSpan.FromSeconds(30));
            while (url is null && await process.Output.ReadLineAsync(linkWait.Token) is { } line)
            {
                var clean = AnsiRegex().Replace(line, string.Empty);
                var match = LinkRegex().Match(clean);
                if (match.Success) url = match.Value;
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

        var last = string.Empty;
        while (true)
        {
            var code = await interaction.PromptAsync(new(
                "manual_code",
                "Paste the authorization code shown after signing in to Antigravity."), token);
            await process.Input.WriteLineAsync(code.Trim().AsMemory(), token);
            await process.Input.FlushAsync(token);

            if (await process.Output.ReadLineAsync(token) is not { } reply) break;
            last = AnsiRegex().Replace(reply, string.Empty).Trim();
            if (!last.Contains("Invalid code", StringComparison.OrdinalIgnoreCase)
                && !last.Contains("Try again", StringComparison.OrdinalIgnoreCase))
                break;

            await interaction.NotifyAsync(new(
                "info",
                "That authorization code was not accepted. Copy the complete code and paste it again."), token);
        }

        while (await process.Output.ReadLineAsync(token) is { } line)
        {
            var clean = AnsiRegex().Replace(line, string.Empty).Trim();
            if (clean.Length > 0) last = clean;
        }

        if (await process.ExitAsync(token) != 0)
        {
            var detail = last.Length > 0 ? last : process.StderrTail.Trim();
            throw new ProviderAuthenticationException(
                "Antigravity.LoginFailed",
                detail.Length > 0 ? detail : "Antigravity CLI could not complete sign-in.");
        }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
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
        if (exitCode != 0 && !AuthPromptRegex().IsMatch(stderr))
            throw new InvalidOperationException("Antigravity.LogoutFailed: Antigravity CLI could not sign out.");
    }

    public async Task<ModelCatalogResult> FetchModelsAsync(EffectiveModelRuntime runtime, CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
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
