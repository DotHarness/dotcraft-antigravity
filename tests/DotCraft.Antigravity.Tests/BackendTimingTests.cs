using DotCraft.Tracing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendTimingTests
{
    [Theory]
    [InlineData("matching", true)]
    [InlineData("mismatched", false)]
    [InlineData("ambiguous", false)]
    [InlineData("missing", false)]
    public async Task StepUsageUsesOnlyMatchingNativeTiming(string fixtureKind, bool expectedTiming)
    {
        await using var fixture = new BackendFixture();
        if (fixtureKind != "missing")
            WriteTimingFixture(fixture.Root, fixtureKind);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var contents = new List<UsageContent>();
        await foreach (var update in fixture.Client.GetStreamingResponseAsync(
            [new(ChatRole.User, "native-timing-fixture")], cancellationToken: timeout.Token))
        {
            contents.AddRange(update.Contents.OfType<UsageContent>());
        }

        var usage = Assert.Single(contents);
        Assert.Equal(15, usage.Details.OutputTokenCount);
        Assert.Equal(expectedTiming ? new TokenUsageRequestTiming(1250, 2500) : null, TokenUsageRequestTiming.TryGet(usage));
        if (!expectedTiming)
            Assert.Null(Assert.Single(usage.AdditionalProperties!).Value);
    }

    [Fact]
    public async Task ToolRoundReportsTimingForTheGenerationThatEmittedItsUsage()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        WriteTimingFixture(fixture.Root, "tool-round");
        var options = BackendSamplingTests.Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "native-timing-fixture parallel") };

        var (first, firstUsage) = await ReadResponseAsync();
        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        Assert.Equal(new TokenUsageRequestTiming(1250, 2500), TokenUsageRequestTiming.TryGet(firstUsage));

        history.AddRange(first.Messages);
        history.Add(new ChatMessage(ChatRole.Tool, first.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Select(call => (AIContent)new FunctionResultContent(call.CallId, "done"))
            .ToList()));
        var (final, finalUsage) = await ReadResponseAsync();
        Assert.Equal(ChatFinishReason.Stop, final.FinishReason);
        Assert.Null(TokenUsageRequestTiming.TryGet(finalUsage));

        async Task<(ChatResponse, UsageContent)> ReadResponseAsync()
        {
            var usage = new List<UsageContent>();
            var response = await CollectUsageAsync().ToChatResponseAsync(timeout.Token);
            return (response, Assert.Single(usage));

            async IAsyncEnumerable<ChatResponseUpdate> CollectUsageAsync()
            {
                await foreach (var update in fixture.Client.GetStreamingResponseAsync(history, options, timeout.Token))
                {
                    usage.AddRange(update.Contents.OfType<UsageContent>());
                    yield return update;
                }
            }
        }
    }

    private static void WriteTimingFixture(string root, string fixtureKind)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "fake-timing.db"),
            Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB NOT NULL)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO gen_metadata (data) VALUES ($data)";
        var blob = cmd.Parameters.Add("$data", SqliteType.Blob);

        if (fixtureKind == "tool-round")
        {
            // One generation for reply step 1 and tool step 2, with 10 output tokens.
            blob.Value = Convert.FromHexString("0a172202180a5a0708011080e59a77620808021080cab5ee0112020102");
            cmd.ExecuteNonQuery();
            return;
        }

        // One model generation for step 10, with 15 output tokens and 1.25s/2.5s timing.
        var matching = Convert.FromHexString("0a172202180f5a0708011080e59a77620808021080cab5ee0112010a");
        blob.Value = matching;
        if (fixtureKind == "mismatched")
        {
            var wrongOutput = matching.ToArray();
            wrongOutput[5] = 14;
            blob.Value = wrongOutput;
        }
        cmd.ExecuteNonQuery();

        if (fixtureKind != "ambiguous")
        {
            var wrongStep = matching.ToArray();
            wrongStep[^1] = 11;
            blob.Value = wrongStep;
        }
        cmd.ExecuteNonQuery();
    }
}
