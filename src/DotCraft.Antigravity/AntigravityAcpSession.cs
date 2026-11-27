using System.Text.Json.Nodes;

namespace DotCraft.Antigravity;

internal sealed class AntigravityAcpSession(AntigravityAcpConnection connection)
{
    private JsonArray _config = [];

    public string Id { get; private set; } = "";
    public bool IsOpen => Id.Length > 0 && !connection.Completion.IsCompleted;

    public async Task OpenAsync(string workingDirectory, JsonArray servers, string? resumeId, CancellationToken ct)
    {
        var init = await connection.SendRequestAsync("initialize", new()
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "dotcraft" }
        }, ct);
        var capabilities = init["agentCapabilities"];
        if (init["protocolVersion"]?.GetValue<int>() != 1
            || (servers.Count > 0 && capabilities?["mcpCapabilities"]?["http"]?.GetValue<bool>() != true))
            throw new InvalidDataException("Antigravity.AcpCapabilities: The CLI does not support the required ACP capabilities.");

        // ACP resume does not restore the relay from the working directory's MCP configuration.
        var input = new JsonObject { ["cwd"] = workingDirectory, ["mcpServers"] = servers };
        if (resumeId is not null)
        {
            if ((capabilities?["sessionCapabilities"] as JsonObject)?.ContainsKey("resume") != true)
                throw new InvalidDataException("Antigravity.AcpResumeUnsupported: The CLI does not support ACP resume.");
            input["sessionId"] = resumeId;
        }

        var opened = await connection.SendRequestAsync(resumeId is null ? "session/new" : "session/resume", input, ct);
        var returnedId = opened["sessionId"]?.GetValue<string>();
        if (resumeId is not null && returnedId is not null && returnedId != resumeId)
            throw new InvalidDataException("Antigravity.ResumeMismatch: The CLI opened a different conversation.");
        Id = returnedId ?? resumeId ?? throw new InvalidDataException("Antigravity.MissingSession: The CLI did not return a session ID.");
        _config = opened["configOptions"] as JsonArray ?? [];
    }

    public async Task SelectModelAsync(AntigravityRequest request, CancellationToken ct)
    {
        var model = Option("model");
        if (model is null)
            throw new InvalidDataException("Antigravity.AcpModelConfig: The CLI did not publish a model selector.");
        var (baseId, _, _) = AntigravityModels.SplitEffortSuffix(request.ModelSlug);
        var values = Values(model).ToHashSet(StringComparer.Ordinal);
        var selectedModel = values.Contains(baseId) ? baseId : values.Contains(request.ModelSlug) ? request.ModelSlug : baseId;
        await SetOptionAsync(model, selectedModel, ct);

        if (request.EffortToken is { } effort && Option("thought_level") is { } thought)
        {
            var levels = Values(thought).ToHashSet(StringComparer.Ordinal);
            if (!levels.Contains(effort))
                throw new InvalidDataException("Antigravity.AcpEffortConfig: The selected model does not offer the requested thinking level.");
            await SetOptionAsync(thought, effort, ct);
        }
    }

    public Task<JsonObject> PromptAsync(JsonArray content, CancellationToken ct) =>
        connection.SendRequestAsync("session/prompt", new() { ["sessionId"] = Id, ["prompt"] = content }, ct);

    public Task CancelAsync(CancellationToken ct) =>
        connection.SendNotificationAsync("session/cancel", new() { ["sessionId"] = Id }, ct);

    public static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    public static JsonArray TextPrompt(string text) => new(TextBlock(text));

    public static JsonArray McpServers(AntigravityMcpServer? mcp)
    {
        if (mcp is null) return [];
        return new(new JsonObject
        {
            ["type"] = "http",
            ["name"] = AntigravityTools.ServerName,
            ["url"] = mcp.Address,
            ["headers"] = new JsonArray(new JsonObject
            {
                ["name"] = "Authorization",
                ["value"] = mcp.AuthorizationHeader
            })
        });
    }

    private JsonObject? Option(string category) => _config.OfType<JsonObject>()
        .FirstOrDefault(option => option["category"]?.GetValue<string>() == category || option["id"]?.GetValue<string>() == category);

    private static IEnumerable<string> Values(JsonObject option) =>
        (option["options"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(item => item["options"] is JsonArray group ? group.OfType<JsonObject>() : [item])
            .Select(item => item["value"]?.GetValue<string>()).OfType<string>();

    private async Task SetOptionAsync(JsonObject option, string value, CancellationToken ct)
    {
        if (option["currentValue"]?.GetValue<string>() == value) return;
        var result = await connection.SendRequestAsync("session/set_config_option", new()
        {
            ["sessionId"] = Id,
            ["configId"] = option["id"]?.GetValue<string>(),
            ["value"] = value
        }, ct);
        if (result["configOptions"] is JsonArray updated)
            _config = updated;
    }
}
