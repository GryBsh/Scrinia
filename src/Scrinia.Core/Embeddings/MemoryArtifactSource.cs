using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The shared rule for "given a memory, get me its stored content array" — used by every
/// embed-and-index caller that has to re-read a memory rather than receive its content in flight
/// (append re-embeds, reindex loops).
///
/// <para><b>Why this exists (finding M-2).</b> Ephemeral memories are not backed by an on-disk
/// artifact keyed by (scope, subject), so <see cref="IPluginStore.ReadArtifactAsync"/> throws
/// <see cref="FileNotFoundException"/> for them; they resolve only through the <c>~name</c>
/// qualified-name path. All three append re-embed paths called <c>ReadArtifactAsync</c>
/// unconditionally and swallowed the resulting exception into a warning — so appending to an
/// ephemeral memory silently left its vectors stale.</para>
///
/// <para><b>Ephemerality is a property of the NAME, not of the parsed scope.</b>
/// <c>FileMemoryStore.ParseQualifiedName</c> has no <c>~</c> branch: a bare ephemeral name
/// <c>~note</c> falls through to <c>("local", SanitizeName("~note"))</c> — the scope is
/// <c>"local"</c>, never <c>"ephemeral"</c>, and the subject keeps its <c>~</c> prefix (sanitizing
/// does not strip it). That is also the pair the STORE path indexes under
/// (<c>CoreEmbeddingEventHandler.EmbedAndIndexAsync</c> et al. call
/// <c>ParseQualifiedName</c> too), so <see cref="ReadForAppendAsync"/> — the append entry point —
/// keys its ephemeral check on the qualified NAME and returns that same <c>(scope, subject)</c>
/// pair. Keying it on the parsed SCOPE instead (as if <c>~note</c> resolved to scope
/// <c>"ephemeral"</c>) would index the append under a key the store path never uses, orphaning
/// the store path's vectors under a second, unreachable scope rather than updating them.</para>
///
/// <para>The reindex entry point, <see cref="ReadArtifactAsync(IMemoryStore, string, string, CancellationToken)"/>,
/// is scope-keyed by contrast, because its callers already have a <c>(scope, subject)</c> pair in
/// hand from <c>IMemoryStore.ListScoped()</c> — including a literal <c>"ephemeral"</c> scope for
/// ephemeral memories, since <c>ListScoped()</c> includes them by default. The CLI's
/// <c>EmbeddingReindexer</c> filters ephemeral scope out of its own item list before ever calling
/// this, so it never passes <c>"ephemeral"</c> through — but a whole-store reindex loop
/// (<c>EmbeddingsPlugin.ReindexStoreAsync</c>) does not filter, and relies on this method taking
/// the ephemeral branch for those items in the same long-lived process where the ephemeral store
/// is still populated. One rule, expressed once — but two different keys, because the two kinds of
/// caller start from two different things (a name vs. an already-resolved scope).</para>
/// </summary>
public static class MemoryArtifactSource
{
    /// <summary>The scope name whose memories live outside the (scope, subject) artifact layout.</summary>
    public const string EphemeralScope = "ephemeral";

    internal static bool IsEphemeral(string scope) =>
        scope.Equals(EphemeralScope, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the raw stored artifact for <paramref name="subject"/> in <paramref name="scope"/>,
    /// taking the ephemeral path when the SCOPE calls for it. For reindex callers only — see the
    /// class doc for why this is scope-keyed rather than name-keyed like
    /// <see cref="ReadForAppendAsync"/>.
    ///
    /// <para>Resolves by (scope, subject) rather than round-tripping through
     /// Address-based reads avoid reconstructing a path from a display-oriented scope label.
     /// This preserves namespaced topic addresses exactly.</para>
    /// </summary>
    internal static Task<string> ReadArtifactAsync(
        IMemoryStore store, string scope, string subject, CancellationToken ct) =>
        IsEphemeral(scope)
            ? store.ReadArtifactAsync(MemoryAddress.Parse($"~{subject}"), ct)
            : ReadPersistentArtifactAsync(store, scope, subject, ct);

    private static async Task<string> ReadPersistentArtifactAsync(
        IMemoryStore store, string scope, string subject, CancellationToken ct)
    {
        var entry = store.LoadIndex(scope).FirstOrDefault(candidate =>
            candidate.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new FileNotFoundException($"Memory '{subject}' was not found in scope '{scope}'.");
        return await store.ReadArtifactAsync(entry.Address, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a memory and decodes it back into the content array a write would have carried —
    /// the layout <see cref="MemoryVectorIndexer"/> must reproduce, not a single decoded blob.
    /// Returns an empty array when the memory holds nothing embeddable, so callers can treat
    /// "no content" as a skip without repeating the blank/empty checks. For reindex callers —
    /// see the class doc.
    /// </summary>
    public static async Task<string[]> ReadContentElementsAsync(
        IMemoryStore store, MemoryAddress address, CancellationToken ct)
    {
        string artifact = await store.ReadArtifactAsync(address, ct);
        return DecodeContentElements(artifact);
    }

    /// <summary>
    /// The append re-embed rule: resolve a qualified name to the (scope, subject) the vector
    /// index is keyed by — the SAME pair the store path derives, so an append updates the
    /// vectors the store wrote rather than a second set — and read that memory's content
    /// elements.
    ///
    /// <para>Ephemerality is a property of the NAME (<c>~subject</c>), not of the scope:
    /// <c>ParseQualifiedName</c> yields <c>("local", "~subject")</c> for an ephemeral name, so a
    /// scope-keyed test can never see it. This checks <c>store.IsEphemeral(qualifiedName)</c>
    /// instead, and still returns the <c>ParseQualifiedName</c>-derived pair for indexing, so the
    /// three append call sites index under exactly what the store path indexed under.</para>
    /// </summary>
    public static async Task<string[]> ReadForAppendAsync(
        IPluginStore store, MemoryAddress address, CancellationToken ct)
    {
        string artifact = await store.ReadArtifactAsync(address, ct);
        return DecodeContentElements(artifact);
    }

    private static string[] DecodeContentElements(string artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact)) return [];

        var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
        return elements.Any(e => !string.IsNullOrWhiteSpace(e)) ? elements : [];
    }
}
