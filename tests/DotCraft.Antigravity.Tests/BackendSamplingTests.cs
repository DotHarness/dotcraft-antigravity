using System.Text.Json;
using System.Text.Json.Nodes;
using DotCraft.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendSamplingTests
{
    [Fact]
    public async Task ParallelToolsReportWindowOccupancySeparatelyFromPromptBilling()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var invoked = 0;
        var options = Options(() => invoked++);
        var history = new List<ChatMessage> { new(ChatRole.User, "parallel context-metrics") };

        var (first, firstUsage) = await SampleAsync();

        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        AssertContextOnly(Assert.Single(firstUsage), 70);
        var message = Assert.Single(first.Messages);
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("Checking.", first.Text);
        var pid = Assert.Single(fixture.ChatPids);

        var callOne = calls.Single(c => c.Name == "Echo.One");
        var callTwo = calls.Single(c => c.Name == "Echo-Two");
        Assert.Equal("first", Assert.IsType<JsonElement>(callOne.Arguments!["value"]).GetString());
        Assert.Equal("second", Assert.IsType<JsonElement>(callTwo.Arguments!["value"]).GetString());

        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool,
        [
            new FunctionResultContent(callTwo.CallId, "B-result"),
            new FunctionResultContent(callOne.CallId, new AIContent[]
            {
                new TextContent("A-result"),
                new DataContent(new byte[] { 1, 2 }, "image/png")
            })
        ]));

        var (final, finalUsage) = await SampleAsync();

        Assert.Collection(finalUsage, context => AssertContextOnly(context, 115), billed =>
        {
            Assert.True(ProviderContextUsage.IsAttached(billed));
            Assert.Null(ProviderContextUsage.TryGet(billed));
            Assert.Equal(160, billed.Details.InputTokenCount);
            Assert.Equal(25, billed.Details.OutputTokenCount);
        });
        Assert.Equal("A-result [image/png:2]|B-result", final.Text);
        Assert.Equal(Assert.IsType<string>(first.ResponseId), final.ResponseId);
        Assert.Equal(ChatFinishReason.Stop, final.FinishReason);
        Assert.Equal(0, invoked);
        Assert.Single(fixture.ChatPids);
        Assert.True(BackendFixture.IsAlive(pid));

        history.AddRange(final.Messages);
        history.Add(new(ChatRole.User, "next question"));

        var next = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("完成 ✓", next.Text);
        Assert.Equal(pid, Assert.Single(fixture.ChatPids));
        Assert.Equal(115, next.Usage!.InputTokenCount);
        Assert.Equal(15, next.Usage.OutputTokenCount);
        Assert.Equal(90, next.Usage.CachedInputTokenCount);
        Assert.Equal("next question", Assert.Single(fixture.Files($"prompt-{pid}-2.json"))
            .GetProperty("input").GetProperty("message").GetProperty("content").GetString());

        async Task<(ChatResponse Response, UsageContent[] Usage)> SampleAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in fixture.Client.GetStreamingResponseAsync(history, options, timeout.Token))
                updates.Add(update);
            return (updates.ToChatResponse(), updates.SelectMany(update => update.Contents).OfType<UsageContent>().ToArray());
        }

        static void AssertContextOnly(UsageContent usage, long occupied)
        {
            var measurement = Assert.IsType<ProviderContextUsage>(ProviderContextUsage.TryGet(usage));
            Assert.Equal(occupied, measurement.OccupiedTokens);
            Assert.Null(measurement.InputTokens);
            Assert.Equal(0L, usage.Details.InputTokenCount.GetValueOrDefault());
            Assert.Equal(0L, usage.Details.OutputTokenCount.GetValueOrDefault());
        }
    }

    [Fact]
    public async Task ConsecutiveToolRoundsStayOnTheSameWorkerAndRestartDoesNotReuseIds()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var options = Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "two-rounds parallel") };

        var first = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        var firstCalls = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToArray();
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool,
        [
            new FunctionResultContent(firstCalls.Single(c => c.Name == "Echo.One").CallId, "A"),
            new FunctionResultContent(firstCalls.Single(c => c.Name == "Echo-Two").CallId, "B")
        ]));

        var second = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ChatFinishReason.ToolCalls, second.FinishReason);
        var thirdCall = Assert.Single(second.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("Echo.One", thirdCall.Name);

        history.AddRange(second.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent(thirdCall.CallId, "C")]));

        var final = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("A|B|C", final.Text);
        var firstPid = Assert.Single(fixture.ChatPids);

        await fixture.Backend.RetireIdleNowAsync();
        history.AddRange(final.Messages);
        history.Add(new(ChatRole.User, "parallel wrapped-legacy"));
        var resumed = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        var resumedCalls = resumed.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToArray();

        var capture = Assert.Single(fixture.Files("capture-*.json"), c => c.GetProperty("pid").GetInt32() != firstPid);
        Assert.Equal(first.ResponseId, capture.GetProperty("resume").GetString());
        Assert.Equal(1, capture.GetProperty("mcpServerCount").GetInt32());
        Assert.Equal("parallel wrapped-legacy", capture.GetProperty("input").GetProperty("message").GetProperty("content").GetString());
        Assert.NotEmpty(resumedCalls);
        Assert.Empty(firstCalls.Append(thirdCall).Select(c => c.CallId).Intersect(resumedCalls.Select(c => c.CallId)));
    }

    [Fact]
    public async Task IsolatedRequestsCloseCliAndCleanRunDirectories()
    {
        await using var fixture = new BackendFixture();

        var response = await fixture.Client.GetResponseAsync([new(ChatRole.User, "hello")]).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("完成 ✓", response.Text);
        Assert.False(BackendFixture.IsAlive(Assert.Single(fixture.ChatPids)));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
    }

    [Fact]
    public async Task ColdStartWritesRelayAgentPermissionsIsolatedEnvAndNestedToolSchemas()
    {
        Environment.SetEnvironmentVariable("FAKE_HOST_API_KEY", "should-never-reach-agy");
        Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://proxy.example:8080");
        Environment.SetEnvironmentVariable("NO_PROXY", "service.example.invalid");
        try
        {
            await using var fixture = new BackendFixture(allowAiCredits: true);
            var schema = JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"value":{"anyOf":[{"type":"string"},{"$ref":"#/$defs/Item"}]}},
                 "$defs":{"Item":{"type":"object","properties":{"nested":{"type":"boolean"}}}}}
                """);
            var options = new ChatOptions
            {
                Instructions = "dotcraft instructions",
                Tools = [AIFunctionFactory.CreateDeclaration("Inspect", "Inspect a value", schema)],
                ToolMode = ChatToolMode.RequireSpecific("Inspect"),
                ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"title":{"type":"string"}}}"""))
            };
            ChatMessage[] history =
            [
                new(ChatRole.System, "workspace instructions"),
                new(new ChatRole("developer"), "developer instructions"),
                new(ChatRole.User, "earlier user"),
                new(ChatRole.Assistant, [new FunctionCallContent("prior", "Inspect", new Dictionary<string, object?> { ["value"] = "old" })]),
                new(ChatRole.Tool, [new FunctionResultContent("prior", "earlier result")]),
                new(ChatRole.User, "current user"),
                new(ChatRole.User, "current guidance")
            ];

            await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

            var capture = fixture.Capture;
            var agentMd = capture.GetProperty("agentMarkdown").GetString()!;
            Assert.Contains("excludeDefaultComponents: true", agentMd);
            Assert.Contains("tools: []", agentMd);
            Assert.Contains("dotcraft instructions", agentMd);
            Assert.Contains("workspace instructions", agentMd);
            Assert.Contains("developer instructions", agentMd);
            Assert.Contains("You must call the Inspect tool", agentMd);

            var settings = capture.GetProperty("settings");
            Assert.True(settings.GetProperty("useG1Credits").GetBoolean());
            Assert.Contains("mcp(dotcraft/*)", settings.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains("command(*)", settings.GetProperty("permissions").GetProperty("deny").EnumerateArray().Select(e => e.GetString()));

            Assert.Equal("true", capture.GetProperty("disableAutoUpdate").GetString());
            Assert.Equal(JsonValueKind.Null, capture.GetProperty("leakedSecret").ValueKind);

            var inputText = capture.GetProperty("input").GetProperty("message").GetProperty("content").GetString()!;
            var currentMarker = inputText.IndexOf("Current user request:\n", StringComparison.Ordinal);
            Assert.True(currentMarker > 0);
            var historySection = inputText[..currentMarker];
            var currentSection = inputText[currentMarker..];
            Assert.Contains("earlier user", historySection);
            Assert.Contains("earlier result", historySection);
            Assert.DoesNotContain("current user", historySection);
            Assert.Contains("current user", currentSection);
            Assert.Contains("current guidance", currentSection);

            var advertised = Assert.Single(fixture.Files("tools-*.json")).GetProperty("result").GetProperty("tools")[0].GetProperty("inputSchema");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(schema.GetRawText()), JsonNode.Parse(advertised.GetRawText())));

            var args = capture.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
            Assert.Equal("acp", args[0]);
            Assert.Contains("--process-per-session", args);
            Assert.Equal(1, capture.GetProperty("mcpServerCount").GetInt32());
            Assert.False(File.Exists(Path.Combine(capture.GetProperty("cwd").GetString()!, ".agents", "mcp_config.json")));
            Assert.Equal("dotcraft-relay", args[Array.IndexOf(args, "--agent") + 1]);
            Assert.DoesNotContain("--dangerously-skip-permissions", args);
            var childEnv = AntigravityEnvironment.BuildChildEnvironment(fixture.Root);
            Assert.Contains("127.0.0.1", childEnv["NO_PROXY"].Split(','));
            Assert.Contains("localhost", childEnv["NO_PROXY"].Split(','));
            Assert.Contains("service.example.invalid", childEnv["NO_PROXY"].Split(','));

            var emptyEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Client.GetResponseAsync([new(ChatRole.User, "empty-success")]).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("Antigravity.EmptyResponse", emptyEx.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_HOST_API_KEY", null);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
            Environment.SetEnvironmentVariable("NO_PROXY", null);
        }
    }

    [Fact]
    public async Task MediaAttachmentsMaterializeAuthorizeReadFileAndRespawnOnNewMedia()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("hello")])
        };

        var first = await fixture.Client.GetResponseAsync(history).WaitAsync(TimeSpan.FromSeconds(15));
        var firstPid = Assert.Single(fixture.ChatPids);

        history.AddRange(first.Messages);
        history.Add(new(ChatRole.User,
        [
            new TextContent("media-read inspect this"),
            new DataContent(new byte[] { 1, 2, 3, 4 }, "image/png")
        ]));

        var second = await fixture.Client.GetResponseAsync(history).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("saw-image:4", second.Text);
        Assert.Equal(2, fixture.ChatPids.Length);

        var respawned = fixture.Files("capture-*.json").Single(c => c.GetProperty("pid").GetInt32() != firstPid);
        Assert.Equal(first.ResponseId, respawned.GetProperty("resume").GetString());
        Assert.Equal(first.ResponseId, second.ResponseId);
        var allowRules = respawned.GetProperty("settings").GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains(allowRules, rule => rule.StartsWith("read_file(", StringComparison.Ordinal) && rule.EndsWith(".png)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedUnauthorizedNativeToolRecoversOnSameSessionWithDotCraftTools()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var options = Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "native-tool-recover") };

        var first = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        var call = Assert.Single(first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        var pid = Assert.Single(fixture.ChatPids);

        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent(call.CallId, "ok-from-mcp")]));

        var final = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ChatFinishReason.Stop, final.FinishReason);
        Assert.Equal("recovered:ok-from-mcp", final.Text);
        Assert.Equal(pid, Assert.Single(fixture.ChatPids));
    }

    [Theory]
    [InlineData("native-tool")]
    [InlineData("native-tool-completed")]
    public async Task UnauthorizedNativeToolStepFailsClosedAndKillsWorker(string prompt)
    {
        await using var fixture = new BackendFixture();

        var ex = await Assert.ThrowsAsync<NativeToolBlockedException>(() =>
            fixture.Client.GetResponseAsync([new(ChatRole.User, prompt)]).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Contains("Antigravity.NativeToolBlocked", ex.Message);
        Assert.False(BackendFixture.IsAlive(Assert.Single(fixture.ChatPids)));
    }

    [Theory]
    [InlineData("foreign-mcp")]
    [InlineData("foreign-mcp-wrapped")]
    public async Task CallMcpToolTargetingForeignServerFailsClosed(string prompt)
    {
        await using var fixture = new BackendFixture();

        var ex = await Assert.ThrowsAsync<NativeToolBlockedException>(() =>
            fixture.Client.GetResponseAsync([new(ChatRole.User, prompt)], Options()).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Contains("Antigravity.NativeToolBlocked", ex.Message);
        Assert.False(BackendFixture.IsAlive(Assert.Single(fixture.ChatPids)));
    }

    [Theory]
    [InlineData("gemini-4-neon", false, "low")]
    [InlineData("claude-sonnet-4-6", true, null)]
    public async Task AcpEffortHonorsDisabledReasoningAndFixedEffortModels(
        string modelId, bool reasoningEnabled, string? expectedEffort)
    {
        await using var fixture = new BackendFixture();
        var runtime = fixture.Runtime with { Model = modelId };
        using var pipeline = ProviderPipelineOptionsScope.Push(new(runtime, "High", null, reasoningEnabled, "standard", false, null));
        using var client = fixture.Provider.CreateChatClient(runtime);

        await client.GetResponseAsync([new(ChatRole.User, "hello")]).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(modelId, fixture.Capture.GetProperty("model").GetString());
        Assert.Equal(expectedEffort, fixture.Capture.GetProperty("effort").GetString());
    }

    [Theory]
    [InlineData("history")]
    [InlineData("instructions")]
    [InlineData("window")]
    [InlineData("thread")]
    [InlineData("model")]
    [InlineData("tool-schema")]
    public async Task ChangedConversationRebuildsFromCompleteHostHistory(string change)
    {
        await using var fixture = new BackendFixture();
        using var originalScope = BackendFixture.Conversation();
        var options = Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "parallel") };
        var first = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        var calls = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToArray();
        var oldPid = Assert.Single(fixture.ChatPids);

        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool,
        [
            new FunctionResultContent(calls[0].CallId, "one"),
            new FunctionResultContent(calls[1].CallId, "two")
        ]));

        if (change == "history") history[0] = new(ChatRole.User, "changed parallel");
        if (change == "instructions") options.Instructions = "new instructions";
        if (change == "tool-schema") options.Tools!.Add(AIFunctionFactory.Create(() => "new tool", "Extra"));
        using var client = change == "model"
            ? fixture.Provider.CreateChatClient(fixture.Runtime with { Model = "claude-sonnet-4-6" })
            : null;
        var activeClient = client ?? fixture.Client;
        using var nextScope = BackendFixture.Conversation(
            change == "thread" ? "fork" : "thread",
            change == "window" ? "new-window" : "window");

        var response = await activeClient.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("完成 ✓", response.Text);
        Assert.Equal(2, fixture.ChatPids.Length);
        if (change is not ("thread" or "window")) Assert.False(BackendFixture.IsAlive(oldPid));
        var newest = fixture.Files("capture-*.json").Single(c => c.GetProperty("pid").GetInt32() != oldPid);
        var input = newest.GetProperty("input").GetRawText();
        Assert.Contains(calls[0].CallId, input);
        Assert.Contains("one", input);
    }

    internal static ChatOptions Options(Action? invoked = null) => new()
    {
        Tools =
        [
            AIFunctionFactory.Create((string value) => { invoked?.Invoke(); return value; }, "Echo.One"),
            AIFunctionFactory.Create((string value) => { invoked?.Invoke(); return value; }, "Echo-Two")
        ]
    };
}
