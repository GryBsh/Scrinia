using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Server.Models;

// ── Memory CRUD ──────────────────────────────────────────────────────────────

/// <summary>Body of POST /memories. <c>Attributes</c> is the agent-supplied attribute map — the
/// current model. It replaces the legacy <c>tags</c>/<c>reviewAfter</c>/<c>reviewWhen</c> carriers,
/// which were only ever translated server-side into the <c>tag</c> and <c>review</c> attribute keys;
/// callers write those keys directly now. <c>Description</c> stays — it is a curated ContentPreview,
/// a different concept from the carriers.</summary>
public sealed record StoreRequest(
    string[] Content,
    string Name,
    string? Description = null,
    Dictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null);

/// <summary>Response to POST /memories: the complete entry the STORE derived, plus the
/// human-facing message.
/// <para><c>Name</c>, <c>QualifiedName</c>, <c>ChunkCount</c> and <c>OriginalBytes</c> are deleted,
/// not retained: <see cref="MemoryEntry"/> already carries all four, and a derivable field that can
/// disagree with its payload is the defect this branch removes (same reasoning that deleted
/// <c>ShowResponse.ChunkCount</c>). A caller that needs the counts reads <c>Entry</c>.</para></summary>
public sealed record StoreResponse(MemoryEntry Entry, string Message);

/// <summary>Body of POST /memories/{name}/append. Mirrors <see cref="Core.Models.MemoryWrite"/>'s
/// append shape — it used to carry <c>Content</c> alone, so an append made over the wire silently
/// dropped the description, attributes and keywords a local append carries (D-18).
/// <para><c>Content</c> is a single <c>string</c>, not a <c>string[]</c> like
/// <see cref="StoreRequest.Content"/>: <see cref="IMemoryStore.AppendAsync"/> refuses any count but
/// one (the derivation pipeline can express exactly one appended chunk), and a scalar makes that
/// constraint unrepresentable-otherwise rather than a validation rule the endpoint has to enforce
/// and a client has to remember. There is no "wrong number of chunks" request to reject.</para></summary>
public sealed record AppendRequest(
    string Content,
    string? Description = null,
    Dictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null);

/// <inheritdoc cref="StoreResponse"/>
public sealed record AppendResponse(MemoryEntry Entry, string Message);

public sealed record ListResponse(MemoryEntry[] Memories, int Total);

/// <summary>The one complete entry shape — a null field means genuinely absent, never "not
/// requested". Produced by the store-owned derivation pipeline and carried whole over the
/// wire.</summary>
public sealed record MemoryEntry(
    string Name, string QualifiedName, string Scope,
    int ChunkCount, long OriginalBytes,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    string Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntryDto[]? ChunkEntries,
    Dictionary<string, string>? FileHashes,
    /// <summary>Which of <see cref="Keywords"/> the caller supplied. Additive and OPTIONAL on this
    /// wire, which is why it did not need an API version bump: an older client ignores the key, and
    /// a newer client reading an older server sees it absent — which is null, which already means
    /// "unknown" (<see cref="ArtifactEntry.AgentKeywords"/>) rather than "none".
    /// <para>It travels because the alternative is a store-mode divergence: a compaction reads the
    /// provenance to decide which keywords to hand back, so a remote client that could not see it
    /// would fall back to the heuristic for every memory — including the ones the server knows the
    /// real answer for.</para></summary>
    string[]? AgentKeywords = null)
{
    /// <summary>The one place a store-derived <see cref="ArtifactEntry"/> becomes this wire shape.
    /// Every endpoint that emits an entry — list, get-one, patch — and every write response goes
    /// through here, so none of them can grow a private copy that projects a different subset.
    /// <para><paramref name="scope"/> is the store's internal scope (<c>"local"</c>,
    /// <c>"local:topic"</c>, <c>"ephemeral"</c>), not a display label. An ephemeral entry's whole
    /// address is its <c>~name</c>, which is why it does not go through
    /// <see cref="IMemoryStore.FormatQualifiedName"/>.</para></summary>
    public static MemoryEntry From(IMemoryStore store, ArtifactEntry entry, string scope)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entry);

        bool ephemeral = string.Equals(scope, "ephemeral", StringComparison.Ordinal);
        return new MemoryEntry(
            entry.Name,
            ephemeral ? $"~{entry.Name}" : store.FormatQualifiedName(scope, entry.Name),
            MemoryNaming.FormatScopeLabel(scope),
            entry.ChunkCount,
            entry.OriginalBytes,
            entry.CreatedAt,
            entry.UpdatedAt,
            AttributeReaders.PreviewText(entry.ContentPreview),
            entry.Attributes,
            entry.Keywords,
            // Populated whenever the store had them in hand (a local, file-backed store always
            // does), never fabricated for a store that doesn't.
            entry.TermFrequencies,
            entry.ChunkEntries?.Select(c => new ChunkEntryDto(
                c.ChunkIndex, c.ContentPreview, c.Keywords, c.TermFrequencies)).ToArray(),
            entry.FileHashes,
            entry.AgentKeywords);
    }
}

public sealed record ChunkEntryDto(
    int ChunkIndex, string? ContentPreview, string[]? Keywords,
    Dictionary<string, int>? TermFrequencies);

