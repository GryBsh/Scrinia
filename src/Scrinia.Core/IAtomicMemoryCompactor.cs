using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>A store capability that compacts the current content and metadata atomically.</summary>
/// <remarks>Implementations must serialize persistent reads and publication with competing mutations,
/// or retry against a newer snapshot. A separate read followed by StoreAsync does not satisfy this contract.</remarks>
public interface IAtomicMemoryCompactor
{
    /// <summary>Merges all chunks, or retains only the requested number of most recent chunks.</summary>
    Task<CompactMemoryResult> CompactAsync(MemoryAddress address, int keepRecent = 0,
        CancellationToken cancellationToken = default);
}

/// <summary>The committed compaction, or an unchanged snapshot when no compaction was needed.</summary>
/// <param name="Entry">The committed entry; null when no mutation was needed.</param>
/// <param name="Content">The committed chunks, for post-commit notifications.</param>
/// <param name="OriginalChunkCount">The chunk count of the snapshot actually compacted.</param>
/// <param name="Archived">Whether the prior indexed version was archived.</param>
public sealed record CompactMemoryResult(
    ArtifactEntry? Entry, IReadOnlyList<string> Content, int OriginalChunkCount, bool Archived)
{
    internal WholeMemoryResult? Transaction { get; init; }
}
