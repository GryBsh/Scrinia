namespace Scrinia.Core.Models;

public sealed record ArtifactEntry(
    string Name,
    string Uri,
    long OriginalBytes,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    string? ContentPreview = null,
    // v3 fields:
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null,
    DateTimeOffset? UpdatedAt = null,
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
    /// Unified multi-valued **attributes** — key:value facts from agent-supplied keywords
    /// (person:x routed at write time), mechanical content extraction, and reference
    /// extraction (file/ref). Keys lowercase, values verbatim. See Search.AttributeExtractor
    /// and Search.KnownAttributes. Null for pre-attributes entries (graceful degradation).
    /// </summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("attributes")]
    Dictionary<string, string[]>? Attributes = null,
    /// <summary>
    /// Which of <see cref="Keywords"/> the CALLER supplied, as opposed to the ones derivation
    /// extracted from the content. Recorded at write time, because that is the only moment it is
    /// known: <see cref="Keywords"/> is the merge of the two and the merge erases the difference.
    ///
    /// <para><b>Null means UNKNOWN, not "none".</b> An entry written before this field existed
    /// carries null, and a reader must fall back
    /// (<see cref="MemoryDerivation.CuratedKeywords"/>) rather than conclude that nothing was
    /// curated. An empty array means the caller supplied no keywords, which is a different fact.
    /// Any store, append or compact writes the field, so an entry stops being unknown the first
    /// time it is rewritten.</para>
    ///
    /// <para><b>Why it has to be stored.</b> Agent keywords seed BM25 at +5 and auto-extracted
    /// ones at +2 (D-13). Every rewrite of a memory's content re-runs that classification, and
    /// with the provenance gone the rewrite has to guess — either promoting every auto term to
    /// curated weight, or demoting every curated term to auto. Both were tried on this branch;
    /// both are wrong. Subtracting the content's own terms recovers only part of it and cannot be
    /// made exact, because a curated keyword that the content also yields is genuinely
    /// indistinguishable after the merge.</para>
    /// </summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("agentKeywords")]
    string[]? AgentKeywords = null)
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
