using System.Text.Json;
using DotCraft.Agents;
using DotCraft.Tracing;
using DotHarness.Acp.V1;
using Microsoft.Extensions.AI;
using static DotCraft.Antigravity.AntigravityRequestTiming;

namespace DotCraft.Antigravity;

internal sealed class AntigravityAcpUsage(string isolatedGeminiDir, string sessionId)
{
    private long? _cursor = ReadCursor(isolatedGeminiDir, sessionId);
    private GenerationUsage _reported;

    public UsageContent? ReadToolBoundary()
    {
        if (ReadPending() is not { } batch)
            return null;

        _reported += batch.Usage;
        _cursor = batch.Cursor;
        return ToContent(batch.Usage, batch.Timing);
    }

    public UsageContent? ReadResult(PromptResponse promptResult)
    {
        var batch = ReadPending();
        _cursor = batch?.Cursor ?? ReadCursor(isolatedGeminiDir, sessionId);

        var reported = _reported;
        _reported = default;
        if (promptResult.ExtensionData is null
            || !promptResult.ExtensionData.TryGetValue("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadCount(usage, "inputTokens");
        var output = ReadCount(usage, "outputTokens");
        var total = new GenerationUsage(input, output, ReadCount(usage, "cachedReadTokens"),
            Math.Min(ReadCount(usage, "cachedWriteTokens"), input), Math.Min(ReadCount(usage, "thoughtTokens"), output));
        var remaining = total.RemainingAfter(reported);
        if (!remaining.HasBillableTokens)
            return null;

        var timing = batch is not null && batch.Usage.Input == remaining.Input
            && batch.Usage.Output == remaining.Output && batch.Usage.CachedRead == remaining.CachedRead
            ? batch.Timing : null;
        return ToContent(remaining, timing);
    }

    private GenerationBatch? ReadPending() =>
        _cursor is long cursor ? ReadAfter(isolatedGeminiDir, sessionId, cursor) : null;

    private static UsageContent ToContent(GenerationUsage counts, TokenUsageRequestTiming? timing)
    {
        var input = counts.Input + counts.CachedRead;
        var details = new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = counts.Output,
            CachedInputTokenCount = counts.CachedRead,
            ReasoningTokenCount = counts.Reasoning > 0 ? counts.Reasoning : null,
            TotalTokenCount = input + counts.Output
        };
        if (counts.CachedWrite > 0)
            details.AdditionalCounts = new() { ["cache_write_input_tokens"] = counts.CachedWrite };

        var content = new UsageContent(details);
        TokenUsageRequestTiming.Attach(content, timing);
        ProviderContextUsage.Attach(content, null);
        return content;
    }

    private static long ReadCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var count)
        && count >= 0
            ? count
            : 0;
}
