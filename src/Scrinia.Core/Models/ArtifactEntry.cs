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
    /// <summary>
    /// The <c>file</c> attribute's private drift baseline: dependency file path → content SHA hash,
    /// captured at write time. Drift detection re-hashes each path and compares. The path KEYS are
    /// also surfaced as the searchable <c>file</c> attribute (<see cref="Fields"/>); the hashes are
    /// private behavior state, never displayed or scored. Persisted under the legacy JSON key
    /// <c>"codeRefs"</c> (kept stable — no migration; renamed from the old <c>CodeRefs</c> member).
    /// </summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("codeRefs")]
    Dictionary<string, string>? FileHashes = null,
    /// <summary>
    /// Agent-supplied phrases, questions, or category-level paraphrases that bridge
    /// likely user queries to this entry's content. Indexed via field-score + TF seeding.
    /// </summary>
    string[]? RetrievalAnchors = null,
    /// <summary>
    /// Unified multi-valued fields — key:value facts from agent-supplied keywords
    /// (person:x routed at write time), mechanical content extraction, and reference
    /// extraction (file/ref). Keys lowercase, values verbatim. See Search.FieldExtractor
    /// and Search.KnownFields. Null for pre-fields entries (graceful degradation).
    /// </summary>
    Dictionary<string, string[]>? Fields = null);
