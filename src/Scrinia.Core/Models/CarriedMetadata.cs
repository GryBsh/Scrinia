namespace Scrinia.Core.Models;

/// <summary>
/// Exactly the metadata a copy carries: what a store can neither derive from the artifact nor
/// compute from the destination.
///
/// <para><c>Name</c>, <c>Uri</c> and <c>Scope</c> belong to the destination; <c>ChunkCount</c> is
/// read off the artifact; <c>CreatedAt</c> is stamped when the copy is made. None of them appear
/// here, so none of them can disagree with the artifact they travel with — the rule
/// <c>Scrinia.Server.Models.ShowResponse</c> states for the response side.</para>
///
/// <para><b><see cref="OriginalBytes"/> is the one deliberate exception, and it is an asymmetry
/// with <c>ChunkCount</c>, not an oversight.</b> Both are derivable from the artifact, but not at
/// the same price: <c>Nmp2ChunkedEncoder.GetChunkCount</c> is a scrape of the header, while the
/// true byte count is the sum of each chunk's decoded UTF-8 length — reaching it means
/// decompressing the whole artifact on a path whose entire job is to move already-derived metadata
/// without re-deriving it. Both of the two callers hand over a value that is true by construction:
/// <c>FileMemoryStore.CopyMemory</c> passes the source entry's own count, and
/// <c>MemoryEndpoints.CopyFromClient</c> computes it from the request's <c>Content</c> before
/// encoding. Note the wire DTO is stricter than this type: <c>Server.Models.CarriedEntry</c> has no
/// <c>OriginalBytes</c> field at all (RULED 2026-08-14), because a REMOTE caller's number could
/// disagree with the artifact it travels with, where an in-process caller's cannot.</para>
/// </summary>
public sealed record CarriedMetadata(
    long OriginalBytes,
    DateTimeOffset? UpdatedAt,
    string? ContentPreview,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntry[]? ChunkEntries,
    Dictionary<string, string>? FileHashes,
    string[]? AgentKeywords = null);
