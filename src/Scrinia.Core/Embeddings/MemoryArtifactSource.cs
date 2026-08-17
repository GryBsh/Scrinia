using Scrinia.Core.Encoding;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The single rule for "given a (scope, subject), get me this memory's stored content array" —
/// used by every embed-and-index caller that has to re-read a memory rather than receive its
/// content in flight (append re-embeds, reindex loops).
///
/// <para><b>Why this exists (finding M-2).</b> Ephemeral memories are not backed by an on-disk
/// artifact keyed by (scope, subject), so <see cref="IMemoryStore.ReadArtifactAsync"/> throws
/// <see cref="FileNotFoundException"/> for them; they resolve only through the <c>~name</c>
/// qualified-name path. The two reindex loops each carried that conditional inline while all
/// three append re-embed paths called <c>ReadArtifactAsync</c> unconditionally and swallowed the
/// resulting exception into a warning — so appending to an ephemeral memory silently left its
/// vectors stale. Five call sites, one rule, expressed once.</para>
/// </summary>
public static class MemoryArtifactSource
{
    /// <summary>The scope name whose memories live outside the (scope, subject) artifact layout.</summary>
    public const string EphemeralScope = "ephemeral";

    public static bool IsEphemeral(string scope) =>
        scope.Equals(EphemeralScope, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the raw stored artifact for <paramref name="subject"/> in <paramref name="scope"/>,
    /// taking the ephemeral path when the scope calls for it.
    ///
    /// <para>Resolves by (scope, subject) rather than round-tripping through
    /// <c>FormatQualifiedName</c> → <c>ResolveArtifactAsync</c> → <c>ParseQualifiedName</c>: for a
    /// v1 namespaced topic scope (e.g. <c>local:memory/api</c>) <c>FormatQualifiedName</c>
    /// intentionally strips the <c>memory/</c> prefix for display, so re-parsing that string
    /// reconstructs the wrong scope and the artifact can never be found.</para>
    /// </summary>
    public static Task<string> ReadArtifactAsync(
        IMemoryStore store, string scope, string subject, CancellationToken ct) =>
        IsEphemeral(scope)
            ? store.ResolveArtifactAsync($"~{subject}", ct)
            : store.ReadArtifactAsync(subject, scope, ct);

    /// <summary>
    /// Reads a memory and decodes it back into the content array a write would have carried —
    /// the layout <see cref="MemoryVectorIndexer"/> must reproduce, not a single decoded blob.
    /// Returns an empty array when the memory holds nothing embeddable, so callers can treat
    /// "no content" as a skip without repeating the blank/empty checks.
    /// </summary>
    public static async Task<string[]> ReadContentElementsAsync(
        IMemoryStore store, string scope, string subject, CancellationToken ct)
    {
        string artifact = await ReadArtifactAsync(store, scope, subject, ct);
        if (string.IsNullOrWhiteSpace(artifact)) return [];

        var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
        return elements.Any(e => !string.IsNullOrWhiteSpace(e)) ? elements : [];
    }
}
