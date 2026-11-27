using System.Text.Json.Nodes;

namespace DotCraft.Antigravity;

internal sealed class NativeToolBlockedException(string message) : InvalidOperationException(message);

internal static class AntigravityAcpToolPolicy
{
    public static bool Allows(JsonObject tool, AntigravityTools tools, IReadOnlySet<string> allowedPaths)
    {
        const string prefix = "mcp_dotcraft_";
        var name = tool["name"]?.GetValue<string>();
        var title = tool["title"]?.GetValue<string>();
        if (name is null && title?.StartsWith("Run ", StringComparison.Ordinal) == true)
            name = title[4..];
        if (name?.StartsWith(prefix, StringComparison.Ordinal) == true)
            return tools.Contains(name[prefix.Length..]);

        var input = tool["rawInput"] as JsonObject ?? tool["args"] as JsonObject;
        if (input is null) return false;
        if (name == "call_mcp_tool")
        {
            var server = (input["ServerName"] ?? input["serverName"] ?? input["server_name"])?.GetValue<string>();
            var target = (input["ToolName"] ?? input["toolName"] ?? input["tool_name"])?.GetValue<string>();
            return server == AntigravityTools.ServerName && target is not null && tools.Contains(target);
        }
        if (name is not ("read_file" or "view_file")) return false;
        var path = (input["Path"] ?? input["path"])?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        try
        {
            return allowedPaths.Contains(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
