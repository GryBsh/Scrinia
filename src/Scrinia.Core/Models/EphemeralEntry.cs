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
    /// <summary>
    /// Unified multi-valued **attributes** — key:value facts from agent-supplied keywords
    /// (person:x routed at write time), mechanical content extraction, and reference
    /// extraction (file/ref). Keys lowercase, values verbatim. See Search.FieldExtractor
    /// and Search.KnownFields. Null for pre-attributes entries (graceful degradation).
    /// </summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("attributes")]
    Dictionary<string, string[]>? Attributes = null);
