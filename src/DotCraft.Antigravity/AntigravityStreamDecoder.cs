using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace DotCraft.Antigravity;

internal sealed class AntigravityUsageBaseline
{
    public long? InputTokens;
    public long? OutputTokens;
    public long? CacheReadTokens;
    public long? TotalTokens;

    public UsageDetails ComputeDelta(JsonElement usage)
    {
        var input = Delta(usage, "input_tokens", ref InputTokens);
        var output = Delta(usage, "output_tokens", ref OutputTokens);
        var cached = Delta(usage, "cache_read_tokens", ref CacheReadTokens);
        var total = usage.TryGetProperty("total_tokens", out _)
            ? Delta(usage, "total_tokens", ref TotalTokens)
            : input + output;

        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            CachedInputTokenCount = cached,
            TotalTokenCount = total
        };
    }

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
    string modelId)
{
    private static readonly HashSet<string> MediaTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file",
        "view_file"
    };

    private readonly StringBuilder _text = new();
    private JsonElement? _lastStepUsage;
    private bool _hasText;

    public string? ConversationId { get; private set; }
    public bool SawProgressEvent { get; private set; }
    public bool FinalResult { get; private set; }

    public string AccumulatedText => _text.ToString();

    public IEnumerable<ChatResponseUpdate> Read(JsonElement line, Func<string> stderrTail)
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

                if (step.TryGetProperty("usage", out var stepUsage) && stepUsage.ValueKind == JsonValueKind.Object)
                    _lastStepUsage = stepUsage.Clone();

                var stepType = step.TryGetProperty("step_type", out var st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString()
                    : null;

                if (string.Equals(stepType, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateToolStep(step);
                    yield break;
                }

                if (step.TryGetProperty("text_delta", out var deltaProp)
                    && deltaProp.ValueKind == JsonValueKind.String
                    && deltaProp.GetString() is { Length: > 0 } delta)
                {
                    _hasText = true;
                    _text.Append(delta);
                    yield return CreateUpdate([new TextContent(delta)]);
                }
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

                JsonElement? usageEl = result.TryGetProperty("usage", out var resUsage) && resUsage.ValueKind == JsonValueKind.Object
                    ? resUsage
                    : _lastStepUsage;
                if (usageEl is { } u)
                    finalItems.Add(new UsageContent(usageBaseline.ComputeDelta(u)));

                FinalResult = true;
                yield return CreateUpdate(finalItems, ChatFinishReason.Stop);
                break;
        }
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

        var serverName = ReadString(parameters, "ServerName")
            ?? ReadString(parameters, "serverName")
            ?? ReadString(parameters, "server_name");
        if (!string.Equals(serverName, AntigravityTools.ServerName, StringComparison.Ordinal))
            return false;

        var targetTool = ReadString(parameters, "ToolName")
            ?? ReadString(parameters, "toolName")
            ?? ReadString(parameters, "tool_name");
        return tools.IsBridgedStreamToolName(targetTool);
    }

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
