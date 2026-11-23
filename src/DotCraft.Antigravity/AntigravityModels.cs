using System.Text.RegularExpressions;
using DotCraft.Agents;
using DotCraft.Configuration;

namespace DotCraft.Antigravity;

internal sealed partial class AntigravityModels
{
    public const int SafeDefaultContextWindow = 239_000;
    public const int ClaudeContextWindow = 200_000;
    public const int GptOssContextWindow = 128_000;

    private static readonly (string Suffix, ProviderReasoningEffort Effort, string Token)[] EffortSuffixes =
    [
        ("-extra-high", ProviderReasoningEffort.ExtraHigh, "xhigh"),
        ("-xhigh", ProviderReasoningEffort.ExtraHigh, "xhigh"),
        ("-extra-low", ProviderReasoningEffort.Low, "low"),
        ("-max", ProviderReasoningEffort.Max, "max"),
        ("-high", ProviderReasoningEffort.High, "high"),
        ("-medium", ProviderReasoningEffort.Medium, "medium"),
        ("-low", ProviderReasoningEffort.Low, "low")
    ];

    private static readonly string[] SkipPrefixes = ["error:", "usage:", "available models:", "warning:", "--", "fetching"];
    private static readonly char[] SpinnerChars = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    private readonly object _gate = new();
    private CatalogSnapshot _snapshot = BuildFallbackSnapshot();

    public CatalogSnapshot Current
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

    public void Reset()
    {
        lock (_gate) _snapshot = BuildFallbackSnapshot();
    }

    public CatalogSnapshot UpdateFromCliOutput(string stdout, DateTimeOffset observedAt)
    {
        var parsed = Parse(stdout, observedAt);
        if (parsed.Entries.Count == 0) return Current;
        lock (_gate)
        {
            _snapshot = parsed;
            return parsed;
        }
    }

    public ModelCatalogEntry? FindModel(string modelId)
    {
        var snapshot = Current;
        var clean = modelId.Trim();
        if (snapshot.ById.TryGetValue(clean, out var exact)) return exact;
        var (baseId, _, _) = SplitEffortSuffix(clean);
        if (snapshot.ById.TryGetValue(baseId, out var grouped)) return grouped;
        return BuildSyntheticEntry(baseId, null, []);
    }

    public (string ModelSlug, string? EffortToken) ResolveLaunchModel(string requestedModel, ProviderReasoningEffort? requestedEffort)
    {
        var clean = requestedModel.Trim();
        var snapshot = Current;

        if (snapshot.ExactSlugs.TryGetValue(clean, out var directEntry))
        {
            if (directEntry.Effort is { } directEffort)
                return (clean, ToCliEffortToken(directEffort));
            return (clean, null);
        }

        var (baseId, suffixEffort, _) = SplitEffortSuffix(clean);
        if (snapshot.VariantsByBase.TryGetValue(baseId, out var variants))
        {
            if (variants.Count == 0)
                return (baseId, null);

            var target = NormalizeRequestedEffort(requestedEffort ?? suffixEffort, variants);
            return (variants[target], ToCliEffortToken(target));
        }

        // Unknown custom model: pass through verbatim; only forward --effort when a suffix or explicit effort exists.
        if (suffixEffort is { } fallbackSuffix)
            return (clean, ToCliEffortToken(fallbackSuffix));

        if (requestedEffort is { } explicitEffort and not ProviderReasoningEffort.None)
            return (clean, ToCliEffortToken(explicitEffort));

        return (clean, null);
    }

    public static CatalogSnapshot Parse(string text, DateTimeOffset observedAt)
    {
        var order = new List<string>();
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variantsByBase = new Dictionary<string, Dictionary<ProviderReasoningEffort, string>>(StringComparer.OrdinalIgnoreCase);
        var exactSlugs = new Dictionary<string, (string BaseId, ProviderReasoningEffort? Effort)>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split('\n'))
        {
            var line = AnsiRegex().Replace(raw, string.Empty).Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (SpinnerChars.Contains(line[0])) continue;
            if (SkipPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;

            var match = ModelLineRegex().Match(line);
            if (!match.Success) continue;

            var slug = match.Groups[1].Value;
            var label = match.Groups[2].Value.Trim();
            var (baseId, effort, _) = SplitEffortSuffix(slug);

            if (!variantsByBase.TryGetValue(baseId, out var variants))
            {
                variants = [];
                variantsByBase[baseId] = variants;
                order.Add(baseId);
            }

            exactSlugs[slug] = (baseId, effort);
            if (effort is { } eff)
                variants.TryAdd(eff, slug);

            if (!string.IsNullOrWhiteSpace(label) && !displayNames.ContainsKey(baseId))
                displayNames[baseId] = StripEffortSuffixFromDisplayName(label);
        }

        var entries = new List<ModelCatalogEntry>(order.Count);
        var byId = new Dictionary<string, ModelCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseId in order)
        {
            var variants = variantsByBase[baseId];
            var sortedEfforts = variants.Keys.OrderBy(EffortRank).ToArray();
            displayNames.TryGetValue(baseId, out var display);
            var entry = BuildSyntheticEntry(baseId, display, sortedEfforts);
            entries.Add(entry);
            byId[baseId] = entry;
        }

