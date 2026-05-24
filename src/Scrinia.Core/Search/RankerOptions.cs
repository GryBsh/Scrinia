using System.Globalization;

namespace Scrinia.Core.Search;

/// <summary>
/// How <see cref="WeightedFieldScorer.SearchAll(string, IEnumerable{ScopedArtifact}, IEnumerable{TopicInfo}, int, IReadOnlyDictionary{string, double}?)"/>
/// combines the three relevance signals — field-match score, normalized BM25, and
/// supplemental embedding cosine — into a single value passed to the recency/importance
/// composition.
/// <list type="bullet">
/// <item><see cref="Sum"/>: <c>fieldScore + normalizedBm25·5 + supplemental</c>. Historical
/// default. Magnitudes are uneven (BM25 contributes up to ~500, embeddings up to ~50 in
/// the bench harness), so the lexical signal dominates.</item>
/// <item><see cref="Rrf"/>: Reciprocal Rank Fusion with <c>k=60</c>. Each source is ranked
/// independently across all candidates; the per-source <c>1/(60+rank)</c> contributions
/// are summed. Scale-invariant, so embeddings get an equal vote even when their raw
/// cosine values are tiny relative to BM25 magnitudes.</item>
/// </list>
/// <para><b>Composition note:</b> RRF scores are ~10⁻²–10⁻¹ in magnitude. When
/// <see cref="RankerOptions.AlphaRecency"/> or <see cref="RankerOptions.AlphaImportance"/>
/// is non-zero, their scaled contributions (up to <see cref="RankerOptions.RecencyScale"/>
/// and <see cref="RankerOptions.ImportanceScale"/>) completely dominate RRF. Pair RRF with
/// alpha_recency=alpha_importance=0 for a pure-relevance run; the bench harness already
/// does this by default.</para>
/// </summary>
public enum FusionMode
{
    /// <summary>Historical sum-with-weights. Default.</summary>
    Sum = 0,

    /// <summary>Reciprocal Rank Fusion across field-match, BM25, and supplemental sources.</summary>
    Rrf = 1,
}

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
///
/// <para><see cref="Fusion"/> selects how the three retrieval signals combine inside the
/// "relevance" term. Default <see cref="FusionMode.Sum"/> matches the historical behavior;
/// <see cref="FusionMode.Rrf"/> rank-fuses each source for scale-invariant combination.</para>
/// </summary>
public sealed record RankerOptions(
    double AlphaRelevance = 1.0,
    double AlphaRecency = 1.0,
    double AlphaImportance = 1.0,
    double TauDays = 14.0,
    int NeutralImportance = 5,
    double RecencyScale = 50.0,
    double ImportanceScale = 50.0,
    FusionMode Fusion = FusionMode.Sum)
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
