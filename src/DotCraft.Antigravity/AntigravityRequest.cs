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
    public int? MaxOutputTokens { get; }
    public bool AllowAiCredits { get; }
    public string Fingerprint { get; }
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
        MaxOutputTokens = options?.MaxOutputTokens ?? runtime.MaxOutputTokens;

        SystemPrompt = string.Join("\n\n", new[] { options?.Instructions }
            .Concat(Messages.Where(IsInstruction).Select(m => m.Text))
            .Append(Tools.Count > 0
                ? "Use ONLY the declared `dotcraft` MCP tools when taking actions. Tool names in these instructions map to the available MCP tools as follows:\n" + Tools.Mapping
                : "Do not invoke any built-in tools; answer directly with text.")
            .Append(ToolChoice(options?.ToolMode))
            .Append(ResponseFormat(options?.ResponseFormat))
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        History = Snapshot(Messages, reasoning: false);
        Fingerprint = new JsonArray(
            ModelSlug,
            EffortToken,
            SystemPrompt,
            Tools.Schema,
            MaxOutputTokens,
            AllowAiCredits).ToJsonString(AntigravityJson.Compact);
    }

    public string BuildRelayAgentMarkdown()
    {
        var body = string.IsNullOrWhiteSpace(SystemPrompt)
            ? "You are a helpful assistant."
            : SystemPrompt;

        return $"""
            ---
            name: {RelayAgentName}
            description: DotCraft model relay
            mainAgent: true
            subagent: false
            excludeDefaultComponents: true
            inheritCustomizations: false
            tools: []
            ---

            {body}
            """;
    }

    public async Task<(JsonObject Payload, List<string> MediaPaths)> BuildColdInputAsync(
        PrivateDirectory directory,
        CancellationToken ct)
    {
        var conversation = Messages.Where(m => !IsInstruction(m)).ToArray();
        var lastUser = conversation.LastOrDefault()?.Role == ChatRole.User ? conversation[^1] : null;
        var prior = lastUser is null ? conversation : conversation[..^1];
        var mediaPaths = new List<string>();
        var sections = new List<string>();

        if (prior.Length > 0)
        {
            sections.Add("Conversation history (role-tagged JSON):\n" + Snapshot(prior).ToJsonString(AntigravityJson.Compact));
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

        if (lastUser is not null)
        {
            var userText = await RenderMessageContentsAsync(lastUser.Contents, directory, mediaPaths, ct);
            if (!string.IsNullOrEmpty(userText))
                sections.Add(userText);
        }
        else
        {
            sections.Add("Continue the conversation from the history above.");
        }

        var prompt = sections.Count > 0 ? string.Join("\n\n", sections) : "Continue.";
        return (AntigravityJson.UserMessage(prompt), mediaPaths);
    }

    public async Task<(JsonObject Payload, List<string> MediaPaths)?> BuildIncrementalUserInputAsync(
        JsonNode?[] added,
        PrivateDirectory directory,
        CancellationToken ct)
    {
        if (added.Length == 0 || !added.All(n => n?["role"]?.GetValue<string>() == "user"))
            return null;

        // Collect the trailing user messages whose atoms match the newly appended user content.
        var trailingUsers = new List<ChatMessage>();
        for (var i = Messages.Length - 1; i >= 0 && Messages[i].Role == ChatRole.User; i--)
            trailingUsers.Add(Messages[i]);
        trailingUsers.Reverse();

        var trailingAtoms = trailingUsers.SelectMany(m => m.Contents)
            .Where(c => c is not UsageContent and not TextReasoningContent)
            .ToArray();

        // Map from the end of trailing user contents to match newly appended text/media atoms accurately.
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
        return (AntigravityJson.UserMessage(string.Join("\n\n", parts)), mediaPaths);
    }

    public JsonNode?[]? After(JsonArray expected)
    {
        if (History.Count <= expected.Count) return null;
        var prefix = new JsonArray(History.Take(expected.Count).Select(n => n?.DeepClone()).ToArray());
        return JsonNode.DeepEquals(expected, prefix) ? History.Skip(expected.Count).ToArray() : null;
    }

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

    public bool IsBridgedStreamToolName(string? streamToolName)
    {
        if (string.IsNullOrWhiteSpace(streamToolName)) return false;
        var name = streamToolName.Trim();
        if (_byWire.ContainsKey(name)) return true;

        foreach (var prefix in new[] { $"mcp__{ServerName}__", $"mcp_{ServerName}_", $"{ServerName}/", $"{ServerName}:" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && _byWire.ContainsKey(name[prefix.Length..]))
                return true;
        }
        return false;
    }
}
