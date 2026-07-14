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
    /// also surfaced as the searchable <c>file</c> attribute (<see cref="Attributes"/>); the hashes are
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
    /// Unified multi-valued **attributes** — key:value facts from agent-supplied keywords
    /// (person:x routed at write time), mechanical content extraction, and reference
    /// extraction (file/ref). Keys lowercase, values verbatim. See Search.FieldExtractor
    /// and Search.KnownFields. Null for pre-attributes entries (graceful degradation).
    /// </summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("attributes")]
    Dictionary<string, string[]>? Attributes = null)
{
    // Back-compat ingest: sidecars written before the Fields→Attributes rename stored the
    // dict under JSON key "fields". Deserialization routes that key here so old sidecars load
    // losslessly. The getter stays observable (so AttributeMigration.MigratesTo can detect a
    // not-yet-rewritten legacy entry); NormalizeAttributesKey() folds it into Attributes AND
    // nulls it, so a normalized entry re-serializes WITHOUT a "fields" key (WhenWritingNull).
    // Freshly-constructed entries never set it → never emit "fields". Remove once all
    // workspaces have migrated (Merge 3 cleanup candidate).
    //
    // FOOTGUN: as a private instance field, _legacyFields participates in the record's synthesized
    // Equals/GetHashCode, so an un-normalized legacy entry is NOT record-equal to its normalized
    // form. This is inert in practice — ArtifactEntry equality is already non-value-semantic (its
    // Dictionary/array members compare by reference), so nothing uses it — but the invariant that
    // defuses it entirely is: EVERY load seam calls NormalizeAttributesKey() before an entry
    // escapes, so no LegacyFields-bearing entry is ever compared. Wired in the load-seam task.
    private readonly Dictionary<string, string[]>? _legacyFields;

    // [JsonInclude] kept intentionally: forces source-gen to emit the ingest metadata even under
    // PublishTrimmed (the trimmed `scri serve` publish has no reflection fallback — cf. the
    // Dictionary<string,string[]> tool-param crash). Defensive, not redundant.
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonPropertyName("fields")]
    public Dictionary<string, string[]>? LegacyFields
    {
        get => _legacyFields;
        init => _legacyFields = value;
    }

    /// <summary>Coalesces a legacy "fields"-key value into <see cref="Attributes"/> (new key
    /// wins) and clears the legacy holder so re-serialization emits only "attributes". Call
    /// once immediately after deserializing a persisted entry. No-op when no legacy value.</summary>
    public ArtifactEntry NormalizeAttributesKey()
        => _legacyFields is not null
            ? this with { Attributes = Attributes ?? _legacyFields, LegacyFields = null }
            : this;
}
