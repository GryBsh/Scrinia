namespace Scrinia.Core.Models;

public sealed record EphemeralEntry(
    string Name,
    string Artifact,
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
    ChunkEntry[]? ChunkEntries = null);
