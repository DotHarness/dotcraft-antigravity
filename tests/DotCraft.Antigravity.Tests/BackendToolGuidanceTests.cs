using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendToolGuidanceTests
{
    [Fact]
    public async Task QueuedSteerUsesNativePromptWhileMcpResultsStaySeparate()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var options = BackendSamplingTests.Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "parallel two-rounds") };
        var first = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        var id = Assert.IsType<string>(first.ResponseId);
        var pid = Assert.Single(fixture.ChatPids);
        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);

        var firstCalls = first.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool,
        [
            new FunctionResultContent(firstCalls.Single(call => call.Name == "Echo.One").CallId,
                new AIContent[] { new TextContent("A"), new DataContent(new byte[] { 1, 2 }, "image/png") }),
            new FunctionResultContent(firstCalls.Single(call => call.Name == "Echo-Two").CallId, "B")
        ]));
        const string firstInput = "First queued input";
        var secondInput = "Second queued input\n继续：" + new string('x', 36_000);
        history.Add(new(ChatRole.User, firstInput));
        history.Add(new(ChatRole.User, secondInput));

        var second = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(id, second.ResponseId);
        Assert.Equal(ChatFinishReason.ToolCalls, second.FinishReason);
        Assert.Equal(60, second.Usage?.InputTokenCount);
        Assert.Equal(10, second.Usage?.OutputTokenCount);
        var nextCall = Assert.Single(second.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        history.AddRange(second.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent(nextCall.CallId, "C")
        {
            Exception = new InvalidOperationException("Synthetic tool failure")
        }]));

        var final = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(id, final.ResponseId);
        Assert.Equal(ChatFinishReason.Stop, final.FinishReason);
        Assert.Equal(100, final.Usage?.InputTokenCount);
        Assert.Equal(15, final.Usage?.OutputTokenCount);
        Assert.Equal(pid, Assert.Single(fixture.ChatPids));
        var results = Assert.Single(fixture.Files("mcp-results-*.json")).EnumerateArray().ToArray();
        Assert.Collection(results,
            result =>
            {
                Assert.Equal("A", Assert.Single(TextBlocks(result)));
                var image = Assert.Single(result.GetProperty("images").EnumerateArray());
                Assert.Equal("image/png", image.GetProperty("MimeType").GetString());
                Assert.Equal(new byte[] { 1, 2 }, image.GetProperty("bytes").GetBytesFromBase64());
            },
            result => Assert.Equal("B", Assert.Single(TextBlocks(result))),
            result =>
            {
                Assert.Equal("C", Assert.Single(TextBlocks(result)));
                Assert.True(result.GetProperty("IsError").GetBoolean());
            });

        var steer = Assert.Single(fixture.Files("steer-*.json"));
        Assert.Equal(2, steer.GetProperty("pending").GetInt32());
        var inputText = steer.GetProperty("blocks").EnumerateArray().Select(block => block.GetProperty("text").GetString());
        Assert.Equal(firstInput + "\n\n" + secondInput, string.Join("\n\n", inputText));
        Assert.Empty(fixture.Files("cancel-*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleToolBatchesStillReplayHistory(bool wrongCallId)
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var options = BackendSamplingTests.Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "parallel") };
        var first = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        var calls = first.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        var pid = Assert.Single(fixture.ChatPids);
        Assert.Equal(2, calls.Length);

        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool,
        [
            new FunctionResultContent(calls[0].CallId, "first result"),
            new FunctionResultContent(wrongCallId ? "different-call" : calls[1].CallId, "second result")
        ]));
        history.Add(wrongCallId
            ? new(ChatRole.User, "queued input")
            : new(ChatRole.User, [new DataContent(new byte[] { 1, 2 }, "image/png")]));

        var resumed = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ChatFinishReason.Stop, resumed.FinishReason);
        Assert.NotEqual(first.ResponseId, resumed.ResponseId);
        Assert.False(BackendFixture.IsAlive(pid));
        var capture = Assert.Single(fixture.Files("capture-*.json"), capture => capture.GetProperty("pid").GetInt32() != pid);
        var payload = capture.GetProperty("input").GetProperty("message").GetProperty("content").GetString();
        Assert.Contains("Conversation history", payload);
        Assert.Contains(wrongCallId ? "queued input" : "[Image](", payload);
    }

    private static string[] TextBlocks(JsonElement result) =>
        result.GetProperty("text").EnumerateArray().Select(block => block.GetString()!).ToArray();
}
