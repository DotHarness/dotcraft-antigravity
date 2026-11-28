using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace DotCraft.Antigravity;

internal static class AntigravityContent
{
    private static readonly Dictionary<string, string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
        ["image/bmp"] = ".bmp",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/ogg"] = ".ogg",
        ["audio/flac"] = ".flac",
        ["audio/mp4"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm"
    };

    public static bool IsSupportedMedia(DataContent data) =>
        !string.IsNullOrWhiteSpace(data.MediaType) && (
            data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            data.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
            data.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));

    public static async Task<(string Path, string Reference)> MaterializeMediaAsync(
        PrivateDirectory directory,
        DataContent data,
        CancellationToken ct)
    {
        if (!IsSupportedMedia(data))
            throw new NotSupportedException($"Antigravity.MediaType: Unsupported attachment media type '{data.MediaType}'.");

        var ext = ResolveExtension(data.MediaType);
        var hash = Convert.ToHexString(SHA256.HashData(data.Data.Span))[..16].ToLowerInvariant();
        var relative = Path.Combine("media", $"{hash}{ext}");
        var fullPath = await directory.WriteBytesAsync(relative, data.Data, ct);
        var label = data.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? "Audio"
            : data.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? "Video"
                : "Image";
        return (fullPath, $"[{label}]({fullPath})");
    }

    public static IEnumerable<AIContent> Expand(AIContent content) => content is FunctionResultContent result
        ? RichResult(result.Result)
        : [content];

    public static JsonNode? HistoryResult(object? result) => result switch
    {
        AIContent content => HistoryItem(content),
        IEnumerable<AIContent> contents => new JsonArray(contents.Select(HistoryItem).ToArray()),
        JsonElement el => JsonNode.Parse(el.GetRawText()),
        _ => AntigravityJson.Value(result)
    };

    public static JsonObject MediaSummary(AIContent content) => content switch
    {
        DataContent data when IsSupportedMedia(data) => new()
        {
            ["type"] = "media",
            ["mediaType"] = data.MediaType,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(data.Data.Span))[..16].ToLowerInvariant(),
            ["bytes"] = data.Data.Length
        },
        UriContent uri => new()
        {
            ["type"] = "uri",
            ["mediaType"] = uri.MediaType ?? "",
            ["uri"] = uri.Uri.ToString()
        },
        _ => throw new NotSupportedException($"Antigravity.ContentType: Unsupported content '{content.GetType().Name}'.")
    };

    public static CallToolResult McpResult(object? value, bool isError)
    {
        var contents = new List<ContentBlock>();
        if (value is AIContent or IEnumerable<AIContent>)
        {
            foreach (var item in RichResult(value))
            {
                contents.Add(item switch
                {
                    TextContent text => new TextContentBlock { Text = text.Text },
                    DataContent data when data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) =>
                        ImageContentBlock.FromBytes(data.Data, data.MediaType),
                    UriContent uri => new TextContentBlock { Text = uri.Uri.ToString() },
                    _ => new TextContentBlock { Text = item.ToString() ?? string.Empty }
                });
            }
        }
        else if (value is JsonElement el)
        {
            contents.Add(new TextContentBlock { Text = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText() });
        }
        else
        {
            contents.Add(new TextContentBlock { Text = value as string ?? AntigravityJson.Value(value)?.ToJsonString() ?? "null" });
        }

        if (contents.Count == 0)
            contents.Add(new TextContentBlock { Text = string.Empty });

        return new() { Content = contents, IsError = isError };
    }

    private static IEnumerable<AIContent> RichResult(object? result) => result switch
    {
        AIContent content => [content],
        IEnumerable<AIContent> contents => contents,
        _ => []
    };

    private static JsonNode HistoryItem(AIContent content) => content switch
    {
        TextContent text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        DataContent or UriContent => MediaSummary(content),
        _ => new JsonObject { ["type"] = content.GetType().Name, ["text"] = content.ToString() ?? "" }
    };

    public static string CleanMediaType(string mediaType)
    {
        var semi = mediaType.IndexOf(';');
        return semi >= 0 ? mediaType[..semi].Trim() : mediaType.Trim();
    }

    private static string ResolveExtension(string mediaType) =>
        MediaExtensions.TryGetValue(CleanMediaType(mediaType), out var ext) ? ext : ".bin";
}