/// <summary><c>ChunkCount</c> is deleted, not retained: it is <c>Content.Length</c>, and a
/// derivable field that can disagree with its payload is the defect this shape fixes.</summary>
public sealed record ShowResponse(string Name, string[] Content, long OriginalBytes);

/// <summary>Body of POST /search. Exists because attributeFilter is a dictionary of arrays and
/// does not belong in a query string. Mirrors the GET parameters exactly otherwise.
/// <para>Scopes/ExcludeTopics are arrays (Task 9, D-2) — a scope name containing a comma cannot
/// survive a comma-joined string, so this is a breaking wire change distinct from the MCP
/// facade's own array conversion. The GET route's <c>scopes</c>/<c>excludeTopics</c> query
/// parameters take the same shape via ASP.NET Core's native <c>string[]?</c> binding
/// (<c>?scopes=a&amp;scopes=b</c>), not a single comma-joined value.</para></summary>
public sealed record SearchRequest(
    string Q,
    string[]? Scopes = null,
    int Limit = 20,
    string[]? ExcludeTopics = null,
    string? PathPrefix = null,
    Dictionary<string, string[]>? AttributeFilter = null);

public sealed record SearchResponse(SearchResultItem[] Results);

public sealed record SearchResultItem(
    string Type,
    string Name,
    double Score,
    string? Description,
    int? ChunkIndex = null,
    int? TotalChunks = null,
    // Null for a "topic" result: a topic is a scope label, not a memory, so it has no real byte
    // count or creation time to report. Never fabricated/aggregated and never
    // DateTimeOffset.MinValue as a stand-in for "unknown" — both are the defect this exists to fix,
    // wearing a different hat. Present (non-null) for "entry" and "chunk" results.
    long? OriginalBytes = null,
    DateTimeOffset? CreatedAt = null);

public sealed record ChunkResponse(
    string Content,
    int ChunkIndex,
    int TotalChunks);

public sealed record CopyRequest(
    string Destination,
    bool Overwrite = false);

/// <summary>
/// The metadata a client-side source carries to a destination this server owns: exactly the fields
/// the server can neither derive from the artifact nor compute from the destination name.
///
/// <para><c>Name</c>, <c>QualifiedName</c> and <c>Scope</c> are the destination's; <c>ChunkCount</c>
/// is <c>Content.Length</c>; <c>CreatedAt</c> is stamped on arrival. None of them appear here, so
/// none of them can disagree with the request that carries them — the same rule
/// <see cref="ShowResponse"/> states, applied to a request.</para>
///
/// <para><c>OriginalBytes</c> is deleted for the same reason (RULED 2026-08-14): the encoder
/// computes the true value from <c>Content</c> itself
/// (<see cref="Scrinia.Core.Encoding.Nmp2ChunkedEncoder"/> sums each chunk's UTF-8 length), so it is
/// not metadata the client derived and the server cannot — it is exactly the kind of derivable field
/// this shape otherwise excludes. Trusting a client-supplied number here would let the request
/// disagree with its own artifact. The handler computes it from <c>Content</c>.</para>
/// </summary>
public sealed record CarriedEntry(
    DateTimeOffset? UpdatedAt,
    string? Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntryDto[]? ChunkEntries,
    Dictionary<string, string>? FileHashes,
    string[]? AgentKeywords = null);

/// <summary>
/// Body of <c>POST /memories/{name}/copy-from-client</c>, where <c>{name}</c> is the
/// <b>DESTINATION</b> — the opposite of <c>POST /memories/{name}/copy</c>, where it is the source.
///
/// <para>That asymmetry is deliberate and is the whole reason this route exists: the source lives
/// only on the client, so there is no server-side name to put in the path. The caller supplies the
/// artifact and the metadata it already derived, and the server stores both without deriving
/// anything.</para>
/// </summary>
public sealed record CopyFromClientRequest(
    string[] Content,
    CarriedEntry Entry,
    bool Overwrite = false);

public sealed record ExportRequest(
    string[] Topics,
    string? Filename = null);

public sealed record ErrorResponse(string Error);

// ── Health ───────────────────────────────────────────────────────────────────

public sealed record HealthResponse(string Status, HealthCheck[]? Checks = null);

public sealed record HealthCheck(string Name, string Status, string? Error = null);

// ── Key management ───────────────────────────────────────────────────────────

public sealed record CreateKeyRequest(
    string UserId,
    string[] Stores,
    string[]? Permissions = null,
    string? Label = null);

public sealed record CreateKeyResponse(
    string RawKey,
    string KeyId,
    string UserId,
    string[] Stores,
    string[] Permissions);

public sealed record KeySummaryDto(
    string Id,
    string UserId,
    string[] Stores,
    string[] Permissions,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool Revoked);

// ── Plugins ─────────────────────────────────────────────────────────────────

public sealed record PluginInfo(string Name, string Version, int Order);

// ── Embeddings ───────────────────────────────────────────────────────────────

public sealed record EmbeddingsStatusResponse(
    string Provider, string Hardware, bool Available, int Dimensions,
    double SemanticWeight, int VectorCount);

public sealed record EmbeddingsSettingsResponse(
    string Provider, string Hardware, double SemanticWeight);

public sealed record ReindexResponse(string Message);
