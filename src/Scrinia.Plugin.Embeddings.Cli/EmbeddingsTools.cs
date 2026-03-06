using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Scrinia.Core.Embeddings;

namespace Scrinia.Plugin.Embeddings.Cli;

[McpServerToolType]
public sealed class EmbeddingsTools(
    VectorStore vectorStore, IEmbeddingProvider provider, EmbeddingOptions options)
{
    [McpServerTool(Name = "status")]
    [Description("Returns plugin status as JSON.")]
    public string Status()
    {
        return JsonSerializer.Serialize(new
        {
            provider = provider.GetType().Name,
            available = provider.IsAvailable,
            dimensions = provider.Dimensions,
            vectorCount = vectorStore.TotalVectorCount(),
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
            foreach (var (entry, similarity) in topK)
            {
                string key = entry.ChunkIndex is not null
                    ? $"{scope}|{entry.Name}|{entry.ChunkIndex}"
                    : $"{scope}|{entry.Name}";

                scores[key] = similarity * options.SemanticWeight;
            }
        }

        return JsonSerializer.Serialize(scores);
    }

    [McpServerTool(Name = "upsert")]
    [Description("Embed text and store/update the vector.")]
    public async Task<string> Upsert(
        [Description("Scope")] string scope,
        [Description("Memory name")] string name,
        [Description("Text to embed")] string text,
        [Description("Chunk index (1-based)")] int? chunkIndex = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return JsonSerializer.Serialize(new { ok = true });

        var vector = await provider.EmbedAsync(text, ct);
        if (vector is null)
            return JsonSerializer.Serialize(new { error = "Embedding failed" });

        await vectorStore.UpsertAsync(scope, name, chunkIndex, vector, ct);
        return JsonSerializer.Serialize(new { ok = true });
    }

    [McpServerTool(Name = "remove")]
    [Description("Remove all vectors for a memory.")]
    public async Task<string> Remove(
        [Description("Scope")] string scope,
        [Description("Memory name")] string name,
        CancellationToken ct = default)
    {
        await vectorStore.RemoveAsync(scope, name, ct);
        return JsonSerializer.Serialize(new { ok = true });
    }
}
