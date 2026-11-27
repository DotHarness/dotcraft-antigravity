using System.Text.Json;
using DotCraft.Contributions;
using DotCraft.Plugins;

namespace DotCraft.Antigravity;

public sealed class Plugin : IDotCraftPlugin
{
    public ValueTask ActivateAsync(IPluginActivationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = AntigravitySettings.Read(context.Settings);
        var root = AntigravitySessions.WorkspaceRoot(context.DataRoot, context.WorkspaceRoot);
        var backend = new AntigravityBackend(settings, root, context.Lifetime.Stopping);
        context.Lifetime.OwnAsync(backend);
        context.Lifetime.Run(backend.SuperviseAsync);
        context.Contributions.Add<IModelBackendSource>(backend);
        context.Contributions.Add<ITurnLifecycleContributor>(backend);
        return ValueTask.CompletedTask;
    }
}

internal sealed record AntigravitySettings(
    string ExecutablePath = "agy",
    int IdleTimeoutSeconds = 900,
    bool AllowAiCredits = false,
    int RequestTimeoutSeconds = 300)
{
    public static AntigravitySettings Read(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return new AntigravitySettings();

        var executable = ReadString(json, "executablePath", "agy");
        var idle = ReadInt(json, "idleTimeoutSeconds", 900);
        var credits = ReadBool(json, "allowAiCredits", false);
        var timeout = ReadInt(json, "requestTimeoutSeconds", 300);

        if (string.IsNullOrWhiteSpace(executable) || idle is < 10 or > 3600 || timeout is < 30 or > 1800)
            throw new ArgumentException("Antigravity.InvalidSettings: executablePath, idleTimeoutSeconds, or requestTimeoutSeconds is invalid.");

        return new(executable.Trim(), idle, credits, timeout);
    }

    private static string ReadString(JsonElement json, string name, string fallback) =>
        json.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? fallback
            : fallback;

    private static int ReadInt(JsonElement json, string name, int fallback) =>
        json.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)
            ? value
            : fallback;

    private static bool ReadBool(JsonElement json, string name, bool fallback) =>
        json.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : fallback;
}
