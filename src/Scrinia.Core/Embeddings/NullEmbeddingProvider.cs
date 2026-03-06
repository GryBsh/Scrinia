namespace Scrinia.Core.Embeddings;

/// <summary>No-op fallback when no embedding provider is available.</summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public bool IsAvailable => false;
    public int Dimensions => 0;
    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
    public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) => Task.FromResult<float[][]?>(null);
    public void Dispose() { }
}