        return new CatalogSnapshot(entries, byId, variantsByBase, exactSlugs, "cli", observedAt);
    }

    public static (string BaseId, ProviderReasoningEffort? Effort, string? Token) SplitEffortSuffix(string slug)
    {
        foreach (var (suffix, effort, token) in EffortSuffixes)
        {
            if (slug.Length > suffix.Length && slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (slug[..^suffix.Length], effort, token);
        }
        return (slug, null, null);
    }

    private static ProviderReasoningEffort NormalizeRequestedEffort(
        ProviderReasoningEffort? requested,
        Dictionary<ProviderReasoningEffort, string> supported)
    {
        var available = supported.Keys.OrderBy(EffortRank).ToArray();
        if (requested is null)
        {
            if (supported.ContainsKey(ProviderReasoningEffort.High)) return ProviderReasoningEffort.High;
            if (supported.ContainsKey(ProviderReasoningEffort.Medium)) return ProviderReasoningEffort.Medium;
            return available[^1];
        }

        if (requested == ProviderReasoningEffort.None)
            return available[0];

        if (supported.ContainsKey(requested.Value))
            return requested.Value;

        var targetRank = EffortRank(requested.Value);
        return available
            .OrderBy(e => Math.Abs(EffortRank(e) - targetRank))
            .ThenByDescending(EffortRank)
            .First();
    }

    private static int EffortRank(ProviderReasoningEffort effort) => effort switch
    {
        ProviderReasoningEffort.None => 0,
        ProviderReasoningEffort.Low => 1,
        ProviderReasoningEffort.Medium => 2,
        ProviderReasoningEffort.High => 3,
        ProviderReasoningEffort.ExtraHigh => 4,
        ProviderReasoningEffort.Max => 5,
        _ => 2
    };

    private static string ToCliEffortToken(ProviderReasoningEffort effort) => effort switch
    {
        ProviderReasoningEffort.Low => "low",
        ProviderReasoningEffort.Medium => "medium",
        ProviderReasoningEffort.High => "high",
        ProviderReasoningEffort.ExtraHigh => "xhigh",
        ProviderReasoningEffort.Max => "max",
        _ => "low"
    };

    private static ModelCatalogEntry BuildSyntheticEntry(string baseId, string? displayName, ProviderReasoningEffort[] efforts)
    {
        var lower = baseId.ToLowerInvariant();
        var isClaude = lower.StartsWith("claude-", StringComparison.Ordinal);
        var isGptOss = lower.StartsWith("gpt-oss", StringComparison.Ordinal);
        var contextWindow = isGptOss ? GptOssContextWindow : isClaude ? ClaudeContextWindow : SafeDefaultContextWindow;
        var ownedBy = isClaude ? "Anthropic" : isGptOss ? "OpenAI" : "Google";
        string[] modalities = isGptOss ? new[] { "text" } : new[] { "text", "image" };

        ModelReasoningSupport? reasoning = efforts.Length > 0
            ? new(
                SupportsDisable: false,
                SupportedEfforts: efforts,
                DefaultEffort: efforts.Contains(ProviderReasoningEffort.High)
                    ? ProviderReasoningEffort.High
                    : efforts.Contains(ProviderReasoningEffort.Medium)
                        ? ProviderReasoningEffort.Medium
                        : efforts[^1],
                DefaultEnabled: true)
            : null;

        return new ModelCatalogEntry
        {
            Id = baseId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? baseId : displayName,
            OwnedBy = ownedBy,
            ContextWindow = contextWindow,
            MaxOutputTokens = 65_536,
            InputModalities = modalities,
            Reasoning = reasoning
        };
    }

    private static string StripEffortSuffixFromDisplayName(string label) =>
        DisplayEffortSuffixRegex().Replace(label, string.Empty).Trim();

    private static CatalogSnapshot BuildFallbackSnapshot()
    {
        const string fallback = """
            gemini-4-neon-xhigh          Gemini 4 Neon (XHigh)
            gemini-4-neon-high           Gemini 4 Neon (High)
            gemini-4-neon-medium         Gemini 4 Neon (Medium)
            gemini-4-neon-low            Gemini 4 Neon (Low)
            gemini-3.8-flash-high        Gemini 3.8 Flash (High)
            gemini-3.8-flash-medium      Gemini 3.8 Flash (Medium)
            gemini-3.8-flash-low         Gemini 3.8 Flash (Low)
            gemini-3.1-pro-high          Gemini 3.1 Pro (High)
            gemini-3.1-pro-low           Gemini 3.1 Pro (Low)
            claude-sonnet-4-6            Claude Sonnet 4.6
            claude-opus-4-6-thinking     Claude Opus 4.6 (Thinking)
            gpt-oss-120b-medium          GPT-OSS 120B (Medium)
            """;
        var parsed = Parse(fallback, DateTimeOffset.MinValue);
        return parsed with { Source = "fallback" };
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9][a-zA-Z0-9_\-\.]+)(?:\s+(.*))?$")]
    private static partial Regex ModelLineRegex();

    [GeneratedRegex(@"\s*\((?:Extra\s*High|XHigh|High|Medium|Low|Extra\s*Low|Max)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayEffortSuffixRegex();

    internal sealed record CatalogSnapshot(
        IReadOnlyList<ModelCatalogEntry> Entries,
        IReadOnlyDictionary<string, ModelCatalogEntry> ById,
        IReadOnlyDictionary<string, Dictionary<ProviderReasoningEffort, string>> VariantsByBase,
        IReadOnlyDictionary<string, (string BaseId, ProviderReasoningEffort? Effort)> ExactSlugs,
        string Source,
        DateTimeOffset RefreshedAt);
}
