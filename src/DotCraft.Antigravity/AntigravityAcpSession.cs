using System.Text.Json;
using DotHarness.Acp.V1;

namespace DotCraft.Antigravity;

internal sealed class AntigravityAcpSession(AcpV1Client client)
{
    private static readonly string ClientVersion =
        typeof(AntigravityAcpSession).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private SessionConfigOption[] _config = [];

    public string Id { get; private set; } = "";
    public bool IsOpen => Id.Length > 0 && !client.Connection.Completion.IsCompleted;
    public bool SupportsImagePrompt { get; private set; }

    public async Task OpenAsync(string workingDirectory, McpServer[] servers, string? resumeId, CancellationToken ct)
    {
        var init = await client.InitializeAsync(new InitializeRequest
        {
            ProtocolVersion = 1,
            ClientCapabilities = new ClientCapabilities(),
            ClientInfo = new Implementation { Name = "dotcraft", Version = ClientVersion }
        }, ct);
        var capabilities = init.AgentCapabilities.GetValueOrDefault();
        SupportsImagePrompt = capabilities?.PromptCapabilities.GetValueOrDefault()?.Image.GetValueOrDefault() == true;
        if (servers.Length > 0 && capabilities?.McpCapabilities.GetValueOrDefault()?.Http.GetValueOrDefault() != true)
            throw new InvalidDataException("Antigravity.AcpCapabilities: The CLI does not support the required ACP capabilities.");

        // ACP resume does not restore the relay from the working directory's MCP configuration.
        if (resumeId is not null)
        {
            if (capabilities?.SessionCapabilities.GetValueOrDefault()?.Resume.GetValueOrDefault() is null)
                throw new InvalidDataException("Antigravity.AcpResumeUnsupported: The CLI does not support ACP resume.");

            var resumed = await client.Agent.SessionResumeAsync(new ResumeSessionRequest
            {
                SessionId = resumeId,
                Cwd = workingDirectory,
                McpServers = servers
            }, ct);
            var returnedId = resumed.ExtensionData is not null
                && resumed.ExtensionData.TryGetValue("sessionId", out var sid)
                && sid.ValueKind == JsonValueKind.String
                ? sid.GetString()
                : null;
            if (returnedId is not null && returnedId != resumeId)
                throw new InvalidDataException("Antigravity.ResumeMismatch: The CLI opened a different conversation.");
            Id = returnedId ?? resumeId;
            _config = resumed.ConfigOptions.GetValueOrDefault() ?? [];
        }
        else
        {
            var created = await client.Agent.SessionNewAsync(new NewSessionRequest
            {
                Cwd = workingDirectory,
                McpServers = servers
            }, ct);
            if (string.IsNullOrEmpty(created.SessionId.Value))
                throw new InvalidDataException("Antigravity.MissingSession: The CLI did not return a session ID.");
            Id = created.SessionId.Value;
            _config = created.ConfigOptions.GetValueOrDefault() ?? [];
        }
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

    public Task<PromptResponse> PromptAsync(ContentBlock[] content, CancellationToken ct) =>
        client.Agent.SessionPromptAsync(new PromptRequest { SessionId = Id, Prompt = content }, ct);

    public Task CancelAsync(CancellationToken ct) =>
        client.Agent.SessionCancelAsync(new CancelNotification { SessionId = Id }, ct);

    public static ContentBlock[] TextPrompt(string text) => [ContentBlock.FromText(text)];

    public static McpServer[] McpServers(AntigravityMcpServer? mcp) =>
        mcp is null ? [] :
        [
            new McpServerHttp
            {
                Name = AntigravityTools.ServerName,
                Url = mcp.Address,
                Headers = [new HttpHeader { Name = "Authorization", Value = mcp.AuthorizationHeader }]
            }
        ];

    private SessionConfigOptionSelectPayload? Option(string category) =>
        _config.OfType<SessionConfigOption.Select>()
            .Select(option => option.Value)
            .FirstOrDefault(option =>
                option.Category.GetValueOrDefault()?.Value == category || option.Id.Value == category);

    private static IEnumerable<string> Values(SessionConfigOptionSelectPayload option) =>
        option.Options switch
        {
            SessionConfigSelectOptions.Ungrouped flat => flat.Value.Select(item => item.Value.Value),
            SessionConfigSelectOptions.Grouped grouped => grouped.Value.SelectMany(group => group.Options).Select(item => item.Value.Value),
            _ => []
        };

    private async Task SetOptionAsync(SessionConfigOptionSelectPayload option, string value, CancellationToken ct)
    {
        if (option.CurrentValue.Value == value) return;
        var result = await client.Agent.SessionSetConfigOptionAsync(new SetSessionConfigOptionRequestValueIdPayload
        {
            SessionId = Id,
            ConfigId = option.Id,
            Value = value
        }, ct);
        _config = result.ConfigOptions;
    }
}
