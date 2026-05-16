namespace Scrinia.Core.Models;

public sealed record ArtifactEntry(
    string Name,
    string Uri,
    long OriginalBytes,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    string Description,
    string[]? Tags = null,
    string? ContentPreview = null,
    // v3 fields:
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? ReviewAfter = null,
    string? ReviewWhen = null,
    ChunkEntry[]? ChunkEntries = null,
    Dictionary<string, string>? CodeRefs = null,
    // v4 field: Tier 2 (LLM-extracted) atomic facts, populated by `scri consolidate --tier2`.
    // Stored alongside keywords for retrieval; each fact also enters TermFrequencies via the
    // Tier 2 sidecar write so BM25 naturally picks them up.
    string[]? Facts = null,
    // v5 field: LLM-scored 1-10 importance ("poignancy" in Generative Agents terms).
    // Populated asynchronously after Upsert by ImportanceScoringSink; null means the score
    // has not been computed yet (legacy entries, no Tier 2 LLM configured, or scoring failed).
    // Read by WeightedFieldScorer.SearchAll as a ranker bump alongside recency.
    int? Importance = null);
