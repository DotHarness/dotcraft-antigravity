using System.Text.Json;
using DotCraft.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendResumeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    [InlineData("different-id")]
    public async Task RestartResumesByIdAndOnlySendsNewInputOrReplaysAfterResumeFailure(string? failure)
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var options = BackendSamplingTests.Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "hello") };
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "antigravity-oauth-token"), "fake-test-credential");

        var first = await fixture.Client.GetResponseAsync(history, options);
        var cold = fixture.Capture;
        var firstPid = cold.GetProperty("pid").GetInt32();
        var work = cold.GetProperty("cwd").GetString()!;
        var home = cold.GetProperty("home").GetString()!;
        await fixture.RestartAsync();

        Assert.False(BackendFixture.IsAlive(firstPid));
        Assert.False(File.Exists(Path.Combine(work, ".agents", "mcp_config.json")));
        Assert.False(File.Exists(Path.Combine(home, ".gemini", "antigravity-cli", "antigravity-oauth-token")));
        var nativeSession = Path.Combine(home, ".gemini", "antigravity-cli", "fake-native-session");
        if (failure == "missing")
            File.Delete(nativeSession);
        if (failure == "different-id")
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-resume-new-id"), "");
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.User, "next question"));

        var response = await fixture.Client.GetResponseAsync(history, options);

        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        var next = SubsequentCaptures(fixture, firstPid);
        var resume = Assert.Single(next, c => Args(c).Contains("--conversation"));
        var args = Args(resume);
        Assert.Equal($"conv-{firstPid}", args[Array.IndexOf(args, "--conversation") + 1]);
        Assert.Equal(work, resume.GetProperty("cwd").GetString());
        Assert.Equal("next question", Input(resume));
        if (failure is null)
            Assert.Single(next);
        else
            Assert.Contains("Conversation history", Input(Assert.Single(next, c => !Args(c).Contains("--conversation"))));
    }

    [Theory]
    [InlineData(false, "gemini-4-neon", "ExtraHigh", "gemini-4-neon-xhigh", "xhigh")]
    [InlineData(true, "gemini-3.8-flash", "Medium", "gemini-3.8-flash-medium", "medium")]
    public async Task ModelOrEffortChangeResumesConversationAndSendsOnlyNewInput(
        bool restartHost, string model, string effort, string expectedModel, string expectedEffort)
    {
        await using var fixture = new BackendFixture(idleTimeoutSeconds: 120);
        using var scope = BackendFixture.Conversation();
        var history = new List<ChatMessage> { new(ChatRole.User, "first question") };

        var first = await SampleAsync("gemini-4-neon", "High");
        var originalPid = Assert.Single(fixture.ChatPids);
        var conversationId = Assert.IsType<string>(first.ResponseId);
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.User, "next question"));

        if (restartHost)
            await fixture.RestartAsync();

        var next = await SampleAsync(model, effort);

        var capture = Assert.Single(SubsequentCaptures(fixture, originalPid));
        var args = Args(capture);
        Assert.Contains("--conversation", args);
        Assert.Equal(conversationId, args[Array.IndexOf(args, "--conversation") + 1]);
        Assert.Equal(conversationId, next.ResponseId);
        Assert.Equal(expectedModel, args[Array.IndexOf(args, "--model") + 1]);
        Assert.Equal(expectedEffort, args[Array.IndexOf(args, "--effort") + 1]);
        Assert.Equal("next question", Input(capture));

        async Task<ChatResponse> SampleAsync(string modelId, string reasoningEffort)
        {
            var runtime = fixture.Runtime with { Model = modelId };
            using var pipeline = ProviderPipelineOptionsScope.Push(new(runtime, reasoningEffort, null, true, "standard", false, null));
            using var client = fixture.Provider.CreateChatClient(runtime);
            return await client.GetResponseAsync(history).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Theory]
    [InlineData("history")]
    [InlineData("window")]
    [InlineData("instructions")]
    [InlineData("subagent")]
    [InlineData("logout")]
    [InlineData("corrupt")]
    public async Task RestartDoesNotResumeAnIncompatibleOrInvalidatedConversation(string change)
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var history = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var first = await fixture.Client.GetResponseAsync(history);
        var firstPid = Assert.Single(fixture.ChatPids);
        await fixture.RestartAsync();

        history.AddRange(first.Messages);
        history.Add(new(ChatRole.User, "next question"));
        if (change == "history")
            history[0] = new(ChatRole.User, "edited user message");
        if (change == "corrupt")
        {
            var directory = Assert.Single(Directory.GetDirectories(fixture.Root, "run-*"));
            await File.WriteAllTextAsync(Path.Combine(directory, AntigravitySessions.CheckpointFile), "{");
        }
        if (change == "logout")
        {
            await Assert.IsAssignableFrom<IProviderAuthenticator>(fixture.Provider).LogoutAsync(default);
            Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
            File.Delete(Path.Combine(fixture.Root, "fake-account.json"));
        }

        using var changedScope = change switch
        {
            "window" => BackendFixture.Conversation(window: "compacted"),
            "subagent" => ProviderRequestContextScope.Push(new(new(
                "child", "thread", "thread", null, "turn", "window", ProviderRequestKind.Turn, 0, "subagent", "thread_spawn"))),
            _ => null
        };
        var response = await fixture.Client.GetResponseAsync(history,
            change == "instructions" ? new ChatOptions { Instructions = "different instructions" } : null);

        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        var next = Assert.Single(SubsequentCaptures(fixture, firstPid));
        Assert.DoesNotContain("--conversation", Args(next));
        Assert.Contains("Conversation history", Input(next));
    }

    [Fact]
    public async Task RestartDoesNotSaveAnUnansweredToolCall()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var first = await fixture.Client.GetResponseAsync([new(ChatRole.User, "parallel")], BackendSamplingTests.Options());
        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);

        await fixture.RestartAsync();

        Assert.False(BackendFixture.IsAlive(Assert.Single(fixture.ChatPids)));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
    }

    private static JsonElement[] SubsequentCaptures(BackendFixture fixture, int firstPid) =>
        fixture.Files("capture-*.json").Where(c => c.GetProperty("pid").GetInt32() != firstPid).ToArray();

    private static string?[] Args(JsonElement capture) =>
        capture.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();

    private static string Input(JsonElement capture) =>
        capture.GetProperty("input").GetProperty("message").GetProperty("content").GetString()!;
}
