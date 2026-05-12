using BenchmarkDotNet.Attributes;
using Scrinia.Core.Embeddings;

namespace Scrinia.Benchmarks;

/// <summary>
/// Measures HNSW insert and nearest-neighbor search throughput at sizes where
/// HNSW is meant to dominate flat scan (≥1000 vectors). The reader-writer lock
/// refactor under P1.8 should keep read-throughput steady as the graph grows.
/// </summary>
[MemoryDiagnoser]
public class HnswIndexBenchmarks
{
    [Params(1_000, 10_000)]
    public int VectorCount;

    private const int Dims = 32;
    private HnswIndex _index = null!;
    private float[][] _queryVectors = null!;
    private int _queryCursor;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _index = new HnswIndex();
        for (int i = 0; i < VectorCount; i++)
            _index.Insert($"vec-{i}", RandomUnitVector(rng));

        // Pre-generate a small pool of query vectors so iteration cost is just the search.
        _queryVectors = new float[256][];
        for (int i = 0; i < _queryVectors.Length; i++)
            _queryVectors[i] = RandomUnitVector(rng);
    }

    [GlobalCleanup]
    public void Cleanup() => _index.Dispose();

    [Benchmark]
    public IReadOnlyList<(string, float)> SearchTopK_10()
    {
        var query = _queryVectors[_queryCursor++ & 255];
        return _index.Search(query, topK: 10);
    }

    [Benchmark]
    public IReadOnlyList<(string, float)> SearchTopK_50()
    {
        var query = _queryVectors[_queryCursor++ & 255];
        return _index.Search(query, topK: 50);
    }

    private static float[] RandomUnitVector(Random rng)
    {
        float[] v = new float[Dims];
        for (int i = 0; i < Dims; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1);

        float norm = 0;
        foreach (float f in v) norm += f * f;
        norm = MathF.Sqrt(norm);
        if (norm > 0)
            for (int i = 0; i < Dims; i++)
                v[i] /= norm;
        return v;
    }
}
