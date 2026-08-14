namespace Scrinia.Core.Models;

/// <summary>
/// Exactly the metadata a copy carries: what a store can neither derive from the artifact nor
/// compute from the destination.
///
/// <para><c>Name</c>, <c>Uri</c> and <c>Scope</c> belong to the destination; <c>ChunkCount</c> is
/// read off the artifact; <c>CreatedAt</c> is stamped when the copy is made. None of them appear
/// here, so none of them can disagree with the artifact they travel with — the rule
/// <c>Scrinia.Server.Models.ShowResponse</c> states for the response side.</para>
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
