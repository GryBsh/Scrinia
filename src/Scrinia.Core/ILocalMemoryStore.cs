using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// A memory store backed by a local filesystem. Extends <see cref="IMemoryStore"/> with the
/// members that have no meaning without one — the members <c>HttpMemoryStore</c> could only
/// ever implement by throwing.
/// <para>The split exists so that "this operation needs local files" is a compile-time fact.
/// Before it, six members of the shared interface threw <see cref="NotSupportedException"/>
/// at runtime in remote mode, and nothing told a caller which six.</para>
/// <para>It extends rather than stands alone because every caller that needs a path also
/// needs ordinary store operations — bundle export reads the index, drift detection formats
/// qualified names — and C# has no intersection types. One parameter, both member sets.</para>
/// </summary>
public interface ILocalMemoryStore : IMemoryStore
{
    // Paths
    string ArtifactPath(string name, string scope = "local");
    string FindArtifactPath(string subject, string normalizedScope);
    string GetStoreDirForScope(string scope);

    /// <summary>
    /// Absolute path to the workspace root — the directory that contains <c>.scrinia/</c>.
    /// The authority for the workspace's filesystem layout: hand-authored file entities
    /// (via <see cref="PathRouter.ToFileEntityDir"/>), bundle export/import, conflict
    /// scanning, and drift detection all resolve their on-disk location from this one
    /// property.
    /// </summary>
    string WorkspaceRoot { get; }

    // Export/Import support
    List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope);
    void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, string> artifactContents, bool overwrite);

    /// <summary>
    /// Writes <paramref name="artifact"/> to <paramref name="destinationName"/> and indexes
    /// <paramref name="carried"/> against it <b>verbatim — nothing is derived</b>.
    ///
    /// <para>This is the copy rule <c>FileMemoryStore.CopyMemory</c> states inline: the artifact bytes
    /// are identical by definition, so the derived metadata is identical, and re-deriving would re-hash
    /// `file` values against THIS workspace and produce an entry that differs from its byte-identical
    /// source. It exists as a store member because a copy can now arrive from a client whose ephemeral
    /// storage this process cannot see, and that caller needs the guarantee an in-process copy gets.</para>
    ///
    /// <para><c>ChunkCount</c> is read off <paramref name="artifact"/> and <c>CreatedAt</c> is stamped
    /// now — a copy is created now. Both are therefore absent from
    /// <see cref="CarriedMetadata"/>.</para>
    /// </summary>
    /// <returns>False with a caller-facing <paramref name="message"/> when the destination exists and
    /// <paramref name="overwrite"/> is false.</returns>
    bool WriteCarriedCopy(
        string destinationName, string artifact, CarriedMetadata carried, bool overwrite,
        out string message);
}
