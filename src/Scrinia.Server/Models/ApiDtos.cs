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

public sealed record StoreResponse(
    string Name,
    string QualifiedName,
    int ChunkCount,
    long OriginalBytes,
    string Message);

public sealed record AppendRequest(string Content);

public sealed record AppendResponse(
    string Name,
    int ChunkCount,
    long OriginalBytes,
    string Message);

public sealed record ListResponse(MemoryListItem[] Memories, int Total);

public sealed record MemoryListItem(
    string Name,
    string QualifiedName,
    string Scope,
    int ChunkCount,
    long OriginalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string Description,
    Dictionary<string, string[]>? Attributes);

public sealed record ShowResponse(
    string Name,
    string Content,
    int ChunkCount,
    long OriginalBytes);

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
