using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotCraft.Agents;
using DotCraft.Configuration;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace DotCraft.Antigravity;

internal sealed class AntigravityRequest
{
    public const string RelayAgentName = "dotcraft-relay";

    public EffectiveModelRuntime Runtime { get; }
    public ChatMessage[] Messages { get; }
    public AntigravityTools Tools { get; }
    public string SystemPrompt { get; }
    public string ModelSlug { get; }
    public string? EffortToken { get; }
    public bool AllowAiCredits { get; }
    public string ResumeFingerprint { get; }
    public JsonArray History { get; }

    public AntigravityRequest(
        EffectiveModelRuntime runtime,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        AntigravityModels models,
        bool allowAiCredits)
    {
        Runtime = runtime;
        Messages = messages.ToArray();
        AllowAiCredits = allowAiCredits;
        Tools = new(options?.ToolMode == ChatToolMode.None ? [] : options?.Tools ?? []);

        var requestedModel = !string.IsNullOrWhiteSpace(options?.ModelId) ? options.ModelId!.Trim() : runtime.Model;
        if (string.IsNullOrWhiteSpace(requestedModel))
            throw new ArgumentException("Antigravity.MissingModel: A model ID is required.");

        var effort = ResolveEffort(options);
        (ModelSlug, EffortToken) = models.ResolveLaunchModel(requestedModel, effort);

        SystemPrompt = string.Join("\n\n", new[] { options?.Instructions }
            .Concat(Messages.Where(IsInstruction).Select(m => m.Text))
            .Append(Tools.Count > 0
                ? Tools.PromptSection
                : "Do not invoke any built-in tools; answer directly with text.")
            .Append(ToolChoice(options?.ToolMode))
            .Append(ResponseFormat(options?.ResponseFormat))
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        History = Snapshot(Messages, reasoning: false);
        ResumeFingerprint = new JsonArray(SystemPrompt, Tools.Schema, AllowAiCredits)
            .ToJsonString(AntigravityJson.Compact);
    }

    public string BuildRelayAgentMarkdown(bool allowPrivateReads)
    {
        var body = string.IsNullOrWhiteSpace(SystemPrompt)
            ? "You are a helpful assistant."
            : SystemPrompt;
        if (Tools.Count > 0)
            body += "\n\nUse the exposed mcp_dotcraft_<tool> tools directly when available.";
        if (allowPrivateReads)
            body += "\n\nThe one exception to the built-in tool restriction is viewing explicitly provided private attachments or conversation evidence.";

        return $"""
            ---
            name: {RelayAgentName}
            description: DotCraft model relay
            mainAgent: true
            subagent: false
            excludeDefaultComponents: true
            inheritCustomizations: {(Tools.Count > 0 || allowPrivateReads ? "true" : "false")}
            tools: {(allowPrivateReads ? "[view_file]" : "[]")}
            ---

            {body}
            """;
    }

    public Task<(string Text, List<string> MediaPaths, string? HandoffPath)> BuildColdInputAsync(
        PrivateDirectory directory,
        CancellationToken ct) =>
        BuildConversationInputAsync(Messages, directory, ct, afterStop: false);

    public async Task<(string Text, List<string> MediaPaths, string? HandoffPath)?> BuildResumeInputAsync(
        AntigravityCheckpoint checkpoint,
        PrivateDirectory directory,
        CancellationToken ct)
    {
        if (Hash(ResumeFingerprint) != checkpoint.Fingerprint)
            return null;

        if (checkpoint.SubmittedMessageCount is int submitted)
        {
            if (submitted <= 0 || submitted >= Messages.Length
                || HistoryHash(Snapshot(Messages.Take(submitted), reasoning: false)) != checkpoint.HistoryHash)
                return null;

            return await BuildConversationInputAsync(Messages[submitted..], directory, ct, afterStop: true);
        }

        if (After(checkpoint) is { } added && await BuildIncrementalUserInputAsync(added, directory, ct) is { } input)
            return (input.Text, input.MediaPaths, null);

        return null;
    }

    private static async Task<(string Text, List<string> MediaPaths, string? HandoffPath)> BuildConversationInputAsync(
        ChatMessage[] messages,
        PrivateDirectory directory,
        CancellationToken ct,
        bool afterStop)
    {
        var conversation = messages.Where(m => !IsInstruction(m)).ToArray();
        var trailingStart = conversation.Length;
        while (trailingStart > 0 && conversation[trailingStart - 1].Role == ChatRole.User)
            trailingStart--;

        var prior = conversation[..trailingStart];
        var trailingUsers = conversation[trailingStart..];
        var mediaPaths = new List<string>();
        var sections = new List<string>();
        string? handoffPath = null;

        if (prior.Length > 0)
        {
            if (afterStop)
                sections.Add("The previous turn was stopped. Use its recorded progress as evidence, not as a request to rerun tools.");

            var handoff = await AntigravityHandoff.BuildAsync(prior, directory, ct, append: afterStop);
            sections.Add(handoff.Context);
            handoffPath = handoff.File;
            var priorMediaRefs = new List<string>();
            foreach (var content in prior.SelectMany(m => m.Contents).SelectMany(AntigravityContent.Expand))
            {
                if (content is DataContent data && AntigravityContent.IsSupportedMedia(data))
                {
                    var (path, reference) = await AntigravityContent.MaterializeMediaAsync(directory, data, ct);
                    mediaPaths.Add(path);
                    priorMediaRefs.Add(reference);
                }
            }
            if (priorMediaRefs.Count > 0)
                sections.Add("Attachments from conversation history:\n" + string.Join("\n", priorMediaRefs));
        }

        if (trailingUsers.Length > 0)
        {
            var userParts = new List<string>(trailingUsers.Length);
            foreach (var userMessage in trailingUsers)
            {
                var userText = await RenderMessageContentsAsync(userMessage.Contents, directory, mediaPaths, ct);
                if (!string.IsNullOrEmpty(userText))
                    userParts.Add(userText);
            }
            if (userParts.Count > 0)
            {
                var currentUser = string.Join("\n\n", userParts);
                sections.Add(prior.Length > 0 ? "Current user request:\n" + currentUser : currentUser);
            }
        }
        else
        {
            sections.Add("Continue the conversation from the history above.");
        }

        var prompt = sections.Count > 0 ? string.Join("\n\n", sections) : "Continue.";
        return (prompt, mediaPaths, handoffPath);
    }

    public async Task<(string Text, List<string> MediaPaths)?> BuildIncrementalUserInputAsync(
        JsonNode?[] added,
        PrivateDirectory directory,
        CancellationToken ct)
    {
        if (added.Length == 0 || !added.All(n => n?["role"]?.GetValue<string>() == "user"))
            return null;

        var trailingUsers = new List<ChatMessage>();
        for (var i = Messages.Length - 1; i >= 0 && Messages[i].Role == ChatRole.User; i--)
            trailingUsers.Add(Messages[i]);
        trailingUsers.Reverse();

        var trailingAtoms = trailingUsers.SelectMany(m => m.Contents)
            .Where(c => c is not UsageContent and not TextReasoningContent)
            .ToArray();

        var mediaPaths = new List<string>();
        var parts = new List<string>();
        var mediaQueue = new Queue<DataContent>(trailingAtoms.OfType<DataContent>().Where(AntigravityContent.IsSupportedMedia));

        // Skip any earlier media from the same trailing user run if added is a strict suffix.
        var addedMediaCount = added.Count(a => a?["content"]?["type"]?.GetValue<string>() == "media");
        while (mediaQueue.Count > addedMediaCount)
            mediaQueue.Dequeue();

        foreach (var atom in added)
        {
            if (atom?["content"] is not JsonObject content) return null;
            if (content["type"] is null && content["text"] is JsonValue text)
            {
                parts.Add(text.GetValue<string>());
            }
            else if (content["type"]?.GetValue<string>() == "media" && mediaQueue.Count > 0)
            {
                var data = mediaQueue.Dequeue();
                var (path, reference) = await AntigravityContent.MaterializeMediaAsync(directory, data, ct);
                mediaPaths.Add(path);
                parts.Add(reference);
            }
            else if (content["type"]?.GetValue<string>() == "uri" && content["uri"] is JsonValue uri)
            {
                parts.Add(uri.GetValue<string>());
            }
            else
            {
                return null;
            }
        }

        if (parts.Count == 0) return null;
        return (string.Join("\n\n", parts), mediaPaths);
    }

    public bool TryBuildSteerPrompt(ReadOnlySpan<JsonNode?> trailing, out JsonArray? guidance)
    {
        guidance = null;
        if (trailing.IsEmpty)
            return true;

        foreach (var atom in trailing)
        {
            if (atom?["role"]?.GetValue<string>() != "user" || atom["content"]?["text"] is not JsonValue)
                return false;
        }

        var messages = Messages.Reverse().TakeWhile(message => message.Role == ChatRole.User).Reverse();
        guidance = new JsonArray(messages.SelectMany(message => message.Contents.OfType<TextContent>())
            .Select(content => (JsonNode?)AntigravityAcpSession.TextBlock(content.Text)).ToArray());
        return true;
    }

    public JsonNode?[]? After(JsonArray expected)
    {
        if (History.Count <= expected.Count) return null;
        var prefix = new JsonArray(History.Take(expected.Count).Select(n => n?.DeepClone()).ToArray());
        return JsonNode.DeepEquals(expected, prefix) ? History.Skip(expected.Count).ToArray() : null;
    }

    private JsonNode?[]? After(AntigravityCheckpoint checkpoint)
    {
        if (History.Count <= checkpoint.HistoryCount)
            return null;

        return HistoryHash(History.Take(checkpoint.HistoryCount)) == checkpoint.HistoryHash
            ? History.Skip(checkpoint.HistoryCount).ToArray()
            : null;
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string HistoryHash(IEnumerable<JsonNode?> atoms) =>
        Hash(new JsonArray(atoms.Select(Canonicalize).ToArray()).ToJsonString(AntigravityJson.Compact));

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Canonicalize(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonicalize).ToArray()),
        _ => node?.DeepClone()
    };

