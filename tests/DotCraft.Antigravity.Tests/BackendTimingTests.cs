using System.Text.Json.Nodes;
using DotCraft.Tracing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendTimingTests
{
    [Theory]
    [InlineData("matching", true)]
    [InlineData("mismatched", false)]
    [InlineData("ambiguous", false)]
    [InlineData("stale", false)]
    [InlineData("missing", false)]
    public void PromptUsageAttachesTimingOnlyForOneMatchingNewGeneration(string fixtureKind, bool expectedTiming)
    {
        var root = Directory.CreateTempSubdirectory("agy-timing-");
        try
        {
            var conversation = Guid.NewGuid().ToString("D");
            var data = Path.Combine(root.FullName, "conversations");
            Directory.CreateDirectory(data);
            var database = Path.Combine(data, $"{conversation}.db");
            var matching = Convert.FromHexString("0a1b22061019180f48035a0708011080e59a77620808021080cab5ee0112010a");
            if (fixtureKind == "stale")
                AddTimingRows(database, matching);
            var usageReader = new AntigravityAcpUsage(root.FullName, conversation);
            if (fixtureKind == "matching")
                AddTimingRows(database, matching);
            if (fixtureKind == "ambiguous")
                AddTimingRows(database, matching, matching);
            if (fixtureKind == "mismatched")
            {
                var mismatch = matching.ToArray();
                mismatch[7] = 14;
                AddTimingRows(database, mismatch);
            }

            var result = JsonNode.Parse("""
                {
                  "stopReason":"end_turn",
                  "usage":{"inputTokens":25,"outputTokens":15,"cachedReadTokens":90,"cachedWriteTokens":5,"thoughtTokens":3}
                }
                """)!.AsObject();
            var usage = usageReader.ReadResult(result)!;
            Assert.Equal(115, usage.Details.InputTokenCount);
            Assert.Equal(15, usage.Details.OutputTokenCount);
            Assert.Equal(90, usage.Details.CachedInputTokenCount);
            Assert.Equal(3, usage.Details.ReasoningTokenCount);
            Assert.Equal(130, usage.Details.TotalTokenCount);
            Assert.Equal(5, usage.Details.AdditionalCounts!["cache_write_input_tokens"]);
            Assert.Equal(expectedTiming ? new TokenUsageRequestTiming(1250, 2500) : null, TokenUsageRequestTiming.TryGet(usage));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void AddTimingRows(string database, params byte[][] records)
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
            blob.Value = record;
            cmd.ExecuteNonQuery();
        }
    }
}
