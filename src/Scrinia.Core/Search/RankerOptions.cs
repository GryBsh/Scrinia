using System.Globalization;

namespace Scrinia.Core.Search;

/// <summary>
/// Tunable weights for the additive ranker composition used by
/// <see cref="WeightedFieldScorer.SearchAll(string, IEnumerable{ScopedArtifact}, IEnumerable{TopicInfo}, int, IReadOnlyDictionary{string, double}?)"/>.
/// The score for an entry with relevance &gt; 0 is:
///
/// <code>
/// recency    = exp(-Δt_days / τ_days)                              // 0..1
/// importance = (entry.Importance ?? NeutralImportance) / 10.0      // 0..1
///
/// total = α_relevance  · relevance
///       + α_recency    · recency    · RecencyScale
///       + α_importance · importance · ImportanceScale
/// </code>
///
/// Defaults follow the Generative Agents (Park et al., 2023) score shape: each of the
/// three terms contributes a meaningful but bounded share of total. A perfectly-fresh,
/// perfectly-important memory adds ~100 points — roughly equal to a name-exact field
/// match — so strong content matches still dominate (BM25 alone can reach ~500 after
/// scaling) but near-ties tip toward the more recent and more important memory.
///
/// Setting α_recency = α_importance = 0 reduces the ranker to relevance only — useful
/// in tests that need to isolate field + BM25 signals from temporal/importance bumps.
/// </summary>
public sealed record RankerOptions(
    double AlphaRelevance = 1.0,
    double AlphaRecency = 1.0,
    double AlphaImportance = 1.0,
    double TauDays = 14.0,
    int NeutralImportance = 5,
    double RecencyScale = 50.0,
    double ImportanceScale = 50.0)
{
    public static RankerOptions Default { get; } = new();

    /// <summary>
    /// Builds options from a config reader. Missing or unparseable keys fall back to the
    /// record's compile-time defaults. The reader signature matches
    /// <c>WorkspaceSetup.GetConfigValue</c> so the bootstrap can pass it directly.
    /// </summary>
    public static RankerOptions FromConfig(Func<string, string?> getConfig)
    {
        var d = Default;
        return new RankerOptions(
            AlphaRelevance:    ParseDouble(getConfig("Scrinia:Search:Alpha:Relevance"),    d.AlphaRelevance),
            AlphaRecency:      ParseDouble(getConfig("Scrinia:Search:Alpha:Recency"),      d.AlphaRecency),
            AlphaImportance:   ParseDouble(getConfig("Scrinia:Search:Alpha:Importance"),   d.AlphaImportance),
            TauDays:           ParseDouble(getConfig("Scrinia:Search:TauDays"),            d.TauDays),
            NeutralImportance: ParseInt   (getConfig("Scrinia:Search:NeutralImportance"),  d.NeutralImportance),
            RecencyScale:      ParseDouble(getConfig("Scrinia:Search:Scale:Recency"),      d.RecencyScale),
            ImportanceScale:   ParseDouble(getConfig("Scrinia:Search:Scale:Importance"),   d.ImportanceScale));
    }

    private static double ParseDouble(string? s, double fallback)
        => s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
           ? v : fallback;

    private static int ParseInt(string? s, int fallback)
        => s is not null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
           ? v : fallback;
}
