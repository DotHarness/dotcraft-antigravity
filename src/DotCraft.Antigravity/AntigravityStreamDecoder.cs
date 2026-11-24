using System.Text;
using System.Text.Json;
using DotCraft.Tracing;
using Microsoft.Extensions.AI;

namespace DotCraft.Antigravity;

internal sealed class AntigravityUsageBaseline
{
    public long? InputTokens;
    public long? OutputTokens;
    public long? CacheReadTokens;
    public long? ReasoningTokens;
    private bool _emittedStepUsageInTurn;

    public void MarkStepUsageEmitted() => _emittedStepUsageInTurn = true;

    public UsageDetails? ConsumeResult(JsonElement usage)
    {
        var delta = ComputeDelta(usage);
        var alreadyEmitted = _emittedStepUsageInTurn;
        _emittedStepUsageInTurn = false;
        if (alreadyEmitted || !HasTokens(delta))
            return null;
        return delta;
    }

    public UsageDetails ComputeDelta(JsonElement usage)
    {
        var uncachedInput = Delta(usage, "input_tokens", ref InputTokens);
        var output = Delta(usage, "output_tokens", ref OutputTokens);
        var cached = Delta(usage, "cache_read_tokens", ref CacheReadTokens);
        var reasoning = Delta(usage, "thinking_tokens", ref ReasoningTokens);
        var totalInput = uncachedInput + cached;

        return new UsageDetails
        {
            InputTokenCount = totalInput,
            OutputTokenCount = output,
            CachedInputTokenCount = cached,
            ReasoningTokenCount = reasoning > 0 ? reasoning : null,
            TotalTokenCount = totalInput + output
        };
    }

    public static UsageDetails? ParseStepUsage(JsonElement usage)
    {
        var uncachedInput = ReadLong(usage, "input_tokens");
        var output = ReadLong(usage, "output_tokens");
        var cached = ReadLong(usage, "cache_read_tokens");
        var reasoning = ReadLong(usage, "thinking_tokens");
        var totalInput = uncachedInput + cached;

        var details = new UsageDetails
        {
            InputTokenCount = totalInput,
            OutputTokenCount = output,
            CachedInputTokenCount = cached,
            ReasoningTokenCount = reasoning > 0 ? reasoning : null,
            TotalTokenCount = totalInput + output
        };
        return HasTokens(details) ? details : null;
    }

    private static bool HasTokens(UsageDetails details) =>
        (details.InputTokenCount ?? 0) > 0
        || (details.OutputTokenCount ?? 0) > 0
        || (details.CachedInputTokenCount ?? 0) > 0
        || (details.ReasoningTokenCount ?? 0) > 0;

    private static long ReadLong(JsonElement usage, string property) =>
        usage.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value) && value > 0
            ? value
            : 0;

    private static long Delta(JsonElement usage, string property, ref long? baseline)
    {
        if (!usage.TryGetProperty(property, out var prop) || prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt64(out var value))
            return 0;

        if (baseline is null || value < baseline.Value)
        {
            baseline = value;
            return value;
        }

        var delta = value - baseline.Value;
        baseline = value;
        return delta;
    }
}

