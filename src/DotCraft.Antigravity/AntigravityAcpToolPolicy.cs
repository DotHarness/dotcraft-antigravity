using System.Text.Json;
using DotHarness.Acp.V1;

namespace DotCraft.Antigravity;

internal sealed class NativeToolBlockedException(string message) : InvalidOperationException(message);

internal static class AntigravityAcpToolPolicy
{
    public static bool Allows(
        ToolCall tool,
        AntigravityTools tools,
        IReadOnlySet<string> allowedPaths,
        string? workDirectory) =>
        Allows(tool.Name.GetValueOrDefault(), tool.Title, tool.RawInput.GetValueOrDefault(), tool.ExtensionData, tools, allowedPaths, workDirectory);

    public static bool Allows(
        ToolCallUpdate tool,
        AntigravityTools tools,
        IReadOnlySet<string> allowedPaths,
        string? workDirectory) =>
        Allows(tool.Name.GetValueOrDefault(), tool.Title.GetValueOrDefault(), tool.RawInput.GetValueOrDefault(), tool.ExtensionData, tools, allowedPaths, workDirectory);

    private static bool Allows(
        string? name,
        string? title,
        JsonElement rawInput,
        Dictionary<string, JsonElement>? extensionData,
        AntigravityTools tools,
        IReadOnlySet<string> allowedPaths,
        string? workDirectory)
    {
        const string prefix = "mcp_dotcraft_";
        if (name is null && title?.StartsWith("Run ", StringComparison.Ordinal) == true)
            name = title[4..];
        if (name?.StartsWith(prefix, StringComparison.Ordinal) == true)
            return tools.Contains(name[prefix.Length..]);

        JsonElement input;
        if (rawInput.ValueKind == JsonValueKind.Object)
            input = rawInput;
        else if (extensionData is not null && extensionData.TryGetValue("args", out var args) && args.ValueKind == JsonValueKind.Object)
            input = args;
        else
            return false;

        if (name == "call_mcp_tool")
        {
            var server = StringProperty(input, "ServerName", "serverName", "server_name");
            var target = StringProperty(input, "ToolName", "toolName", "tool_name");
            return server == AntigravityTools.ServerName && target is not null && tools.Contains(target);
        }
        if (name is not ("read_file" or "view_file")) return false;
        var path = StringProperty(input, "Path", "path", "AbsolutePath");
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var fullPath = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : string.IsNullOrWhiteSpace(workDirectory) ? null : Path.GetFullPath(path, workDirectory);
            if (fullPath is null)
                return false;
            if (allowedPaths.Contains(fullPath))
                return true;
            if (string.IsNullOrWhiteSpace(workDirectory))
                return false;
            var rulesPath = Path.GetFullPath(Path.Combine(workDirectory, AntigravityRequest.RulesFileName));
            return string.Equals(fullPath, rulesPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static string? StringProperty(JsonElement obj, params ReadOnlySpan<string> names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }
}
