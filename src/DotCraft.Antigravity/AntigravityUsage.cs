using System.Globalization;
using System.Text;
using System.Text.Json;
using DotCraft.Agents;

namespace DotCraft.Antigravity;

internal sealed class AntigravityUsage(
    AntigravitySettings settings,
    string root,
    AntigravityLimits limits,
    CancellationToken stopping = default,
    TimeProvider? clock = null,
    string? configDirectory = null)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StaleMaxAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    private static readonly Dictionary<string, string> CanonicalGroupPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini models"] = "Gemini",
        ["gemini model"] = "Gemini",
        ["claude and gpt models"] = "Claude / GPT",
        ["claude and gpt model"] = "Claude / GPT"
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _cacheGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly string _diskCachePath = ResolveDiskCachePath(root, configDirectory);
    private ProviderUsageSnapshot? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private int _backgroundRefreshRunning;

    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _cached = null;
            _cachedAt = DateTimeOffset.MinValue;
        }
        try
        {
            if (File.Exists(_diskCachePath))
                File.Delete(_diskCachePath);
        }
        catch
        {
        }
    }

    public async Task<ProviderUsageSnapshot?> ReadAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var (limitReachedKind, blockedUntil, validationUrl) = limits.Snapshot();
        if (limitReachedKind is not null)
            return BuildBlockedSnapshot(now, limitReachedKind, blockedUntil, validationUrl);

        ProviderUsageSnapshot? cached;
        DateTimeOffset cachedAt;
        lock (_cacheGate)
        {
            cached = _cached;
            cachedAt = _cachedAt;
        }

        if (cached is null || now - cachedAt >= CacheTtl)
        {
            if (TryLoadDiskCache() is { } disk && disk.CachedAt > cachedAt)
            {
                lock (_cacheGate)
                {
                    if (disk.CachedAt > _cachedAt)
                    {
                        _cached = disk.Snapshot;
                        _cachedAt = disk.CachedAt;
                    }
                    cached = _cached;
                    cachedAt = _cachedAt;
                }
            }
        }

        if (cached is not null)
        {
            var age = now - cachedAt;
            if (age < CacheTtl)
                return cached;
            if (age < StaleMaxAge)
            {
                TriggerBackgroundRefresh();
                return cached;
            }
        }

        return await RefreshCoreAsync(ct);
    }

    private void TriggerBackgroundRefresh()
    {
        if (stopping.IsCancellationRequested)
            return;
        if (Interlocked.CompareExchange(ref _backgroundRefreshRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshCoreAsync(stopping);
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref _backgroundRefreshRunning, 0);
            }
        }, CancellationToken.None);
    }

    private async Task<ProviderUsageSnapshot?> RefreshCoreAsync(CancellationToken ct)
    {
        var entryTime = _clock.GetUtcNow();
        await _refreshGate.WaitAsync(ct);
        try
        {
            lock (_cacheGate)
            {
                if (_cached is not null && _cachedAt > entryTime)
                    return _cached;
            }

            var fetched = await QueryCliUsageAsync(ct);
            if (fetched is null)
            {
                lock (_cacheGate) return _cached;
            }

            var observedAt = _clock.GetUtcNow();
            ProviderUsageSnapshot updated;
            lock (_cacheGate)
            {
                _cached = fetched.Value.Snapshot;
                _cachedAt = observedAt;
                updated = _cached;
            }
            TrySaveDiskCache(fetched.Value.RawJson, observedAt);
            return updated;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<(ProviderUsageSnapshot Snapshot, string RawJson)?> QueryCliUsageAsync(CancellationToken ct)
    {
        var (executable, found) = AntigravityEnvironment.ResolveExecutable(settings.ExecutablePath);
        if (!found) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);

        await using var dir = new PrivateDirectory(root, "usage-");
        var (isolatedHome, _) = await AntigravityEnvironment.PrepareIsolatedHomeAsync(
            dir, settings.AllowAiCredits, null, timeout.Token, configDirectory, disableSlashCommands: false);
        var env = AntigravityEnvironment.BuildChildEnvironment(isolatedHome, explicitConfigDir: configDirectory);
        var work = dir.EnsureSubdirectory("work");

        try
        {
            await using var process = new AntigravityProcess(
                executable,
                work,
                ["-p", "/usage", "--output-format", "json"],
                env);
            var (exitCode, stdout, _) = await process.ReadToExitAsync(timeout.Token);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;
            var snapshot = ParseUsageJson(stdout, _clock.GetUtcNow());
            return snapshot is null ? null : (snapshot, stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private (ProviderUsageSnapshot Snapshot, DateTimeOffset CachedAt)? TryLoadDiskCache()
    {
        try
        {
            if (!File.Exists(_diskCachePath))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_diskCachePath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var cachedAtRaw = ReadString(doc.RootElement, "cachedAt");
            var rawJson = ReadString(doc.RootElement, "rawJson");
            if (string.IsNullOrWhiteSpace(cachedAtRaw) || string.IsNullOrWhiteSpace(rawJson))
                return null;
            if (!DateTimeOffset.TryParse(cachedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cachedAt))
                return null;
            var snapshot = ParseUsageJson(rawJson, cachedAt);
            return snapshot is null ? null : (snapshot, cachedAt);
        }
        catch
        {
            return null;
        }
    }

    private void TrySaveDiskCache(string rawJson, DateTimeOffset cachedAt)
    {
        try
        {
            var dir = Path.GetDirectoryName(_diskCachePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var payload = JsonSerializer.Serialize(new
            {
                cachedAt = cachedAt.ToString("O", CultureInfo.InvariantCulture),
                rawJson
            });
            var tempPath = _diskCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, payload, Encoding.UTF8);
            File.Move(tempPath, _diskCachePath, overwrite: true);
        }
        catch
        {
        }
    }

    private static string ResolveDiskCachePath(string root, string? configDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
            return Path.Combine(configDirectory, "usage-cache.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(root, "usage-cache.json")
            : Path.Combine(home, ".craft", "cache", "antigravity-usage.json");
    }

    private ProviderUsageSnapshot BuildBlockedSnapshot(
        DateTimeOffset now,
        string limitReachedKind,
        DateTimeOffset? blockedUntil,
        string? validationUrl)
    {
        ProviderUsageSnapshot? cached;
        lock (_cacheGate) cached = _cached;

        var values = cached is not null
            ? new Dictionary<string, double>(cached.Values, StringComparer.Ordinal)
            : new Dictionary<string, double>(StringComparer.Ordinal);
        var labels = cached?.Labels is not null
            ? new Dictionary<string, string>(cached.Labels, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(validationUrl))
            labels["validation_url"] = validationUrl;

        var primary = cached?.Primary ?? (blockedUntil is { } reset
            ? new ProviderRateLimitWindow(100, TimeSpan.FromHours(5), reset)
            : null);

        return new ProviderUsageSnapshot(
            ObservedAt: now,
            Values: values,
            Labels: labels.Count > 0 ? labels : null,
            PlanType: cached?.PlanType,
            Primary: primary,
            Secondary: cached?.Secondary,
            Credits: cached?.Credits,
            LimitReachedKind: limitReachedKind,
            PluginId: "antigravity");
    }

    internal static ProviderUsageSnapshot? ParseUsageJson(string raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var data = FindUsagePayload(raw);
        if (data is not { ValueKind: JsonValueKind.Object } payload) return null;
        if (!payload.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return null;

        var pools = new List<QuotaPool>();
        var idx = 0;

        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            var groupName = ReadString(group, "name") ?? ReadString(group, "displayName") ?? "Quota";
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                var bucketId = ReadString(bucket, "id") ?? ReadString(bucket, "bucketId");
                var window = ReadString(bucket, "window") ?? ReadString(bucket, "name") ?? ReadString(bucket, "displayName");
                var remaining = ReadDouble(bucket, "remaining_fraction") ?? ReadDouble(bucket, "remainingFraction");
                if (remaining is null) continue;

                var clamped = Math.Clamp(remaining.Value, 0.0, 1.0);
                var resetRaw = ReadString(bucket, "reset_time") ?? ReadString(bucket, "resetTime");
                DateTimeOffset? resetAt = !string.IsNullOrWhiteSpace(resetRaw)
                    && DateTimeOffset.TryParse(resetRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedReset)
                    ? parsedReset
                    : null;

                var label = FormatPoolLabel(groupName, window);
                var key = SanitizeKey(bucketId ?? $"{groupName}_{window}", ++idx);
                var duration = ParseWindowDuration(window);
                var isGemini = groupName.Contains("gemini", StringComparison.OrdinalIgnoreCase)
                    || (bucketId?.Contains("gemini", StringComparison.OrdinalIgnoreCase) ?? false);
                pools.Add(new QuotaPool(key, label, clamped, resetAt, duration, isGemini));
            }
        }

        if (pools.Count == 0) return null;

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>(pools.Count);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pool in pools)
        {
            var uniqueKey = pool.Key;
            var suffix = 2;
            while (!usedKeys.Add(uniqueKey))
                uniqueKey = $"{pool.Key}_{suffix++}";

            order.Add(uniqueKey);
            values[$"pool.{uniqueKey}.remaining"] = Math.Round(pool.RemainingFraction * 100.0, 1);
            if (pool.ResetAt is { } resetAt)
                values[$"pool.{uniqueKey}.reset_epoch_ms"] = resetAt.ToUnixTimeMilliseconds();
            labels[$"pool.{uniqueKey}.name"] = pool.DisplayName;
        }
        labels["pool_order"] = string.Join(',', order);

        var summaryPools = pools.Any(p => p.IsGemini)
            ? pools.Where(p => p.IsGemini).ToList()
            : pools;
        var tightest = summaryPools.OrderBy(p => p.RemainingFraction).First();
        var fiveHour = summaryPools.Where(p => p.WindowDuration == TimeSpan.FromHours(5)).OrderBy(p => p.RemainingFraction).FirstOrDefault();
        var weekly = summaryPools.Where(p => p.WindowDuration == TimeSpan.FromDays(7)).OrderBy(p => p.RemainingFraction).FirstOrDefault();

        ProviderRateLimitWindow primary;
        ProviderRateLimitWindow? secondary = null;
        if (fiveHour is not null && weekly is not null)
        {
            primary = ToRateLimitWindow(fiveHour, now);
            secondary = ToRateLimitWindow(weekly, now);
        }
        else
        {
            primary = ToRateLimitWindow(tightest, now);
        }

        var tightestUsedPercent = (int)Math.Clamp(Math.Round((1.0 - tightest.RemainingFraction) * 100.0), 0, 100);
        var planType = ReadString(payload, "plan") ?? ReadString(payload, "plan_type") ?? ReadString(payload, "tier");
        var credits = ParseCredits(payload);
        var limitReached = tightestUsedPercent >= 100 ? "quota_reached" : null;

        return new ProviderUsageSnapshot(
            ObservedAt: now,
            Values: values,
            Labels: labels,
            PlanType: planType,
            Primary: primary,
            Secondary: secondary,
            Credits: credits,
            LimitReachedKind: limitReached,
            PluginId: "antigravity");
    }

    private static ProviderRateLimitWindow ToRateLimitWindow(QuotaPool pool, DateTimeOffset now)
    {
        var usedPercent = (int)Math.Clamp(Math.Round((1.0 - pool.RemainingFraction) * 100.0), 0, 100);
        var duration = pool.WindowDuration ?? TimeSpan.FromHours(5);
        return new ProviderRateLimitWindow(usedPercent, duration, pool.ResetAt ?? now.Add(duration));
    }

    private static JsonElement? FindUsagePayload(string raw)
    {
        var trimmed = raw.Trim();
        if (TryParseAndFindGroups(trimmed) is { } direct)
            return direct;

        foreach (var line in trimmed.Split('\n'))
        {
            var clean = line.Trim();
            if (!clean.StartsWith('{')) continue;
            if (TryParseAndFindGroups(clean) is { } found)
                return found;
        }
        return null;
    }

    private static JsonElement? TryParseAndFindGroups(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindGroupsRecursive(doc.RootElement, 0)?.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? FindGroupsRecursive(JsonElement node, int depth)
    {
        if (node.ValueKind != JsonValueKind.Object || depth > 15) return null;
        if (node.TryGetProperty("groups", out var groups)
            && groups.ValueKind == JsonValueKind.Array
            && groups.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.Object))
        {
            return node;
        }

        foreach (var prop in node.EnumerateObject())
        {
            var found = FindGroupsRecursive(prop.Value, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }

    private static ProviderCreditStatus? ParseCredits(JsonElement payload)
    {
        var amount = ReadDouble(payload, "credits")
            ?? ReadDouble(payload, "ai_credits")
            ?? ReadDouble(payload, "available_credits");
        if (amount is null) return null;
        return new ProviderCreditStatus(
            HasCredits: amount.Value > 0,
            Unlimited: false,
            Balance: amount.Value.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private static string FormatPoolLabel(string groupName, string? window)
    {
        var cleanGroup = string.Join(' ', groupName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var prefix = CanonicalGroupPrefixes.TryGetValue(cleanGroup, out var mapped) ? mapped : cleanGroup;
        if (string.IsNullOrWhiteSpace(window)) return prefix;

        var w = window.Trim();
        var normalizedWindow = w.Equals("5h", StringComparison.OrdinalIgnoreCase)
            ? "5h"
            : w.Equals("weekly", StringComparison.OrdinalIgnoreCase)
                ? "Weekly"
                : w;
        return $"{prefix} ({normalizedWindow})";
    }

    private static TimeSpan? ParseWindowDuration(string? window)
    {
        if (string.IsNullOrWhiteSpace(window)) return null;
        var lower = window.Trim().ToLowerInvariant();
        if (lower.Contains("week") || lower.Contains("7d")) return TimeSpan.FromDays(7);
        if (lower.Contains("5h")) return TimeSpan.FromHours(5);
        if (lower.Contains("day") || lower.Contains("24h") || lower.Contains("1d")) return TimeSpan.FromDays(1);
        return null;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()?.Trim()
            : null;

    private static double? ReadDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var n)) return n;
        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static string SanitizeKey(string raw, int fallbackIndex)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var key = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(key) ? $"pool_{fallbackIndex}" : key;
    }

    private sealed record QuotaPool(
        string Key,
        string DisplayName,
        double RemainingFraction,
        DateTimeOffset? ResetAt,
        TimeSpan? WindowDuration,
        bool IsGemini);
}