internal sealed class AntigravityStreamDecoder(
    AntigravityTools tools,
    IReadOnlySet<string> allowedMediaPaths,
    AntigravityUsageBaseline usageBaseline,
    AntigravityLimits limits,
    string modelId,
    bool resumedAfterToolCall = false)
{
    private static readonly HashSet<string> MediaTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file",
        "view_file"
    };

    private readonly StringBuilder _text = new();
    private readonly HashSet<int> _emittedUsageSteps = [];
    private bool _emittedStepUsage;
    private bool _hasText;

    public string? ConversationId { get; private set; }
    public bool SawProgressEvent { get; private set; }
    public bool FinalResult { get; private set; }

    public string AccumulatedText => _text.ToString();

    public IEnumerable<ChatResponseUpdate> Read(
        JsonElement line,
        Func<string> stderrTail,
        Func<string?, int, UsageDetails, TokenUsageRequestTiming?>? readTiming = null)
    {
        if (!line.TryGetProperty("event", out var eventProp) || eventProp.ValueKind != JsonValueKind.String)
            yield break;

        if (line.TryGetProperty("conversation_id", out var topConv) && topConv.ValueKind == JsonValueKind.String)
            SetConversationId(topConv.GetString());

        var eventType = eventProp.GetString();
        if (eventType != "init")
            SawProgressEvent = true;

        switch (eventType)
        {
            case "init":
                if (line.TryGetProperty("init", out var initObj) && initObj.ValueKind == JsonValueKind.Object
                    && initObj.TryGetProperty("conversation_id", out var initConv) && initConv.ValueKind == JsonValueKind.String)
                {
                    SetConversationId(initConv.GetString());
                }
                break;

            case "step_update":
                if (!line.TryGetProperty("step_update", out var step) || step.ValueKind != JsonValueKind.Object)
                    yield break;

                if (step.TryGetProperty("conversation_id", out var stepConv) && stepConv.ValueKind == JsonValueKind.String)
                    SetConversationId(stepConv.GetString());

                var stepType = step.TryGetProperty("step_type", out var st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString()
                    : null;

                if (string.Equals(stepType, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateToolStep(step);
                    yield break;
                }

                var stepContents = new List<AIContent>();
                if (step.TryGetProperty("text_delta", out var deltaProp)
                    && deltaProp.ValueKind == JsonValueKind.String
                    && deltaProp.GetString() is { Length: > 0 } delta)
                {
                    _hasText = true;
                    _text.Append(delta);
                    stepContents.Add(new TextContent(delta));
                }

                if (!string.Equals(stepType, "checkpoint", StringComparison.OrdinalIgnoreCase)
                    && TryReadStepUsage(step, out var stepIndex) is { } stepUsageDetails)
                {
                    var timing = !_emittedStepUsage && stepIndex is int index
                        ? readTiming?.Invoke(ConversationId, index, stepUsageDetails)
                        : null;
                    _emittedStepUsage = true;
                    usageBaseline.MarkStepUsageEmitted();
                    stepContents.Add(CreateUsage(stepUsageDetails, timing));
                }

                if (stepContents.Count > 0)
                    yield return CreateUpdate(stepContents);
                break;

            case "result":
                if (!line.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Antigravity.InvalidStream: Missing result payload.");

                if (result.TryGetProperty("conversation_id", out var resConv) && resConv.ValueKind == JsonValueKind.String)
                    SetConversationId(resConv.GetString());

                var status = result.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
                    ? statusProp.GetString()
                    : null;
                var error = result.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String
                    ? errorProp.GetString()
                    : null;

                if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(error))
                    throw limits.ClassifyAndRecordError(error ?? $"status={status ?? "unknown"}", stderrTail());

                var finalItems = new List<AIContent>();
                if (!_hasText
                    && result.TryGetProperty("response", out var respProp)
                    && respProp.ValueKind == JsonValueKind.String
                    && respProp.GetString() is { Length: > 0 } fallbackText)
                {
                    _hasText = true;
                    _text.Append(fallbackText);
                    finalItems.Add(new TextContent(fallbackText));
                }

                if (!_hasText && !resumedAfterToolCall)
                    throw limits.ClassifyAndRecordError("Antigravity CLI returned an empty result.", stderrTail());

                if (result.TryGetProperty("usage", out var resUsage)
                    && resUsage.ValueKind == JsonValueKind.Object
                    && usageBaseline.ConsumeResult(resUsage) is { } fallbackUsage)
                {
                    finalItems.Add(CreateUsage(fallbackUsage, null));
                }

                FinalResult = true;
                yield return CreateUpdate(finalItems, ChatFinishReason.Stop);
                break;
        }
    }

    private static UsageContent CreateUsage(UsageDetails usage, TokenUsageRequestTiming? timing)
    {
        var content = new UsageContent(usage);
        TokenUsageRequestTiming.Attach(content, timing!);
        return content;
    }

    private UsageDetails? TryReadStepUsage(JsonElement step, out int? stepIndex)
    {
        stepIndex = null;
        if (!step.TryGetProperty("usage", out var stepUsage) || stepUsage.ValueKind != JsonValueKind.Object)
            return null;

        var state = step.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.String
            ? stateProp.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(state) && !string.Equals(state, "DONE", StringComparison.OrdinalIgnoreCase))
            return null;

        if (step.TryGetProperty("step_index", out var idxProp) && idxProp.ValueKind == JsonValueKind.Number && idxProp.TryGetInt32(out var index))
        {
            if (!_emittedUsageSteps.Add(index))
                return null;
            stepIndex = index;
        }

        return AntigravityUsageBaseline.ParseStepUsage(stepUsage);
    }

    private void ValidateToolStep(JsonElement step)
    {
        var toolName = step.TryGetProperty("tool_name", out var tn) && tn.ValueKind == JsonValueKind.String
            ? tn.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(toolName)
            && step.TryGetProperty("tool_info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("name", out var infoName) && infoName.ValueKind == JsonValueKind.String)
        {
            toolName = infoName.GetString();
        }

        if (tools.IsBridgedStreamToolName(toolName))
            return;

        if (string.Equals(toolName, "call_mcp_tool", StringComparison.OrdinalIgnoreCase) && IsBridgedMcpCall(step))
            return;

        if (!string.IsNullOrWhiteSpace(toolName) && MediaTools.Contains(toolName) && IsAuthorizedMediaRead(step))
            return;

        throw new NativeToolBlockedException(
            $"Antigravity.NativeToolBlocked: Antigravity CLI attempted built-in tool '{toolName ?? "unknown"}' outside DotCraft's tool catalog.");
    }

    private bool IsBridgedMcpCall(JsonElement step)
    {
        if (!step.TryGetProperty("tool_info", out var info) || info.ValueKind != JsonValueKind.Object)
            return false;
        if (!info.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            return false;

        var (serverName, targetTool) = ReadMcpTarget(parameters);
        if (serverName is not null || targetTool is not null)
            return IsDeclaredDotcraftTarget(serverName, targetTool);

        if (!parameters.TryGetProperty("Arguments", out var nested) || nested.ValueKind != JsonValueKind.Object)
            return false;

        var (nestedServer, nestedTool) = ReadMcpTarget(nested);
        return IsDeclaredDotcraftTarget(nestedServer, nestedTool);
    }

    private static (string? ServerName, string? ToolName) ReadMcpTarget(JsonElement parameters) => (
        ReadString(parameters, "ServerName") ?? ReadString(parameters, "serverName") ?? ReadString(parameters, "server_name"),
        ReadString(parameters, "ToolName") ?? ReadString(parameters, "toolName") ?? ReadString(parameters, "tool_name"));

    private bool IsDeclaredDotcraftTarget(string? serverName, string? toolName) =>
        string.Equals(serverName, AntigravityTools.ServerName, StringComparison.Ordinal)
        && tools.IsBridgedStreamToolName(toolName);

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()?.Trim()
            : null;

    private bool IsAuthorizedMediaRead(JsonElement step)
    {
        if (allowedMediaPaths.Count == 0) return false;
        if (!step.TryGetProperty("tool_info", out var info)) return false;
        return ContainsAuthorizedPath(info);
    }

    private bool ContainsAuthorizedPath(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (string.IsNullOrWhiteSpace(text)) return false;
                foreach (var path in allowedMediaPaths)
                {
                    if (text.Contains(path, StringComparison.OrdinalIgnoreCase)
                        || text.Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    if (ContainsAuthorizedPath(prop.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsAuthorizedPath(item)) return true;
                return false;
            default:
                return false;
        }
    }

    private void SetConversationId(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            ConversationId = id.Trim();
    }

    private ChatResponseUpdate CreateUpdate(IList<AIContent> contents, ChatFinishReason? finish = null) =>
        new(ChatRole.Assistant, contents)
        {
            ResponseId = ConversationId,
            MessageId = ConversationId,
            ModelId = modelId,
            FinishReason = finish
        };
}

internal sealed class NativeToolBlockedException(string message) : InvalidOperationException(message);
