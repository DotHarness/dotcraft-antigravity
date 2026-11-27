using System.Text.Json.Nodes;
using DotCraft.Tracing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendTimingTests
{
    private const string ToolGeneration = "0a1f220a1019180f2005285a48035a0708011080e59a77620808021080cab5ee0112010a";
    private const string AnswerGeneration = "0a20220a102818142006286448055a0808011080afd0e502620808031080cab5ee0112010b";
    private const string MismatchedAnswerGeneration = "0a20220a102818132006286448055a0808011080afd0e502620808031080cab5ee0112010b";

    [Theory]
    [InlineData("matching")]
    [InlineData("mismatched")]
    [InlineData("stale")]
    [InlineData("missing")]
    public void PromptResultAggregatesTimingOnlyFromMatchingNewGenerations(string fixtureKind)
    {
        var root = Directory.CreateTempSubdirectory("agy-timing-");
        try
        {
            var conversation = Guid.NewGuid().ToString("D");
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "conversations"));
            var database = Path.Combine(directory.FullName, $"{conversation}.db");
            if (fixtureKind == "stale")
                AddTimingRows(database, ToolGeneration, AnswerGeneration);

            var reader = new AntigravityAcpUsage(root.FullName, conversation);
            if (fixtureKind is "matching" or "mismatched")
                AddTimingRows(database, ToolGeneration,
                    fixtureKind == "matching" ? AnswerGeneration : MismatchedAnswerGeneration);

            var usage = Assert.IsType<UsageContent>(reader.ReadResult(PromptResult()));
            Assert.Equal(255, usage.Details.InputTokenCount);
            Assert.Equal(35, usage.Details.OutputTokenCount);
            Assert.Equal(190, usage.Details.CachedInputTokenCount);
            Assert.Equal(8, usage.Details.ReasoningTokenCount);
            Assert.Equal(11, usage.Details.AdditionalCounts!["cache_write_input_tokens"]);
            Assert.True(TokenUsageRequestTiming.IsAttached(usage));
            Assert.Equal(fixtureKind == "matching" ? new TokenUsageRequestTiming(1500, 6000) : null,
                TokenUsageRequestTiming.TryGet(usage));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolBoundaryReportsOnceAndPromptResultOnlyReportsTheRemainingGeneration(bool steered)
    {
        var root = Directory.CreateTempSubdirectory("agy-timing-");
        try
        {
            var conversation = Guid.NewGuid().ToString("D");
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "conversations"));
            var database = Path.Combine(directory.FullName, $"{conversation}.db");
            var reader = new AntigravityAcpUsage(root.FullName, conversation);
            AddTimingRows(database, ToolGeneration);

            var tool = Assert.IsType<UsageContent>(reader.ReadToolBoundary());
            Assert.Equal(115, tool.Details.InputTokenCount);
            Assert.Equal(15, tool.Details.OutputTokenCount);
            Assert.Equal(90, tool.Details.CachedInputTokenCount);
            Assert.Equal(3, tool.Details.ReasoningTokenCount);
            Assert.Equal(5, tool.Details.AdditionalCounts!["cache_write_input_tokens"]);
            Assert.Equal(new TokenUsageRequestTiming(1250, 2500), TokenUsageRequestTiming.TryGet(tool));
            Assert.Null(reader.ReadToolBoundary());

            if (steered)
                Assert.Null(reader.ReadResult(PromptResult(25, 15, 90, 5, 3)));

            AddTimingRows(database, AnswerGeneration);

            var answer = Assert.IsType<UsageContent>(reader.ReadResult(
                steered ? PromptResult(40, 20, 100, 6, 5) : PromptResult()));
            Assert.Equal(140, answer.Details.InputTokenCount);
            Assert.Equal(20, answer.Details.OutputTokenCount);
            Assert.Equal(100, answer.Details.CachedInputTokenCount);
            Assert.Equal(5, answer.Details.ReasoningTokenCount);
            Assert.Equal(6, answer.Details.AdditionalCounts!["cache_write_input_tokens"]);
            Assert.Equal(new TokenUsageRequestTiming(1750, 3500), TokenUsageRequestTiming.TryGet(answer));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static JsonObject PromptResult(long input = 65, long output = 35, long cachedRead = 190, long cachedWrite = 11, long thought = 8) =>
        new()
        {
            ["usage"] = new JsonObject
            {
                ["inputTokens"] = input,
                ["outputTokens"] = output,
                ["cachedReadTokens"] = cachedRead,
                ["cachedWriteTokens"] = cachedWrite,
                ["thoughtTokens"] = thought,
            }
        };

    private static void AddTimingRows(string database, params string[] records)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS gen_metadata (idx INTEGER PRIMARY KEY, data BLOB NOT NULL)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO gen_metadata (data) VALUES ($data)";
        var blob = cmd.Parameters.Add("$data", SqliteType.Blob);
        foreach (var record in records)
        {
            blob.Value = Convert.FromHexString(record);
            cmd.ExecuteNonQuery();
        }
    }
}
