using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Core.Embeddings
{
    public interface IVectorStore
    {
        IReadOnlyList<VectorEntry> GetVectors(string scope);
        Task RemoveAsync(string scope, string name, CancellationToken ct = default);
        int Count();
        Task UpsertAsync(string scope, string name, int? chunkIndex, float[] vector, CancellationToken ct = default);
    }
}