using Scrinia.Core.Embeddings;
using Scrinia.Core.Embeddings.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class VectorStoreFreshnessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scrinia-vector-fresh-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmReader_ObservesAnotherWritersReplacementAndRemoval(bool candidatesOnly)
    {
        using var writer = new VectorStore(_root);
        using var reader = new VectorStore(_root);
        await writer.UpsertAsync("local", "note", null, [1f, 0f]);
        var original = reader.GetVectors("local");

        await writer.ReplaceMemoryVectorsAsync("local", "note", [(null, [0f, 1f])]);

        var replacement = Read(reader, candidatesOnly);
        Assert.Equal([0f, 1f], Assert.Single(replacement).Vector);
        Assert.Equal([1f, 0f], Assert.Single(original).Vector);

        await writer.RemoveAsync("local", "note");
        Assert.Empty(Read(reader, candidatesOnly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmEmptyReader_ObservesAnotherWritersNewFile(bool candidatesOnly)
    {
        using var reader = new VectorStore(_root);
        Assert.Empty(reader.GetVectors("local"));
        using var writer = new VectorStore(_root);

        await writer.UpsertAsync("local", "note", null, [1f, 0f]);

        Assert.Single(Read(reader, candidatesOnly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmReader_ObservesDeletedFile(bool candidatesOnly)
    {
        using var writer = new VectorStore(_root);
        using var reader = new VectorStore(_root);
        await writer.UpsertAsync("local", "note", null, [1f, 0f]);
        Assert.Single(reader.GetVectors("local"));

        File.Delete(Path.Combine(_root, "local", "vectors.bin"));

        Assert.Empty(Read(reader, candidatesOnly));
    }

    private static IReadOnlyList<VectorEntry> Read(VectorStore store, bool candidatesOnly) =>
        candidatesOnly ? store.GetCandidateVectors("local", ["note"]) : store.GetVectors("local");

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
