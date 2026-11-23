using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DotCraft.Antigravity;

internal static partial class AntigravityEnvironment
{
    private static readonly string[] TokenFilenames = ["jetski-standalone-oauth-token", "antigravity-oauth-token"];

    private static readonly HashSet<string> AllowedSecretLikeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SSH_AUTH_SOCK",
        "GPG_AGENT_INFO"
    };

    public static (string Executable, bool Found) ResolveExecutable(string configured)
    {
        var trimmed = configured.Trim();
        if (!string.Equals(trimmed, "agy", StringComparison.OrdinalIgnoreCase))
        {
            if (IsPathLike(trimmed))
                return (trimmed, File.Exists(trimmed));
            var fromPath = FindOnPath(trimmed);
            return fromPath is not null ? (fromPath, true) : (trimmed, false);
        }

        foreach (var envVar in new[] { "ANTIGRAVITY_COMMAND", "AGY_CLI_PATH", "ANTIGRAVITY_CLI_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(envVar)?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                return (value, true);
        }

        if (FindOnPath("agy") is { } resolved)
            return (resolved, true);

        var binary = OperatingSystem.IsWindows() ? "agy.exe" : "agy";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, ".gemini", "antigravity-cli", "bin", binary));
            candidates.Add(Path.Combine(home, ".local", "bin", binary));
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "agy", "bin", binary));
                candidates.Add(Path.Combine(localAppData, "Programs", "agy", binary));
                candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", binary));
            }
        }
        else
        {
            candidates.Add(Path.Combine("/usr/local/bin", binary));
            candidates.Add(Path.Combine("/usr/bin", binary));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return (candidate, true);
        }

        return (trimmed, false);
    }

    public static string? ResolveRealTokenPath(string? explicitConfigDir = null)
    {
        var configuredDir = !string.IsNullOrWhiteSpace(explicitConfigDir)
            ? explicitConfigDir.Trim()
            : Environment.GetEnvironmentVariable("ANTIGRAVITY_CONFIG_DIR")?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredDir))
        {
            foreach (var name in TokenFilenames)
            {
                var path = Path.Combine(configuredDir, name);
                if (IsExistingFile(path)) return path;
            }
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return null;
        var baseDir = Path.Combine(home, ".gemini", "antigravity-cli");
        foreach (var name in TokenFilenames)
        {
            var path = Path.Combine(baseDir, name);
            if (IsExistingFile(path)) return path;
        }
        return null;
    }

    public static async Task<(string IsolatedHome, string IsolatedGeminiDir)> PrepareIsolatedHomeAsync(
        PrivateDirectory directory,
        bool allowAiCredits,
        IEnumerable<string>? allowedMediaPaths,
        CancellationToken ct,
        string? explicitConfigDir = null)
    {
        var isolatedHome = directory.EnsureSubdirectory("home");
        var isolatedGeminiDir = directory.EnsureSubdirectory("home", ".gemini", "antigravity-cli");

        SyncIsolatedToken(isolatedHome, explicitConfigDir);
        LinkMacOsKeychains(isolatedHome, explicitConfigDir);

        await WriteSettingsAsync(directory, allowAiCredits, allowedMediaPaths, ct);
        return (isolatedHome, isolatedGeminiDir);
    }

    public static async Task WriteSettingsAsync(
        PrivateDirectory directory,
        bool allowAiCredits,
        IEnumerable<string>? allowedMediaPaths,
        CancellationToken ct)
    {
        var allow = new JsonArray { "mcp(dotcraft/*)" };
        if (allowedMediaPaths is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in allowedMediaPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
                allow.Add($"read_file({path})");
                allow.Add($"view_file({path})");
            }
        }

        var settings = new JsonObject
        {
            ["agentMode"] = "",
            ["toolPermission"] = "request-review",
            ["enableTerminalSandbox"] = false,
            ["disableSlashCommands"] = true,
            ["useG1Credits"] = allowAiCredits,
            ["permissions"] = new JsonObject
            {
                ["allow"] = allow,
                ["deny"] = new JsonArray
                {
                    "command(*)",
                    "write_file(*)",
                    "read_url(*)",
                    "execute_url(*)"
                }
            }
        };

        await directory.WriteAtomicAsync(
            Path.Combine("home", ".gemini", "antigravity-cli", "settings.json"),
            settings.ToJsonString(AntigravityJson.Pretty),
            ct);
    }

    public static void SyncIsolatedToken(string isolatedHome, string? explicitConfigDir = null)
    {
        var realToken = ResolveRealTokenPath(explicitConfigDir);
        if (realToken is null) return;

        var isolatedGeminiDir = Path.Combine(isolatedHome, ".gemini", "antigravity-cli");
        Directory.CreateDirectory(isolatedGeminiDir);
        var targetName = Path.GetFileName(realToken);
        var isolatedToken = Path.Combine(isolatedGeminiDir, targetName);

        if (PathsEqual(realToken, isolatedToken)) return;

        foreach (var candidateName in TokenFilenames)
        {
            if (string.Equals(candidateName, targetName, StringComparison.Ordinal)) continue;
            var stale = Path.Combine(isolatedGeminiDir, candidateName);
            TryDeleteLink(stale);
        }

        try
        {
            if (File.Exists(isolatedToken))
            {
                var info = new FileInfo(isolatedToken);
                if (info.LinkTarget is not null && PathsEqual(info.LinkTarget, realToken))
                    return;
                if (info.LinkTarget is not null)
                    info.Delete();
                else if (FilesEqual(isolatedToken, realToken))
                    return;
                else
                    File.Delete(isolatedToken);
            }

            try
            {
                File.CreateSymbolicLink(isolatedToken, realToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                File.Copy(realToken, isolatedToken, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static Dictionary<string, string> BuildChildEnvironment(
        string? isolatedHome,
        IReadOnlyDictionary<string, string>? overrides = null,
        string? explicitConfigDir = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value) continue;
            if (ShouldStripEnvironmentVariable(name)) continue;
            env[name] = value;
        }

        if (!string.IsNullOrWhiteSpace(isolatedHome))
        {
            env["HOME"] = isolatedHome;
            env["USERPROFILE"] = isolatedHome;
            if (OperatingSystem.IsWindows())
            {
                env.Remove("HOMEDRIVE");
                env.Remove("HOMEPATH");
            }
        }

        if (!env.ContainsKey("AGY_CLI_DISABLE_AUTO_UPDATE"))
            env["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";

        if (!OperatingSystem.IsWindows() && ResolveRealTokenPath(explicitConfigDir) is null)
        {
            env.Remove("SSH_CONNECTION");
            env.Remove("SSH_CLIENT");
            env.Remove("SSH_TTY");
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                env[key] = value;
        }

        return env;
    }

    private static bool ShouldStripEnvironmentVariable(string name)
    {
        if (AllowedSecretLikeNames.Contains(name)) return false;
        if (name.StartsWith("AGY_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ANTIGRAVITY_", StringComparison.OrdinalIgnoreCase))
            return false;
        return SecretEnvRegex().IsMatch(name);
    }

    private static void LinkMacOsKeychains(string isolatedHome, string? explicitConfigDir)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (!string.IsNullOrWhiteSpace(explicitConfigDir)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_CONFIG_DIR")))
            return;

        var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(realHome)) return;
        var realKeychains = Path.Combine(realHome, "Library", "Keychains");
        if (!Directory.Exists(realKeychains)) return;

        var isolatedKeychains = Path.Combine(isolatedHome, "Library", "Keychains");
        if (PathsEqual(realKeychains, isolatedKeychains)) return;

        try
        {
            if (Directory.Exists(isolatedKeychains))
            {
                var info = new DirectoryInfo(isolatedKeychains);
                if (info.LinkTarget is null) return;
                if (PathsEqual(info.LinkTarget, realKeychains)) return;
                info.Delete();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(isolatedKeychains)!);
            Directory.CreateSymbolicLink(isolatedKeychains, realKeychains);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    private static bool IsPathLike(string value) =>
        Path.IsPathRooted(value) || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar);

    private static string? FindOnPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [".EXE", ".CMD", ".BAT", ".COM"])
            : [string.Empty];

        var hasExt = Path.HasExtension(command);
        foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = rawDir.Trim('"');
            if (string.IsNullOrWhiteSpace(dir)) continue;

            if (hasExt || !OperatingSystem.IsWindows())
            {
                var full = Path.Combine(dir, command);
                if (IsExistingFile(full)) return full;
            }
            else
            {
                foreach (var ext in extensions)
                {
                    var full = Path.Combine(dir, command + ext.ToLowerInvariant());
                    if (IsExistingFile(full)) return full;
                }
            }
        }
        return null;
    }

    private static bool IsExistingFile(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null)
                info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            var fullA = Path.GetFullPath(a);
            var fullB = Path.GetFullPath(b);
            return string.Equals(fullA, fullB, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        try
        {
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"(^|_)(TOKENS?|SECRETS?|PASSWORD|PASSWD|PASSPHRASE|API_?KEY|ACCESS_?KEY|PRIVATE_?KEY|CREDENTIALS?|AUTH|WEBHOOKS?|DSN|COOKIES?)($|_)|(^|_)(DATABASE|DB|REDIS|MONGO\w*|POSTGRES\w*|MYSQL|AMQP|BROKER|CACHE)_(URL|URI)($|_)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretEnvRegex();
}
