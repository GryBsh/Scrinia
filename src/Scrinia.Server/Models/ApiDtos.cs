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
    Dictionary<string, string>? FileHashes)
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
            entry.FileHashes);
    }
}

public sealed record ChunkEntryDto(
    int ChunkIndex, string? ContentPreview, string[]? Keywords,
    Dictionary<string, int>? TermFrequencies);

/// <summary><c>ChunkCount</c> is deleted, not retained: it is <c>Content.Length</c>, and a
/// derivable field that can disagree with its payload is the defect this shape fixes.</summary>
public sealed record ShowResponse(string Name, string[] Content, long OriginalBytes);

/// <summary>Body of POST /search. Exists because attributeFilter is a dictionary of arrays and
/// does not belong in a query string. Mirrors the GET parameters exactly otherwise.</summary>
public sealed record SearchRequest(
    string Q,
    string? Scopes = null,
    int Limit = 20,
    string? ExcludeTopics = null,
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
