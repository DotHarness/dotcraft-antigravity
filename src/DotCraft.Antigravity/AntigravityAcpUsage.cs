using System.Text.Json.Nodes;
using DotCraft.Tracing;
using Microsoft.Extensions.AI;

namespace DotCraft.Antigravity;

internal sealed class AntigravityAcpUsage(string isolatedGeminiDir, string sessionId)
{
    private long? _timingCursor = AntigravityRequestTiming.ReadCursor(isolatedGeminiDir, sessionId);

    public UsageContent? ReadResult(JsonObject promptResult)
    {
        if (promptResult["usage"] is not JsonObject usage)
        {
            _timingCursor = null;
            return null;
        }

        var nativeInput = ReadCount(usage, "inputTokens") ?? 0;
        var output = ReadCount(usage, "outputTokens") ?? 0;
        var cachedRead = ReadCount(usage, "cachedReadTokens") ?? 0;
        var cachedWrite = Math.Min(ReadCount(usage, "cachedWriteTokens") ?? 0, nativeInput);
        var nativeThought = ReadCount(usage, "thoughtTokens");
        var thought = Math.Min(nativeThought ?? 0, output);
        var input = nativeInput + cachedRead;
        if (input == 0 && output == 0)
        {
            _timingCursor = null;
            return null;
        }

        var details = new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            CachedInputTokenCount = cachedRead,
            ReasoningTokenCount = thought > 0 ? thought : null,
            TotalTokenCount = input + output
        };
        if (cachedWrite > 0)
            details.AdditionalCounts = new() { ["cache_write_input_tokens"] = cachedWrite };

        var content = new UsageContent(details);
        TokenUsageRequestTiming? timing = null;
        if (_timingCursor is long cursor)
        {
            (_timingCursor, timing) = AntigravityRequestTiming.ReadAfter(
                isolatedGeminiDir, sessionId, cursor, nativeInput, output, nativeThought);
        }

        TokenUsageRequestTiming.Attach(content, timing!);
        return content;
    }

    private static long? ReadCount(JsonObject usage, string name) =>
        usage[name] is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0
            ? count
            : null;
}
