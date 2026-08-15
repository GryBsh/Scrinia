namespace Scrinia.Core.Models;

public sealed record EphemeralEntry(
    string Name,
    string Artifact,
    long OriginalBytes,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    string? ContentPreview = null,
    // v3 fields:
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null,
    DateTimeOffset? UpdatedAt = null,
    ChunkEntry[]? ChunkEntries = null,
    /// <inheritdoc cref="ArtifactEntry.FileHashes"/>
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
    /// <inheritdoc cref="ArtifactEntry.AgentKeywords"/>
    [property: System.Text.Json.Serialization.JsonPropertyName("agentKeywords")]
    string[]? AgentKeywords = null)
{
    /// <summary>
    /// This memory in the <see cref="ArtifactEntry"/> shape every metadata reader, renderer and
    /// derivation rule takes. Carries <b>every</b> field the two records share.
    ///
    /// <para><b>Why this is a method and not a copy at each call site.</b> The projection was
    /// hand-written at every site that needed it, and the two records are two views of ONE memory:
    /// a field present in one and null in the other means the answer depends on which read you came
    /// through. Both <c>Keywords</c> and <c>AgentKeywords</c> have already been that bug — a null
    /// <c>AgentKeywords</c> reads as UNKNOWN, not "none", so dropping it here sends a caller to the
    /// provenance recovery heuristic with the real answer sitting in the record it just read. One
    /// projection means adding a field to <see cref="EphemeralEntry"/> cannot silently miss a site.
    /// </para>
    ///
    /// <para><see cref="ArtifactEntry.Uri"/> is empty because an ephemeral memory has no backing
    /// file. <see cref="ArtifactEntry.Name"/> is BARE — the <c>~</c> belongs to a caller-facing
    /// address, and is added by whoever builds one, never carried on the record.</para>
    /// </summary>
    public ArtifactEntry ToArtifactEntry() => new(
        Name: Name,
        Uri: "",
        OriginalBytes: OriginalBytes,
        ChunkCount: ChunkCount,
        CreatedAt: CreatedAt,
        ContentPreview: ContentPreview,
        Keywords: Keywords,
        TermFrequencies: TermFrequencies,
        UpdatedAt: UpdatedAt,
        ChunkEntries: ChunkEntries,
        FileHashes: FileHashes,
        Attributes: Attributes,
        AgentKeywords: AgentKeywords);

    /// <summary>
    /// This memory with every shared field taken from <paramref name="entry"/>.
    ///
    /// <para><b>One-sided, not an inverse.</b> <c>e.WithMetadataFrom(e.ToArtifactEntry())</c> is
    /// <c>e</c> — that direction holds, because <see cref="ToArtifactEntry"/> drops no shared field.
    /// The other direction does NOT: <c>a</c> is not
    /// <c>e.WithMetadataFrom(a).ToArtifactEntry()</c> whenever <c>a.Uri</c> is non-empty, since
    /// <see cref="EphemeralEntry"/> has no <c>Uri</c> to hold it and the projection back out writes
    /// the empty placeholder. Harmless as used — every caller applies this to an entry derived from
    /// an ephemeral, which has no backing file and so no address — but "the inverse" claimed a
    /// symmetry the pair does not have.</para>
    ///
    /// <para><b>It writes OVER this record rather than building a new one, and it has to.</b>
    /// <see cref="ArtifactEntry"/> has no <see cref="Artifact"/> — the memory's actual content lives
    /// only here, and an ephemeral read is served straight out of it. A projection round trip that
    /// reconstructed the record would report a successful metadata update and leave the memory
    /// empty.</para>
    /// </summary>
    public EphemeralEntry WithMetadataFrom(ArtifactEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return this with
        {
            Name = entry.Name,
            OriginalBytes = entry.OriginalBytes,
            ChunkCount = entry.ChunkCount,
            CreatedAt = entry.CreatedAt,
            ContentPreview = entry.ContentPreview,
            Keywords = entry.Keywords,
            TermFrequencies = entry.TermFrequencies,
            UpdatedAt = entry.UpdatedAt,
            ChunkEntries = entry.ChunkEntries,
            FileHashes = entry.FileHashes,
            Attributes = entry.Attributes,
            AgentKeywords = entry.AgentKeywords,
        };
    }
}