    public static JsonArray Snapshot(IEnumerable<ChatMessage> messages, bool reasoning = true)
    {
        var atoms = new JsonArray();
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is UsageContent || (!reasoning && content is TextReasoningContent)) continue;
                JsonNode body = content switch
                {
                    TextContent text => new JsonObject { ["text"] = text.Text },
                    TextReasoningContent thought => new JsonObject { ["thinking"] = thought.Text },
                    FunctionCallContent call => new JsonObject
                    {
                        ["CallId"] = call.CallId,
                        ["Name"] = call.Name,
                        ["Arguments"] = AntigravityJson.Value(call.Arguments)
                    },
                    FunctionResultContent result => new JsonObject
                    {
                        ["CallId"] = result.CallId,
                        ["Result"] = AntigravityContent.HistoryResult(result.Result),
                        ["IsError"] = result.Exception is not null
                    },
                    DataContent or UriContent => AntigravityContent.MediaSummary(content),
                    _ => throw new NotSupportedException($"Antigravity.ContentType: Unsupported content '{content.GetType().Name}'.")
                };

                var role = message.Role.Value;
                var textKey = content is TextContent ? "text" : content is TextReasoningContent ? "thinking" : null;
                if (textKey is not null
                    && atoms.LastOrDefault() is JsonObject last
                    && last["role"]?.GetValue<string>() == role
                    && last["content"]?[textKey] is JsonValue previous)
                {
                    last["content"]![textKey] = previous.GetValue<string>() + body[textKey]!.GetValue<string>();
                }
                else
                {
                    atoms.Add(new JsonObject { ["role"] = role, ["content"] = body });
                }
            }
        }
        return atoms;
    }

    private static async Task<string> RenderMessageContentsAsync(
        IList<AIContent> contents,
        PrivateDirectory directory,
        List<string> mediaPaths,
        CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var content in contents)
        {
            if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
            {
                parts.Add(text.Text);
            }
            else if (content is DataContent data)
            {
                var (path, reference) = await AntigravityContent.MaterializeMediaAsync(directory, data, ct);
                mediaPaths.Add(path);
                parts.Add(reference);
            }
            else if (content is UriContent uri)
            {
                parts.Add(uri.Uri.ToString());
            }
        }
        return string.Join("\n\n", parts);
    }

    private static ProviderReasoningEffort? ResolveEffort(ChatOptions? options)
    {
        var pipeline = ProviderPipelineOptionsScope.Current;
        return ProviderReasoningOptions.Resolve(ProviderReasoningOptions.ApplyDefaults(options, pipeline))
            ?? (options?.Reasoning is null && pipeline is { ReasoningEnabled: false } ? ProviderReasoningEffort.None : null);
    }

    private static bool IsInstruction(ChatMessage message) =>
        message.Role == ChatRole.System || string.Equals(message.Role.Value, "developer", StringComparison.OrdinalIgnoreCase);

    private string? ToolChoice(ChatToolMode? mode) => mode switch
    {
        RequiredChatToolMode { RequiredFunctionName: { } name } =>
            $"You must call the {Tools.WireName(name)} tool in your next response.",
        RequiredChatToolMode when Tools.Count > 0 =>
            "You must call one of the available `dotcraft` MCP tools in your next response.",
        _ => null
    };

    private static string? ResponseFormat(ChatResponseFormat? format) => format switch
    {
        ChatResponseFormatJson { Schema: { } schema } =>
            "Respond with only a JSON value that matches this JSON schema, without code fences:\n" + schema.GetRawText(),
        ChatResponseFormatJson =>
            "Respond with only a valid JSON value, without code fences.",
        _ => null
    };
}

