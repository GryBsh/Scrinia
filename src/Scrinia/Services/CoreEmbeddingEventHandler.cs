using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;

namespace Scrinia.Services;

/// <summary>
/// In-process <see cref="IMemoryEventSink"/> that embeds content and upserts vectors
/// using the built-in embedding provider. Used when no external plugin overrides it.
///
/// <para>Delegates to <see cref="MemoryVectorIndexer"/> — the single embed-and-index policy —
/// so long memories produce multiple vectors (one per overlapping window within each content
/// element), keeping every part of the memory reachable via semantic search. Search-time dedup
/// in <c>WeightedAttributeScorer.SearchAll</c> collapses chunk matches back to one result per
/// memory.</para>
/// </summary>
internal sealed class CoreEmbeddingEventHandler : IMemoryEventSink
{
    private readonly ILogger _logger;
    private readonly MemoryVectorIndexer _indexer;

    public CoreEmbeddingEventHandler(
        IEmbeddingProvider provider, VectorStore store, ILogger logger, EmbeddingOptions? options = null)
    {
        _logger = logger;
        _indexer = new MemoryVectorIndexer(provider, store, logger, options);
    }

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore memoryStore, CancellationToken ct)
    {
        await EmbedAndIndexAsync(qualifiedName, content, memoryStore, ct);
    }

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore memoryStore, CancellationToken ct)
    {
        // An append mutates the memory. Indexing only the appended fragment would be wrong twice
        // over: the indexer replaces a memory's vectors atomically, so the rest of the memory
        // would lose its vectors entirely, and the surviving element indices would not match the
        // memory's real content array. Re-read and index the whole thing, as the plugin already
        // does (EmbeddingsPlugin.cs).
        try
        {
            var (scope, subject) = memoryStore.ParseQualifiedName(qualifiedName);
            string artifact = await memoryStore.ReadArtifactAsync(subject, scope, ct);
            if (string.IsNullOrWhiteSpace(artifact)) return;

            var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
            if (elements.Length == 0) return;

            await _indexer.IndexAsync(scope, subject, elements, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-embed appended memory '{Name}'", qualifiedName);
        }
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore memoryStore, CancellationToken ct)
    {
        if (!wasDeleted) return;

        // Uses the (qualifiedName, store) overload, which owns the ParseQualifiedName call
        // inside its own try — a malformed qualifiedName must not throw out of an event-sink
        // handler with no guard of its own.
        await _indexer.RemoveAsync(qualifiedName, memoryStore, ct);
    }

    private async Task EmbedAndIndexAsync(
        string qualifiedName, string[] content, IMemoryStore memoryStore, CancellationToken ct)
    {
        try
        {
            var (scope, subject) = memoryStore.ParseQualifiedName(qualifiedName);
            await _indexer.IndexAsync(scope, subject, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
        }
    }
}
