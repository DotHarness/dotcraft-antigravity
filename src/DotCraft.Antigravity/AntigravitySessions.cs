using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DotCraft.Antigravity;

internal sealed record AntigravityCheckpoint(
    string ConversationId,
    string Fingerprint,
    int HistoryCount,
    string HistoryHash,
    string[] MediaFiles)
{
    public static AntigravityCheckpoint Capture(string conversationId, string fingerprint, JsonArray history, IEnumerable<string> media) =>
        new(conversationId, AntigravityRequest.Hash(fingerprint), history.Count, AntigravityRequest.HistoryHash(history),
            media.Select(Path.GetFileName).OfType<string>().ToArray());
}

internal sealed class AntigravitySessions(string root, TimeProvider clock)
{
    internal const string CheckpointFile = "checkpoint-v1.json";
    private const int MaxShelvedSessions = 64;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    internal static string WorkspaceRoot(string dataRoot, string workspace)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        return Path.Combine(dataRoot, "workspaces", AntigravityRequest.Hash(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path));
    }

    public async Task SaveAsync(string key, PrivateDirectory directory, AntigravityCheckpoint checkpoint)
    {
        try
        {
            AntigravityEnvironment.RemoveIsolatedTokens(directory.Path);
            File.Delete(Path.Combine(directory.Path, "work", ".agents", "mcp_config.json"));
            var saved = new SavedAntigravitySession(key, clock.GetUtcNow(), checkpoint);
            var json = JsonSerializer.Serialize(saved, AntigravitySessionJson.Default.SavedAntigravitySession);
            await directory.WriteAtomicAsync(CheckpointFile, json, CancellationToken.None);
            directory.ReleaseRun();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await directory.DisposeAsync();
        }
    }

    public async Task<(PrivateDirectory Directory, AntigravityCheckpoint Checkpoint)?> TakeAsync(string key)
    {
        foreach (var path in Candidates())
        {
            var directory = PrivateDirectory.TryOpenRun(path);
            if (directory is null) continue;

            var saved = Read(path);
            if (saved?.Key != key)
            {
                directory.ReleaseRun();
                continue;
            }

            try
            {
                File.Delete(Path.Combine(path, CheckpointFile));
                if (!Expired(saved))
                    return (directory, saved.Checkpoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            await directory.DisposeAsync();
        }

        return null;
    }

    public async Task PruneAsync(bool clear = false, bool sweepOrphans = false)
    {
        var kept = 0;
        foreach (var path in Candidates(sweepOrphans))
        {
            var directory = PrivateDirectory.TryOpenRun(path);
            if (directory is null) continue;

            if (!clear && Read(path) is { } saved && !Expired(saved) && ++kept <= MaxShelvedSessions)
            {
                directory.ReleaseRun();
                continue;
            }

            if (sweepOrphans)
                AntigravityProcess.TryReapRecordedProcess(path);
            try { File.Delete(Path.Combine(path, CheckpointFile)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await directory.DisposeAsync();
        }
    }

    private IEnumerable<string> Candidates(bool includeOrphans = false) =>
        Directory.EnumerateDirectories(root, "run-*")
            .Select(path => (Path: path, Checkpoint: new FileInfo(Path.Combine(path, CheckpointFile))))
            .Where(entry => includeOrphans || entry.Checkpoint.Exists)
            .OrderByDescending(entry => entry.Checkpoint.LastWriteTimeUtc)
            .Select(entry => entry.Path);

    private bool Expired(SavedAntigravitySession saved) => clock.GetUtcNow() - saved.SavedAt >= Lifetime;

    private static SavedAntigravitySession? Read(string path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(path, CheckpointFile)),
                AntigravitySessionJson.Default.SavedAntigravitySession);
            if (saved is not { Key.Length: > 0, Checkpoint: { ConversationId.Length: > 0, HistoryCount: > 0,
                    Fingerprint.Length: 64, HistoryHash.Length: 64, MediaFiles: not null } checkpoint }
                || checkpoint.MediaFiles.Any(file => string.IsNullOrWhiteSpace(file) || file is "." or ".." || file != Path.GetFileName(file)))
                return null;
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

internal sealed record SavedAntigravitySession(string Key, DateTimeOffset SavedAt, AntigravityCheckpoint Checkpoint);

[JsonSerializable(typeof(SavedAntigravitySession))]
internal sealed partial class AntigravitySessionJson : JsonSerializerContext;