internal sealed class AntigravityTools
{
    public const string ServerName = "dotcraft";

    private readonly Dictionary<string, AIFunctionDeclaration> _byWire = new(StringComparer.Ordinal);

    public int Count => _byWire.Count;

    public List<Tool> ProtocolTools => _byWire.Select(pair => new Tool
    {
        Name = pair.Key,
        Description = pair.Value.Description,
        InputSchema = pair.Value.JsonSchema
    }).ToList();

    public JsonArray Schema => new(_byWire.Select(pair =>
    {
        var schema = new JsonObject
        {
            ["name"] = pair.Key,
            ["inputSchema"] = JsonNode.Parse(pair.Value.JsonSchema.GetRawText())
        };
        if (pair.Value.Description is { } description)
            schema["description"] = description;
        return (JsonNode)schema;
    }).ToArray());

    public string Mapping => new JsonObject(
        _byWire.Select(p => new KeyValuePair<string, JsonNode?>(p.Value.Name, JsonValue.Create(p.Key))))
        .ToJsonString(AntigravityJson.Compact);

    public string PromptSection
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Tool protocol");
            sb.AppendLine("You run as the model inside DotCraft. DotCraft, not you, executes tools. Use ONLY the declared `dotcraft` MCP tools listed below when taking actions.");
            sb.AppendLine("- Call a tool via `call_mcp_tool` with `ServerName` set to \"dotcraft\", `ToolName` set to one of the tool wire names listed below, and `Arguments` set to a JSON object matching that tool's parameter schema.");
            sb.AppendLine("- Never call `list_resources`, `read_resource`, `manage_task`, or any other built-in function, do not try to read MCP schema files from disk, and never write tool calls as text.");
            sb.AppendLine("- When no tool is needed, answer directly.");
            sb.AppendLine();
            sb.AppendLine("Tool names in these instructions map to the available MCP tools as follows:");
            sb.AppendLine(Mapping);
            sb.AppendLine();
            sb.AppendLine("# Available `dotcraft` MCP tools");
            foreach (var (wireName, function) in _byWire)
            {
                sb.AppendLine();
                sb.Append("### `").Append(wireName).Append('`');
                if (!string.Equals(wireName, function.Name, StringComparison.Ordinal))
                    sb.Append(" (maps from `").Append(function.Name).Append("`)");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(function.Description))
                {
                    sb.AppendLine(function.Description.Trim());
                    sb.AppendLine();
                }

