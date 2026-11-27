using System.Text.RegularExpressions;
using DotCraft.Agents;

namespace DotCraft.Antigravity;

internal sealed partial class AntigravityLimits(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private LimitBlock? _block;

    public (string? LimitReachedKind, DateTimeOffset? BlockedUntil, string? ValidationUrl) Snapshot()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var current = ActiveLocked(now);
            return (current?.Kind, current?.Until, current?.ValidationUrl);
        }
    }

    public bool IsBlocked => Snapshot().LimitReachedKind is not null;

    public void Clear()
    {
        lock (_gate) _block = null;
    }

    public void ClearOnSuccess()
    {
        lock (_gate) _block = null;
    }

    public void ThrowIfBlocked()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (ActiveLocked(now) is { } current)
                throw current.ToException();
        }
    }

    public Exception ClassifyAndRecordError(string rawMessage, string? stderrTail = null)
    {
        var combined = string.IsNullOrWhiteSpace(stderrTail)
            ? rawMessage
            : string.IsNullOrWhiteSpace(rawMessage)
                ? stderrTail
                : $"{rawMessage}\n{stderrTail}";
        var now = _clock.GetUtcNow();

        var validationUrl = ExtractValidationUrl(combined);
        if (validationUrl is not null || combined.Contains("VALIDATION_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            var hint = validationUrl is not null
                ? $" Open this link in a browser signed in to the same Google account to verify: {validationUrl}"
                : " Verify your Google account in a browser and retry.";
            var block = new LimitBlock(
                "validation_required",
                "Antigravity.ValidationRequired",
                $"Antigravity requires account verification before continuing.{hint}",
                Until: null,
                ValidationUrl: validationUrl,
                FailureKind: ProviderFailureKind.Unauthorized,
                StatusCode: 403);
            lock (_gate) _block = block;
            return block.ToException();
        }

        if (AuthFailureRegex().IsMatch(combined))
        {
            return new ProviderFailureException(
                new ProviderFailure(ProviderFailureKind.Unauthorized, 401, "Antigravity.LoginRequired")
                {
                    Message = AntigravityAccount.LoginHint
                },
                new InvalidOperationException($"Antigravity.LoginRequired: {AntigravityAccount.LoginHint}"));
        }

        if (CreditsExhaustedRegex().IsMatch(combined))
        {
            var until = ParseResetTime(combined, now) ?? now.AddMinutes(5);
            var block = new LimitBlock(
                "credits_exhausted",
                "Antigravity.CreditsExhausted",
                "Your Google One AI credits balance is exhausted.",
                Until: until,
                ValidationUrl: null,
                FailureKind: ProviderFailureKind.UsageLimitExceeded,
                StatusCode: 429);
            lock (_gate) _block = block;
            return block.ToException();
        }

        if (QuotaReachedRegex().IsMatch(combined))
        {
            var until = ParseResetTime(combined, now) ?? now.AddMinutes(5);
            var block = new LimitBlock(
                "quota_reached",
                "Antigravity.QuotaReached",
                "Antigravity quota reached. Wait for reset or enable AI credits in plugin settings.",
                Until: until,
                ValidationUrl: null,
                FailureKind: ProviderFailureKind.UsageLimitExceeded,
                StatusCode: 429);
            lock (_gate) _block = block;
            return block.ToException();
        }

        var detail = (rawMessage.Length > 0 ? rawMessage : stderrTail ?? "The Antigravity CLI did not complete successfully.").Trim();
        if (detail.Length > 500) detail = detail[..500];
        return new InvalidOperationException($"Antigravity.InferenceFailed: {detail}");
    }

    public static string? CheckEarlyQuotaLog(string isolatedGeminiDir, DateTimeOffset turnStartUtc)
    {
        var logDir = Path.Combine(isolatedGeminiDir, "log");
        if (!Directory.Exists(logDir)) return null;

        try
        {
            var latest = Directory.EnumerateFiles(logDir, "acp-*.log")
                .Concat(Directory.EnumerateFiles(logDir, "cli-*.log"))
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null || latest.LastWriteTimeUtc < turnStartUtc.UtcDateTime.AddSeconds(-2))
                return null;

            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - 16_384);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var tail = reader.ReadToEnd();

            foreach (var rawLine in tail.Split('\n').Reverse())
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.Contains("VALIDATION_REQUIRED", StringComparison.OrdinalIgnoreCase))
                    return line;
                if (CreditsExhaustedRegex().IsMatch(line))
                    return line;
                if (line.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                    && (QuotaReachedRegex().IsMatch(line) || line.Contains("code 429", StringComparison.OrdinalIgnoreCase)))
                    return line;
            }
        }
        catch
        {
        }
        return null;
    }

    public static string? ExtractValidationUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = ValidationUrlRegex().Match(text);
        if (!match.Success) return null;
        var candidate = match.Value.Trim().TrimEnd(')', ',', '.', ';');
        if (candidate.Length > 2048
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.Equals(uri.Host, "accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return candidate;
    }

    public static DateTimeOffset? ParseResetTime(string text, DateTimeOffset now)
    {
        var match = ResetInRegex().Match(text);
        if (!match.Success) return null;

        var span = TimeSpan.Zero;
        foreach (Match part in DurationPartRegex().Matches(match.Groups[1].Value))
        {
            if (!int.TryParse(part.Groups[1].Value, out var value)) continue;
            var unit = part.Groups[2].Value.ToLowerInvariant();
            if (unit.StartsWith('d')) span += TimeSpan.FromDays(value);
            else if (unit.StartsWith('h')) span += TimeSpan.FromHours(value);
            else if (unit.StartsWith('m')) span += TimeSpan.FromMinutes(value);
            else if (unit.StartsWith('s')) span += TimeSpan.FromSeconds(value);
        }
        return span > TimeSpan.Zero ? now + span : null;
    }

    private LimitBlock? ActiveLocked(DateTimeOffset now)
    {
        if (_block is null) return null;
        if (_block.Until is { } until && now >= until)
        {
            _block = null;
            return null;
        }
        return _block;
    }

    private sealed record LimitBlock(
        string Kind,
        string Code,
        string Message,
        DateTimeOffset? Until,
        string? ValidationUrl,
        ProviderFailureKind FailureKind,
        int StatusCode)
    {
        public ProviderFailureException ToException() => new(
            new ProviderFailure(FailureKind, StatusCode, Code, ResetsAt: Until) { Message = Message },
            new InvalidOperationException($"{Code}: {Message}"));
    }

    [GeneratedRegex(@"authentication required|not logged into antigravity|not authenticated|sign in required|error getting token source", RegexOptions.IgnoreCase)]
    private static partial Regex AuthFailureRegex();

    [GeneratedRegex(@"ai credits balance is too low|credits balance is too low|INSUFFICIENT_G1_CREDITS|daily or billing cap", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsExhaustedRegex();

    [GeneratedRegex(@"individual quota reached|quota exceeded|exceeded your|limit reached|reached your|resets? in ", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaReachedRegex();

    [GeneratedRegex(@"https://accounts\.google\.com/[^\s""'<>\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex ValidationUrlRegex();

    [GeneratedRegex(@"resets?\s+in\s+([^.\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetInRegex();

    [GeneratedRegex(@"(\d+)\s*(d(?:ays?)?|h(?:ours?|rs?)?|m(?:in(?:ute)?s?)?|s(?:ec(?:ond)?s?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPartRegex();
}
