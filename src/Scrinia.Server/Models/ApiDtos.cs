namespace Scrinia.Server.Models;

// ── Memory CRUD ──────────────────────────────────────────────────────────────

public sealed record StoreRequest(
    string[] Content,
    string Name,
    string? Description = null,
    string[]? Tags = null,
    string[]? Keywords = null,
    string? ReviewAfter = null,
    string? ReviewWhen = null);

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
    string[]? Tags);

public sealed record ShowResponse(
    string Name,
    string Content,
    int ChunkCount,
    long OriginalBytes);

public sealed record SearchResponse(SearchResultItem[] Results);

public sealed record SearchResultItem(
    string Type,
    string Name,
    double Score,
    string? Description,
    int? ChunkIndex = null,
    int? TotalChunks = null);

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
