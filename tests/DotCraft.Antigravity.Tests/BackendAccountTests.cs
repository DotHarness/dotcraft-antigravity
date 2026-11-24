using DotCraft.Agents;
using DotCraft.Configuration;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendAccountTests
{
    [Fact]
    public async Task SignInReusesSignedInCliAccount()
    {
        await using var fixture = new BackendFixture();
        Assert.Equal("cli", Assert.Single(fixture.Backend.Descriptor.AuthenticationMethods).Kind);
        var auth = Assert.IsAssignableFrom<IProviderAuthenticator>(fixture.Provider);
        var interaction = new Interaction();

        var status = await auth.LoginAsync("cli", interaction, default);

        Assert.True(status.IsAuthenticated);
        Assert.Empty(interaction.Prompts);
        Assert.Empty(fixture.Files("login-*.json"));
    }

    [Fact]
    public async Task SignInRunsAcpLoginThroughInteractionAndLogoutSignsOut()
    {
        await using var fixture = new BackendFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-account.json"), "{\"authenticated\":false}");
        var auth = Assert.IsAssignableFrom<IProviderAuthenticator>(fixture.Provider);
        var interaction = new Interaction("wrong-code", "good-code");

        var status = await auth.LoginAsync("cli", interaction, default);

        Assert.True(status.IsAuthenticated);
        Assert.Equal("https://accounts.google.com/o/oauth2/auth?client=agy", Assert.Single(interaction.Events, e => e.Kind == "auth_url").Url);
        Assert.Single(interaction.Events, e => e.Kind == "info");
        Assert.Equal(["manual_code", "manual_code"], interaction.Prompts.Select(p => p.Kind));
        Assert.Equal("dotcraft-no-browser", Assert.Single(fixture.Files("login-*.json")).GetProperty("browser").GetString());

        await auth.LogoutAsync(default);
        Assert.False((await auth.GetStatusAsync(default)).IsAuthenticated);
    }

    [Fact]
    public async Task SignedOutCliCannotListModelsOrStartInference()
    {
        await using var fixture = new BackendFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-account.json"), "{\"authenticated\":false}");
        var catalog = Assert.IsAssignableFrom<IModelCatalogProvider>(fixture.Provider);

        var result = await catalog.FetchModelsAsync(fixture.Runtime, default);
        var ex = await Assert.ThrowsAsync<ProviderFailureException>(() => fixture.Client.GetResponseAsync([new(ChatRole.User, "hello")]));

        Assert.False(result.Success);
        Assert.Equal(ModelCatalogErrorCode.Unauthorized, result.ErrorCode);
        Assert.Equal("Antigravity.LoginRequired", ex.Failure.ProviderErrorCode);
        Assert.Empty(fixture.ChatPids);
    }

    [Fact]
    public async Task MissingCliExecutableReportsCliNotFound()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotcraft-agy-missing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var backend = new AntigravityBackend(new AntigravitySettings(Path.Combine(root, "no-such-agy.exe")), root);
            var provider = backend.CreateProvider(new ModelBackendContext(
                new EffectiveModelRuntime("ag", "gemini-4-neon", "antigravity", "Antigravity", "", "", 30, null, new()),
                new DummyStore(),
                default));
            var auth = Assert.IsAssignableFrom<IProviderAuthenticator>(provider);

            var status = await auth.GetStatusAsync(default);
            Assert.False(status.IsAuthenticated);
            Assert.Equal("Antigravity.CliNotFound", status.FailureCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ModelDiscoveryGroupsEffortsAndProjectsRuntimeMetadata()
    {
        await using var fixture = new BackendFixture();
        var catalog = Assert.IsAssignableFrom<IModelCatalogProvider>(fixture.Provider);
        var resolver = Assert.IsAssignableFrom<IProviderModelRuntimeResolver>(fixture.Provider);

        var result = await catalog.FetchModelsAsync(fixture.Runtime, default);

        Assert.True(result.Success);
        Assert.Equal(["gemini-4-neon", "gemini-3.8-flash", "claude-sonnet-4-6", "gpt-oss-120b"], result.Models.Select(m => m.Id));

        var neon = result.Models[0];
        Assert.Equal("Gemini 4 Neon", neon.DisplayName);
        Assert.Equal("Google", neon.OwnedBy);
        Assert.Equal(
            [ProviderReasoningEffort.Low, ProviderReasoningEffort.Medium, ProviderReasoningEffort.High, ProviderReasoningEffort.ExtraHigh],
            neon.Reasoning!.SupportedEfforts);
        Assert.Equal(AntigravityModels.GeminiContextWindow, neon.ContextWindow);

        var sonnet = result.Models[2];
        Assert.Equal("Anthropic", sonnet.OwnedBy);
        Assert.Null(sonnet.Reasoning);
        Assert.Equal(AntigravityModels.ClaudeContextWindow, sonnet.ContextWindow);

        var gptOss = result.Models[3];
        Assert.Equal(AntigravityModels.GptOssContextWindow, gptOss.ContextWindow);

        var resolved = resolver.ResolveRuntime(fixture.Runtime with { Model = "claude-sonnet-4-6" });
        Assert.Equal(AntigravityModels.ClaudeContextWindow, resolved.ModelMetadata!.ContextWindow);
        Assert.Contains("image", resolved.ModelMetadata.InputModalities!);
    }

    [Fact]
    public async Task SignInCompletesWhenAcpLoginFinishesViaBrowserCallback()
    {
        await using var fixture = new BackendFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-account.json"), "{\"authenticated\":false}");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-login-callback"), "1");
        var auth = Assert.IsAssignableFrom<IProviderAuthenticator>(fixture.Provider);
        var interaction = new HangingInteraction();

        var status = await auth.LoginAsync("cli", interaction, default);

        Assert.True(status.IsAuthenticated);
        Assert.Equal("https://accounts.google.com/o/oauth2/auth?client=agy&mode=callback", Assert.Single(interaction.Events, e => e.Kind == "auth_url").Url);
    }

    [Fact]
    public async Task AuthenticatedStatusIsCachedAcrossProbesUntilLogout()
    {
        await using var fixture = new BackendFixture();
        var auth = Assert.IsAssignableFrom<IProviderAuthenticator>(fixture.Provider);

        var first = await auth.GetStatusAsync(default);
        var second = await auth.GetStatusAsync(default);

        Assert.True(first.IsAuthenticated);
        Assert.True(second.IsAuthenticated);
        Assert.Single(fixture.Files("started-*.json"));

        await auth.LogoutAsync(default);
        Assert.False((await auth.GetStatusAsync(default)).IsAuthenticated);
    }

    [Fact]
    public async Task UsageParsesQuotaPoolsPlanAndCreditsFromCliJson()
    {
        await using var fixture = new BackendFixture();
        var reader = Assert.IsAssignableFrom<IProviderUsageReader>(fixture.Provider);

        var snapshot = await reader.ReadAsync(default);

        Assert.NotNull(snapshot);
        Assert.Equal("Google AI Pro", snapshot!.PlanType);
        Assert.Equal("420", snapshot.Credits!.Balance);
        Assert.Equal("gemini_5h,gemini_weekly,claude_5h,claude_weekly", snapshot.Labels!["pool_order"]);
        Assert.Equal(80.0, snapshot.Values["pool.gemini_5h.remaining"]);
        Assert.Equal(35.0, snapshot.Values["pool.claude_5h.remaining"]);
        Assert.Equal("Gemini (5h)", snapshot.Labels["pool.gemini_5h.name"]);
        Assert.Equal("Claude / GPT (5h)", snapshot.Labels["pool.claude_5h.name"]);
        Assert.Equal(65, snapshot.Primary!.UsedPercent);
        Assert.Equal(TimeSpan.FromHours(5), snapshot.Primary.WindowDuration);
        Assert.Equal(58, snapshot.Secondary!.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), snapshot.Secondary.WindowDuration);

        var processCountAfterFirstRead = fixture.Files("started-*.json").Length;
        await using var secondBackend = new AntigravityBackend(
            new AntigravitySettings(BackendFixture.Executable),
            Path.Combine(fixture.Root, "other-workspace"),
            configDirectory: fixture.Root);
        var secondProvider = secondBackend.CreateProvider(new ModelBackendContext(fixture.Runtime, new DummyStore(), default));
        var cachedFromDisk = await Assert.IsAssignableFrom<IProviderUsageReader>(secondProvider).ReadAsync(default);

        Assert.NotNull(cachedFromDisk);
        Assert.Equal(snapshot.Primary.UsedPercent, cachedFromDisk!.Primary!.UsedPercent);
        Assert.Equal(processCountAfterFirstRead, fixture.Files("started-*.json").Length);
    }

    [Fact]
    public async Task QuotaCreditsValidationAndEarlyLogWatchdogBlockSubsequentRequests()
    {
        await using var quotaFixture = new BackendFixture();
        var quotaEx = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            quotaFixture.Client.GetResponseAsync([new(ChatRole.User, "quota-error")]).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("Antigravity.QuotaReached", quotaEx.Failure.ProviderErrorCode);
        Assert.Equal("quota_reached", (await Assert.IsAssignableFrom<IProviderUsageReader>(quotaFixture.Provider).ReadAsync(default))?.LimitReachedKind);

        await using var creditsFixture = new BackendFixture();
        var creditsEx = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            creditsFixture.Client.GetResponseAsync([new(ChatRole.User, "credits-error")]).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("Antigravity.CreditsExhausted", creditsEx.Failure.ProviderErrorCode);

        await using var valFixture = new BackendFixture();
        var valEx = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            valFixture.Client.GetResponseAsync([new(ChatRole.User, "validation-error")]).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("Antigravity.ValidationRequired", valEx.Failure.ProviderErrorCode);
        var valUsage = await Assert.IsAssignableFrom<IProviderUsageReader>(valFixture.Provider).ReadAsync(default);
        Assert.Equal("validation_required", valUsage?.LimitReachedKind);
        Assert.Equal("https://accounts.google.com/verify?session=xyz", valUsage?.Labels?["validation_url"]);

        await using var logFixture = new BackendFixture();
        var earlyEx = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            logFixture.Client.GetResponseAsync([new(ChatRole.User, "early-log-quota")]).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("Antigravity.QuotaReached", earlyEx.Failure.ProviderErrorCode);
        Assert.False(BackendFixture.IsAlive(Assert.Single(logFixture.ChatPids)));
    }

    private sealed class Interaction(params string[] answers) : IProviderLoginInteraction
    {
        private readonly Queue<string> _answers = new(answers);
        public List<ProviderLoginPrompt> Prompts { get; } = [];
        public List<ProviderLoginEvent> Events { get; } = [];

        public ValueTask NotifyAsync(ProviderLoginEvent notification, CancellationToken cancellationToken)
        {
            Events.Add(notification);
            return ValueTask.CompletedTask;
        }

        public Task<string> PromptAsync(ProviderLoginPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return Task.FromResult(_answers.Dequeue());
        }
    }

    private sealed class HangingInteraction : IProviderLoginInteraction
    {
        public List<ProviderLoginEvent> Events { get; } = [];

        public ValueTask NotifyAsync(ProviderLoginEvent notification, CancellationToken cancellationToken)
        {
            Events.Add(notification);
            return ValueTask.CompletedTask;
        }

        public async Task<string> PromptAsync(ProviderLoginPrompt prompt, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return string.Empty;
        }
    }

    private sealed class DummyStore : IProviderCredentialStore
    {
        public Task<System.Text.Json.JsonElement?> ReadAsync(CancellationToken ct) => Task.FromResult<System.Text.Json.JsonElement?>(null);
        public Task<System.Text.Json.JsonElement?> ModifyAsync(Func<System.Text.Json.JsonElement?, CancellationToken, Task<System.Text.Json.JsonElement?>> update, CancellationToken ct) => Task.FromResult<System.Text.Json.JsonElement?>(null);
        public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
