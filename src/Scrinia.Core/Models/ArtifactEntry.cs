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
    ChunkEntry[]? ChunkEntries = null);
