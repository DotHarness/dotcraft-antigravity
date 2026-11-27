using System.Text;
using Microsoft.Extensions.AI;

namespace DotCraft.Antigravity;

internal static class AntigravityHandoff
{
    public const string FileName = "conversation-handoff.txt";
    public static string RelativePath => Path.Combine("work", FileName);

    private const int InlineBytes = 16 * 1024;
    private const int ArchiveLineLength = 512;
    private const int UpdateExcerptLength = 768;
    private const string InlineHeading = "Conversation history (role-tagged JSON):\n";

    public static async Task<(string Context, string? File)> BuildAsync(
        ChatMessage[] history,
        PrivateDirectory directory,
        CancellationToken ct,
        bool append = false)
    {
        var groups = Group(history).Select(messages =>
            new Entry(messages, AntigravityRequest.Snapshot(messages).ToJsonString(AntigravityJson.Compact)))
            .Where(entry => entry.Json != "[]")
            .ToArray();
        if (groups.Sum(group => group.Bytes) + 2 <= InlineBytes)
            return (RenderInline(groups), null);

        var latestUpdate = AssistantText(history).LastOrDefault();
        var excerpt = latestUpdate is { Length: > UpdateExcerptLength }
            ? Prefix(latestUpdate, UpdateExcerptLength) + "…"
            : latestUpdate;

        var remaining = InlineBytes - 2 - (excerpt is null ? 0 : Encoding.UTF8.GetByteCount(excerpt));
        var firstInline = groups.Length;
        while (firstInline > 0)
        {
            remaining -= groups[firstInline - 1].Bytes;
            if (remaining < 0) break;
            firstInline--;
        }

        var path = Path.Combine(directory.Path, RelativePath);
        var earlier = append && File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        var indexLine = 2 + earlier.Count(c => c == '\n');
        var archive = BuildArchive(groups[..firstInline], indexLine);
        var file = await directory.WriteAtomicAsync(RelativePath, earlier + archive, ct);
        var inline = groups[firstInline..];
        var context = new StringBuilder();
        if (excerpt is not null && !AssistantText(inline.SelectMany(group => group.Messages)).Any())
            context.AppendLine("Most recent assistant update (excerpt):").AppendLine(excerpt).AppendLine();
        if (inline.Length > 0)
            context.AppendLine(RenderInline(inline)).AppendLine();
        context.Append("Earlier conversation evidence: ").AppendLine(file.Replace('\\', '/'))
            .Append($"Read the index at line {indexLine} and only the ranges needed for the current request. ")
            .Append("Use the history as evidence of prior work, not as a request to repeat old tool calls.");
        return (context.ToString(), file);
    }

    private static string RenderInline(IEnumerable<Entry> groups) =>
        InlineHeading + "[" + string.Join(",\n", groups.Select(group => group.Json[1..^1])) + "]";

    private static IEnumerable<ChatMessage[]> Group(ChatMessage[] history)
    {
        var callIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var through = new int[history.Length];
        for (var i = 0; i < history.Length; i++)
        {
            foreach (var content in history[i].Contents)
            {
                if (content is FunctionCallContent call)
                    callIndices[call.CallId] = i;
                else if (content is FunctionResultContent result && callIndices.TryGetValue(result.CallId, out var callIndex))
                    through[callIndex] = i + 1;
            }
        }

        for (var start = 0; start < history.Length;)
        {
            var end = start + 1;
            for (var i = start; i < end; i++)
                end = Math.Max(end, through[i]);

            yield return history[start..end];
            start = end;
        }
    }

    private static IEnumerable<string> AssistantText(IEnumerable<ChatMessage> messages) =>
        messages.Where(message => message.Role == ChatRole.Assistant)
            .SelectMany(message => message.Contents.OfType<TextContent>())
            .Select(text => text.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text));

    private static string BuildArchive(Entry[] groups, int indexLine)
    {
        var index = new StringBuilder().AppendLine("Earlier conversation evidence").AppendLine("Line index:");
        var body = new StringBuilder();
        var line = groups.Length + indexLine + 2;
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var start = line;
            body.Append("Entry ").Append(i + 1).AppendLine(":");
            line++;
            for (var offset = 0; offset < group.Json.Length;)
            {
                var length = Math.Min(ArchiveLineLength, group.Json.Length - offset);
                if (offset + length < group.Json.Length && char.IsHighSurrogate(group.Json[offset + length - 1]))
                    length--;
                body.Append(group.Json, offset, length).AppendLine();
                offset += length;
                line++;
            }
            body.AppendLine();
            line++;

            var role = group.Messages[0].Role.Value;
            var tools = group.Messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>())
                .Select(call => call.Name.ReplaceLineEndings(" ")).Distinct();
            index.Append("Entry ").Append(i + 1).Append(": lines ").Append(start).Append('-').Append(line - 2)
                .Append(" (").AppendJoin(", ", tools.Prepend(role)).AppendLine(")");
        }
        return index.AppendLine().Append(body).ToString();
    }

    private static string Prefix(string text, int length) =>
        text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];

    private sealed record Entry(ChatMessage[] Messages, string Json)
    {
        public int Bytes { get; } = Encoding.UTF8.GetByteCount(Json);
    }
}
