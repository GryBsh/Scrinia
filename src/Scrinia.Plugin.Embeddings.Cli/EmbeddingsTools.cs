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
            return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(wrote, wrote ? null : "Index operation failed."));
        }
        catch (VectorStoreUnavailableException ex)
        {
            return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(false, ex.Message));
        }
    }

    [Obsolete("Compatibility-only protocol v1 tool. Hosts must use index after a successful handshake.")]
    [McpServerTool(Name = "upsert")]
    [Description("Deprecated compatibility operation for older embeddings clients; use index instead.")]
    public async Task<string> Upsert(
        [Description("Scope")] string scope,
        [Description("Memory name")] string name,
        [Description("Text to embed")] string text,
        [Description("Legacy chunk index")] int? chunkIndex = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(true, null));

        var vector = await provider.EmbedAsync(text, ct);
        if (vector is null)
            return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(false, "Embedding failed"));

        if (provider.Dimensions > 0 && vector.Length != provider.Dimensions)
        {
            return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(
                false, $"Embedding returned {vector.Length} dimensions; expected {provider.Dimensions}."));
        }

        try
        {
            await vectorStore.UpsertAsync(scope, name, chunkIndex, vector, ct);
        }
        catch (VectorStoreUnavailableException ex)
        {
            return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(false, ex.Message));
        }

        return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(true, null));
    }

    [McpServerTool(Name = "status")]
    [Description("Returns plugin status as JSON.")]
    public string Status()
    {
        return EmbeddingsProtocolJson.Serialize(new EmbeddingsStatusResponse(
            EmbeddingsProtocol.Version,
            EmbeddingsProtocol.RequiredCapabilities.ToArray(),
            provider.IsAvailable,
            provider.Dimensions,
            vectorStore.ExpectedSignature ?? provider.Signature));
    }

    [McpServerTool(Name = "search")]
    [Description("Semantic vector search. Returns JSON dict of key:score pairs.")]
    public async Task<string> Search(
        [Description("Query text")] string query,
        [Description("Candidate memory names grouped by scope")] Dictionary<string, string[]> candidatesByScope,
        CancellationToken ct = default)
    {
        var queryVec = await provider.EmbedAsync(query, ct);
        if (queryVec is null)
            return EmbeddingsProtocolJson.Serialize(new EmbeddingsSearchResponse(
                new Dictionary<string, double>(), "Query embedding failed"));

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in candidatesByScope)
        {
            var vectors = vectorStore.GetCandidateVectors(pair.Key, pair.Value);
            if (vectors.Count == 0) continue;

            var topK = VectorIndex.Search(queryVec, vectors, vectors.Count);
            VectorScoreProjection.Project(
                pair.Key, topK.Select(t => (t.Entry, (double)t.Similarity)), options.SemanticWeight, scores);
        }

        return EmbeddingsProtocolJson.Serialize(new EmbeddingsSearchResponse(scores, null));
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
            return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(false, ex.Message));
        }
        return EmbeddingsProtocolJson.Serialize(new EmbeddingsOperationResponse(true, null));
    }
}
