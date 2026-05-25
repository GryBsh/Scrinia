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
    /// <summary>
    /// Agent-supplied phrases, questions, or category-level paraphrases that bridge
    /// likely user queries to this entry's content. Indexed via field-score + TF seeding.
    /// </summary>
    string[]? RetrievalAnchors = null);
