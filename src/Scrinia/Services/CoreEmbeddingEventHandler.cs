using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Embeddings;

namespace Scrinia.Services;

/// <summary>
/// In-process <see cref="IMemoryEventSink"/> that embeds content and upserts vectors
/// using the built-in embedding provider. Used when no external plugin overrides it.
/// </summary>
internal sealed class CoreEmbeddingEventHandler : IMemoryEventSink
{
    private readonly IEmbeddingProvider _provider;
    private readonly VectorStore _store;
    private readonly ILogger _logger;

    public CoreEmbeddingEventHandler(IEmbeddingProvider provider, VectorStore store, ILogger logger)
    {
        _provider = provider;
        _store = store;
        _logger = logger;
    }

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore memoryStore, CancellationToken ct)
    {
        await EmbedAndIndexAsync(qualifiedName, content, memoryStore, ct);
    }

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore memoryStore, CancellationToken ct)
    {
        await EmbedAndIndexAsync(qualifiedName, [content], memoryStore, ct);
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore memoryStore, CancellationToken ct)
    {
        if (!wasDeleted) return;

        try
        {
            var (scope, subject) = memoryStore.ParseQualifiedName(qualifiedName);
            await _store.RemoveAsync(scope, subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove vectors for '{Name}'", qualifiedName);
        }
    }

    private async Task EmbedAndIndexAsync(string qualifiedName, string[] content, IMemoryStore memoryStore, CancellationToken ct)
    {
        if (!_provider.IsAvailable) return;

        try
        {
            var (scope, subject) = memoryStore.ParseQualifiedName(qualifiedName);

            string joined = string.Concat(content);
            if (string.IsNullOrWhiteSpace(joined))
                return;

            // Collect all texts to embed in one batch
            var items = new List<(string text, int? chunkIndex)> { (joined, null) };

            if (content.Length > 1)
            {
                for (int i = 0; i < content.Length; i++)
                    if (!string.IsNullOrWhiteSpace(content[i]))
                        items.Add((content[i], i + 1));
            }

            // Single batched embed call
            var vectors = await _provider.EmbedBatchAsync(
                items.Select(x => x.text).ToList(), ct);
            if (vectors is null) return;

            for (int i = 0; i < vectors.Length; i++)
                await _store.UpsertAsync(scope, subject, items[i].chunkIndex, vectors[i], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
        }
    }
}
