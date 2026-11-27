using System.Text.Json;
using DotCraft.Agents;
using DotCraft.Contributions;
using DotCraft.Sessions;
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
        var resume = Assert.Single(fixture.Files("session-*.json"), c => c.GetProperty("resume").GetString() == first.ResponseId);
        Assert.Equal(work, resume.GetProperty("cwd").GetString());
        Assert.Equal(1, resume.GetProperty("mcpServerCount").GetInt32());
        var next = Assert.Single(SubsequentCaptures(fixture, firstPid));
        if (failure is null)
        {
            Assert.Equal(first.ResponseId, response.ResponseId);
            Assert.Equal("next question", Input(next));
        }
        else
        {
            Assert.NotEqual(first.ResponseId, response.ResponseId);
            Assert.Null(next.GetProperty("resume").GetString());
            Assert.Contains("Conversation history", Input(next));
        }
    }

    [Theory]
    [InlineData(false, "gemini-4-neon", "ExtraHigh", "gemini-4-neon", "xhigh")]
    [InlineData(true, "gemini-3.8-flash", "Medium", "gemini-3.8-flash", "medium")]
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

        var capture = restartHost
            ? Assert.Single(SubsequentCaptures(fixture, originalPid))
            : Assert.Single(fixture.Files($"prompt-{originalPid}-2.json"));
        if (restartHost)
            Assert.Equal(conversationId, capture.GetProperty("resume").GetString());
        else
            Assert.Equal(originalPid, Assert.Single(fixture.ChatPids));
        Assert.Equal(conversationId, next.ResponseId);
        Assert.Equal(expectedModel, capture.GetProperty("model").GetString());
        Assert.Equal(expectedEffort, capture.GetProperty("effort").GetString());
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedEffortDoesNotLoseTheNativeConversation(bool restartHost)
    {
        await using var fixture = new BackendFixture(idleTimeoutSeconds: 120);
        using var scope = BackendFixture.Conversation();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var history = new List<ChatMessage> { new(ChatRole.User, "first question") };

        var first = await SampleAsync("gemini-4-neon", "High");
        var originalId = Assert.IsType<string>(first.ResponseId);
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.User, "next question"));

        if (restartHost)
            await fixture.RestartAsync();

        var denial = Path.Combine(fixture.Root, "fake-reject-effort");
        await File.WriteAllTextAsync(denial, "", deadline.Token);
        var error = await Assert.ThrowsAsync<AntigravityAcpException>(() => SampleAsync("gemini-3.8-flash", "Medium"));
        Assert.Equal(-32602, error.Code);
        var rejected = Assert.Single(fixture.Files("rejected-effort-*.json"));
        Assert.Equal("gemini-3.8-flash", rejected.GetProperty("model").GetString());
        Assert.Single(fixture.Files("prompt-*.json"));
        File.Delete(denial);

        var next = await SampleAsync("gemini-4-neon", "High");

        Assert.Equal(originalId, next.ResponseId);
        Assert.Single(fixture.Files("prompt-*.json"), capture => Input(capture) == "next question");

        async Task<ChatResponse> SampleAsync(string modelId, string reasoningEffort)
        {
            var runtime = fixture.Runtime with { Model = modelId };
            using var pipeline = ProviderPipelineOptionsScope.Push(new(runtime, reasoningEffort, null, true, "standard", false, null));
            using var client = fixture.Provider.CreateChatClient(runtime);
            return await client.GetResponseAsync(history, cancellationToken: deadline.Token);
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
        Assert.Null(next.GetProperty("resume").GetString());
        Assert.Contains("Conversation history", Input(next));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopRetainsTheConversationAndHandsOffOnlyInterruptedProgress(bool waitingForTools, bool forceStop)
    {
        await using var fixture = new BackendFixture(idleTimeoutSeconds: 120);
        var options = BackendSamplingTests.Options();
        const string earlierEvidence = "earlier tool evidence";
        const string interruptedEvidence = "interrupted tool evidence";
        var history = new List<ChatMessage>();
        if (waitingForTools)
        {
            history.Add(new(ChatRole.User, "Record earlier context."));
            history.Add(new(ChatRole.Assistant, [new FunctionCallContent("earlier-call", "earlier_tool")]));
            history.Add(new(ChatRole.Tool, [new FunctionResultContent("earlier-call", new string('a', 20_000) + earlierEvidence)]));
        }
        history.Add(new(ChatRole.User, "Complete the first task."));
        string? conversationId;
        using (BackendFixture.Conversation(turn: "first"))
        {
            var first = await fixture.Client.GetResponseAsync(history, options);
            conversationId = first.ResponseId;
            history.AddRange(first.Messages);
        }

        var stoppedPrompt = waitingForTools ? "parallel" : "hang";
        history.Add(new(ChatRole.User, stoppedPrompt));
        using (BackendFixture.Conversation(turn: "stopped"))
        {
            if (waitingForTools)
            {
                var toolRound = await fixture.Client.GetResponseAsync(history, options);
                history.AddRange(toolRound.Messages);
                foreach (var call in toolRound.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
                    history.Add(new(ChatRole.Tool, [new FunctionResultContent(call.CallId, new string('b', 20_000) + interruptedEvidence)]));
            }
            else
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await foreach (var update in fixture.Client.GetStreamingResponseAsync(history, options, stop.Token))
                    {
                        if (update.Text == "ready")
                            await stop.CancelAsync();
                    }
                });
            }
        }

        var originalPid = Assert.Single(fixture.ChatPids);
        if (forceStop)
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-ignore-cancel"), "");
        if (waitingForTools)
            await fixture.Backend.OnTurnEndedAsync(new TurnLifecycleContext("thread", "stopped") { Status = TurnStatus.Cancelled });

        Assert.Equal(!forceStop, BackendFixture.IsAlive(originalPid));
        if (forceStop)
        {
            Assert.Equal(2, Assert.Single(fixture.Files($"cancel-ignored-{originalPid}.json")).GetProperty("pendingBefore").GetInt32());
        }
        else
        {
            var cancel = Assert.Single(fixture.Files($"cancel-{originalPid}-*.json"));
            Assert.Equal(waitingForTools ? 2 : 0, cancel.GetProperty("pendingBefore").GetInt32());
            Assert.Equal(0, cancel.GetProperty("pendingAfter").GetInt32());
        }

        history.Add(new(ChatRole.User, "<turn_aborted>The previous turn was interrupted.</turn_aborted>"));
        history.Add(new(ChatRole.User, "Continue with the remaining task."));
        using var nextTurn = BackendFixture.Conversation(turn: "next");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var next = await fixture.Client.GetResponseAsync(history, options, deadline.Token);

        Assert.Equal(conversationId, next.ResponseId);
        var capture = forceStop
            ? Assert.Single(SubsequentCaptures(fixture, originalPid))
            : Assert.Single(fixture.Files($"prompt-{originalPid}-3.json"));
        if (forceStop)
            Assert.Equal(conversationId, capture.GetProperty("resume").GetString());
        else
            Assert.Equal(originalPid, Assert.Single(fixture.ChatPids));
        var input = Input(capture);
        Assert.DoesNotContain(stoppedPrompt, input);
        Assert.Contains("<turn_aborted>", input);
        Assert.Contains("Continue with the remaining task.", input);
        if (waitingForTools)
        {
            Assert.Equal(2, fixture.Files("call-*.json").Length);
            Assert.Empty(fixture.Files("mcp-results-*.json"));
            var handoff = Path.Combine(capture.GetProperty("cwd").GetString()!, AntigravityHandoff.FileName);
            var evidence = await File.ReadAllTextAsync(handoff, deadline.Token);
            Assert.Contains(earlierEvidence, evidence);
            Assert.Contains(interruptedEvidence, evidence);
            Assert.DoesNotContain(interruptedEvidence, input);
        }
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

    private static string Input(JsonElement capture) =>
        capture.GetProperty("input").GetProperty("message").GetProperty("content").GetString()!;
}
