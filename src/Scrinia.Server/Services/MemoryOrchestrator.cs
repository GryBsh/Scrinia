using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Server.Models;

namespace Scrinia.Server.Services;

/// <summary>
/// Stateless business logic for memory operations — extracted from ScriniaMcpTools.
/// Takes <see cref="IMemoryStore"/> as parameter for testability.
///
/// <para><b>It does not derive.</b> <see cref="StoreAsync"/> and <see cref="AppendAsync"/> resolve the
/// caller's name, hand the raw request to <see cref="IMemoryStore.StoreAsync"/> /
/// <see cref="IMemoryStore.AppendAsync"/>, and shape the REST response from the entry the store
/// returns. Keywords, term frequencies, attributes, chunk entries and the preview all come from the
/// one pipeline inside the store (<c>MemoryDerivation</c>); so do the archive, the artifact write,
/// the index upsert and the rollback. Everything this class used to compute for itself was a fourth
/// copy of that contract.</para>
/// </summary>
public static class MemoryOrchestrator
{
    public static async Task<StoreResponse> StoreAsync(
        IMemoryStore store, StoreRequest req, CancellationToken ct = default)
    {
        var (ephemeral, scope, subject) = Resolve(store, req.Name);

        var entry = await store.StoreAsync(
            // req.Keywords is the CALLER's raw list (D-01) — nothing derived is ever handed back in
            // as input, which is why this method computes nothing to hand it.
            new MemoryWrite(subject, scope, req.Content, req.Description, req.Attributes, req.Keywords),
            ct);

        return BuildStoreResponse(store, entry, ephemeral, scope);
    }

    /// <summary>Splits a user-facing name into the resolved <c>(scope, subject)</c> pair every store
    /// member takes. An ephemeral <c>~name</c> is its own whole address and keeps its prefix — the
    /// store routes it to ephemeral storage itself, so there is no second ephemeral path here.</summary>
    private static (bool Ephemeral, string Scope, string Subject) Resolve(IMemoryStore store, string name)
    {
        if (store.IsEphemeral(name))
            return (true, "", name);
        var (scope, subject) = store.ParseQualifiedName(name);
        return (false, scope, subject);
    }

    /// <summary>The display name for a written entry. <paramref name="entry"/> carries the name the
    /// store resolved, so a memory written under a mismatched casing reports its own.</summary>
    private static string QualifiedNameOf(IMemoryStore store, ArtifactEntry entry, bool ephemeral, string scope) =>
        ephemeral ? $"~{entry.Name}" : store.FormatQualifiedName(scope, entry.Name);

    private static StoreResponse BuildStoreResponse(
        IMemoryStore store, ArtifactEntry entry, bool ephemeral, string scope)
    {
        string qualifiedName = QualifiedNameOf(store, entry, ephemeral, scope);
        return new StoreResponse(entry.Name, qualifiedName, entry.ChunkCount, entry.OriginalBytes,
            $"Remembered: {qualifiedName} ({entry.ChunkCount} chunk(s), {entry.OriginalBytes} B)"
                + (ephemeral ? " [ephemeral]" : ""));
    }

    public static async Task<AppendResponse> AppendAsync(
        IMemoryStore store, string name, string content, CancellationToken ct = default)
    {
        var (ephemeral, scope, subject) = Resolve(store, name);

        // Exactly one chunk: the store refuses any other count by design, because the derivation
        // pipeline can express only one appended chunk.
        var entry = await store.AppendAsync(new MemoryWrite(subject, scope, [content]), ct);

        // Appending to a memory that does not exist yet is a store, and the store performs it
        // itself rather than handing the decision back. UpdatedAt is the discriminator: a real
        // append always sets it, and it is null only for a first write. Reporting that case as a
        // store keeps the answer the MCP append tool gives for the same input, which returns
        // Remember's response.
        if (entry.UpdatedAt is null)
        {
            var stored = BuildStoreResponse(store, entry, ephemeral, scope);
            return new AppendResponse(stored.Name, stored.ChunkCount, stored.OriginalBytes, stored.Message);
        }

        string qualifiedName = QualifiedNameOf(store, entry, ephemeral, scope);
        return new AppendResponse(qualifiedName, entry.ChunkCount, entry.OriginalBytes,
            $"Appended chunk {entry.ChunkCount} to {qualifiedName} ({entry.ChunkCount} chunk(s), {entry.OriginalBytes} B)");
    }

    public static async Task<ShowResponse?> ShowAsync(
        IMemoryStore store, string name, CancellationToken ct = default)
    {
        string artifact;
        try
        {
            artifact = await store.ResolveArtifactAsync(name, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (!artifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return null;

        // One decompression pass per chunk, not two: DecodeAllChunksVerified decodes each
        // ##CHUNK: section exactly once and still runs the same length/CRC32 check
        // Nmp2Strategy.Decode performs on the whole artifact (see its doc for why a naive
        // Decode() + per-index DecodeChunk() loop double-decompresses every chunk).
        (string[] content, long totalBytes) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact);

        return new ShowResponse(name, content, totalBytes);
    }

    public static async Task<bool> ForgetAsync(
        IMemoryStore store, string name, CancellationToken ct = default)
    {
        if (store.IsEphemeral(name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            return store.ForgetEphemeral(key);
        }

        var (scope, subject) = store.ParseQualifiedName(name);
        bool removed = store.Remove(subject, scope);
        bool deleted = store.DeleteArtifact(subject, scope);
        return deleted || removed;
    }

}
