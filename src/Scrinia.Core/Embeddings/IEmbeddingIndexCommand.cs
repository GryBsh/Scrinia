using Scrinia.Core.Models;

namespace Scrinia.Core.Embeddings;

/// <summary>The acknowledged result of an administrative embedding index command.</summary>
public enum EmbeddingIndexOutcome
{
    /// <summary>The command did not confirm successful indexing.</summary>
    Failed,
    /// <summary>The embedding pipeline confirmed that the memory was indexed.</summary>
    Indexed,
    /// <summary>The embedding pipeline could not accept the command.</summary>
    Unavailable,
}

/// <summary>
/// Indexes a memory with an explicit acknowledgement. Administrative operations use this
/// contract instead of inferring success from a best-effort <see cref="IMemoryEventSink"/>.
/// Implementations propagate cancellation and return Indexed only after confirmation.
/// </summary>
public interface IEmbeddingIndexCommand
{
    /// <summary>Indexes the complete content array and reports its acknowledged outcome.</summary>
    Task<EmbeddingIndexOutcome> IndexAsync(
        MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct);
}