                sb.AppendLine("Parameters (JSON Schema):");
                sb.AppendLine("```json");
                sb.AppendLine(JsonNode.Parse(function.JsonSchema.GetRawText())!.ToJsonString(AntigravityJson.Compact));
                sb.AppendLine("```");
            }
            return sb.ToString().TrimEnd();
        }
    }

    public AntigravityTools(IEnumerable<AITool> tools)
    {
        var originals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is not AIFunctionDeclaration function)
                throw new NotSupportedException("Antigravity.ToolType: Only function declarations are supported.");
            if (!originals.Add(function.Name))
                throw new ArgumentException("Antigravity.DuplicateTool: Tool names must be unique.");

            var safe = new string(function.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(function.Name)))[..12].ToLowerInvariant();
            var wire = safe.Length is > 0 and <= 36 && safe == function.Name
                ? safe
                : safe[..Math.Min(safe.Length, 23)] + "_" + hash;

            if (!_byWire.TryAdd(wire, function))
                throw new ArgumentException("Antigravity.ToolCollision: Tool aliases collide.");
        }
    }

    public bool Contains(string wireName) => _byWire.ContainsKey(wireName);

    public string WireName(string hostName) =>
        _byWire.FirstOrDefault(pair => pair.Value.Name == hostName).Key
        ?? throw new ArgumentException("Antigravity.UnknownTool: The required tool is not declared.");

    public string HostName(string wireName) =>
        _byWire.TryGetValue(wireName, out var tool)
            ? tool.Name
            : throw new InvalidDataException($"Antigravity.UnknownTool: The CLI requested undeclared tool '{wireName}'.");

}
