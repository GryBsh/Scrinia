namespace Scrinia.Core.Search;

/// <summary>
/// The retrieval scoring rubric, in one place.
///
/// <para>Finding M6: these constants were stated four times — a literal ladder inside each of
/// <c>ScoreEntryTerm</c>, <c>ScoreTopicTerm</c> and <c>ScoreChunkEntryTerm</c>, plus an ASCII
/// table in the scorer's header that had to be hand-synced with all three. Changing what a
/// keyword match is worth meant four edits, and the header table — the one people read — was
/// the copy most likely to drift into being confidently wrong.</para>
///
/// <para>All matching is case-insensitive. Every value here is the magnitude the scorer used
/// before consolidation; this record was introduced to make the rubric editable and honest
/// about where a number lives, not to change retrieval behaviour.</para>
/// </summary>
public sealed record ScoringWeights(
    // ── Entry scoring, per query term ──
    double NameExact = 100.0,
    double NameStartsWith = 30.0,
    double NameContains = 20.0,
    double KeywordExact = 40.0,
    double KeywordContains = 12.0,
    double AttributePairExact = 50.0,
    double AttributeValueExact = 40.0,
    double AttributeValueContains = 12.0,
    double ContentPreviewContains = 5.0,

    // ── Topic scoring, per query term ──
    double TopicNameExact = 100.0,
    double TopicNameStartsWith = 30.0,
    double TopicNameContains = 20.0,
    double TopicTagExact = 50.0,
    double TopicTagContains = 15.0,
    double TopicDescriptionContains = 10.0,
    double TopicEntryNameAggregate = 8.0,

    // ── Chunk-entry scoring, per query term ──
    double ChunkKeywordExact = 40.0,
    double ChunkKeywordContains = 12.0,
    double ChunkContentPreviewContains = 5.0,

    // Chunk scoring also consults the PARENT entry, at reduced weight — a chunk of a
    // well-named memory is more relevant than the same text in an unrelated one.
    double ChunkParentNameExact = 50.0,          // WeightedAttributeScorer.cs:510
    double ChunkParentNameContains = 10.0,       // :512
    double ChunkParentKeywordExact = 20.0,       // :521
    double ChunkParentKeywordContains = 6.0,     // :526
    double ChunkParentTagExact = 20.0,           // :537
    double ChunkParentTagContains = 6.0,         // :542

    // ── Composition ──
    /// <summary>Bonus per additional matched query term, applied as (matched - 1) x this.</summary>
    double IntersectionBonus = 15.0,
    /// <summary>Multiplier on normalized BM25 before it joins the attribute score.</summary>
    double Bm25Weight = 5.0)
{
    public static ScoringWeights Default { get; } = new();
}
