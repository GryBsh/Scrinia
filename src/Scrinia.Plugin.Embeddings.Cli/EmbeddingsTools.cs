using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Scrinia.Core.Embeddings;

namespace Scrinia.Plugin.Embeddings.Cli;

[McpServerToolType]
public sealed class EmbeddingsTools(
    VectorStore vectorStore, IEmbeddingProvider provider, EmbeddingOptions options,
    MemoryVectorIndexer indexer)
{
    [McpServerTool(Name = "index")]
    [Description("Embed a memory's full content array and replace its vectors. Preferred over upsert.")]
    public async Task<string> Index(
        [Description("Scope")] string scope,
        [Description("Memory name")] string name,
        [Description("The memory's content elements, in order")] string[] content,
        CancellationToken ct = default)
    {
        try
        {
            // retryOnUnavailable: false — this tool is the wire surface for BOTH a single write
            // AND a bulk `scri reindex` replay (McpPluginHost.OnStoredAsync, awaited once per
            // memory). The default `true` would route through VectorWriteRetry's up-to-42s
            // backoff PER MEMORY here, on top of the retry McpPluginHost.CallToolWithRetryAsync
            // already owns at the wire level — double retry, and a 1000-memory reindex against a
            // locked vector file would stall for hours instead of failing fast. Retry belongs at
            // exactly one layer; CallToolWithRetryAsync is that layer for this process boundary.
            bool wrote = await indexer.IndexAsync(scope, name, content, ct, retryOnUnavailable: false);
            return JsonSerializer.Serialize(new { ok = wrote });
        }
        catch (VectorStoreUnavailableException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "status")]
    [Description("Returns plugin status as JSON.")]
    public string Status()
    {
        return JsonSerializer.Serialize(new
        {
            provider = provider.GetType().Name,
            available = provider.IsAvailable,
            dimensions = provider.Dimensions,
            vectorCount = vectorStore.Count(),
        });
    }

    [McpServerTool(Name = "search")]
    [Description("Semantic vector search. Returns JSON dict of key:score pairs.")]
    public async Task<string> Search(
        [Description("Query text")] string query,
        [Description("Scopes to search")] string[] scopes,
        CancellationToken ct = default)
    {
        var queryVec = await provider.EmbedAsync(query, ct);
        if (queryVec is null)
            return JsonSerializer.Serialize(new { error = "Query embedding failed" });

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (string scope in scopes)
        {
            var vectors = vectorStore.GetVectors(scope);
            if (vectors.Count == 0) continue;

            var topK = VectorIndex.Search(queryVec, vectors, vectors.Count);
            VectorScoreProjection.Project(
                scope, topK.Select(t => (t.Entry, (double)t.Similarity)), options.SemanticWeight, scores);
        }

        return JsonSerializer.Serialize(scores);
    }

    [McpServerTool(Name = "upsert")]
    [Description("Embed text and store/update the vector.")]
    public async Task<string> Upsert(
        [Description("Scope")] string scope,
        [Description("Memory name")] string name,
        [Description("Text to embed")] string text,
        [Description(
            "Legacy per-window chunk index. Any value passed here is below one chunk-index "
            + "stride, so the read side resolves it to content element 0 — which does not exist "
            + "— and the vector contributes to the memory's score but never to a chunk's. "
            + "Use the `index` tool to get correctly-keyed per-element vectors.")]
        int? chunkIndex = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return JsonSerializer.Serialize(new { ok = true });

        var vector = await provider.EmbedAsync(text, ct);
        if (vector is null)
            return JsonSerializer.Serialize(new { error = "Embedding failed" });

        try
        {
            await vectorStore.UpsertAsync(scope, name, chunkIndex, vector, ct);
        }
        catch (VectorStoreUnavailableException ex)
        {
            // Same error shape as a failed embed; the caller retries or falls back to BM25.
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        return JsonSerializer.Serialize(new { ok = true });
    }

    [McpServerTool(Name = "remove")]
    [Description("Remove all vectors for a memory.")]
    public async Task<string> Remove(
        [Description("Scope")] string scope,
        [Description("Memory name")] string name,
        CancellationToken ct = default)
    {
        try
        {
            await vectorStore.RemoveAsync(scope, name, ct);
        }
        catch (VectorStoreUnavailableException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        return JsonSerializer.Serialize(new { ok = true });
    }
}
