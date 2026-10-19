using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

// ── Result types ────────────────────────────────────────────────────────────

public abstract record SearchResult(double Score)
{
    /// <summary>Bounded previews of matching sibling chunks, when a single preview
    /// could hide updates. Null preserves the ordinary entry or chunk preview.</summary>
    public string? ContextPreview { get; init; }
}

public sealed record EntryResult(
    ScopedArtifact Item,
    double Score) : SearchResult(Score);

public sealed record TopicResult(
    string Scope,
    string TopicName,
    string Description,
    int EntryCount,
    string[]? Tags,
    double Score) : SearchResult(Score);

public sealed record ChunkEntryResult(
    ScopedArtifact ParentItem,
    ChunkEntry Chunk,
    int TotalChunks,
    double Score) : SearchResult(Score);

// Topic metadata for search scoring
public sealed record TopicInfo(
    string Scope,
    string TopicName,
    int EntryCount,
    string Description,
    string[]? Tags,
    string[] EntryNames);
