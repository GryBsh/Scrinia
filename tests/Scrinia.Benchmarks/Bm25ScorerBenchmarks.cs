using BenchmarkDotNet.Attributes;
using Scrinia.Core.Search;

namespace Scrinia.Benchmarks;

/// <summary>
/// Measures the cost of <see cref="Bm25Scorer.ComputeCorpusStats"/> and a per-query
/// <see cref="Bm25Scorer.Score"/> call across representative corpus sizes.
///
/// The interesting allocation reduction is in <c>ComputeCorpusStats</c> — the
/// pre-sizing hint introduced under P1.7 removes rehash cycles when the doc count
/// is known up-front. The <c>Score</c> benchmark establishes a steady-state floor
/// for a typical search query.
/// </summary>
[MemoryDiagnoser]
public class Bm25ScorerBenchmarks
{
    [Params(10, 100, 1_000, 10_000)]
    public int DocumentCount;

    private List<IReadOnlyDictionary<string, int>?> _corpus = null!;
    private string[] _queryTerms = null!;
    private (double AvgDocLen, Dictionary<string, int> DocFreqs) _stats;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _corpus = new List<IReadOnlyDictionary<string, int>?>(DocumentCount);
        for (int i = 0; i < DocumentCount; i++)
        {
            int uniqueTerms = 20 + rng.Next(20);
            var tf = new Dictionary<string, int>(uniqueTerms);
            for (int t = 0; t < uniqueTerms; t++)
                tf[$"term-{rng.Next(2000)}"] = 1 + rng.Next(5);
            _corpus.Add(tf);
        }

        _queryTerms = ["term-42", "term-100", "term-1500"];

        // Precompute stats for the Score benchmark.
        _stats = Bm25Scorer.ComputeCorpusStats(_corpus, docCountHint: _corpus.Count);
    }

    [Benchmark]
    public Dictionary<string, int> ComputeCorpusStats_WithHint()
    {
        var (_, docFreqs) = Bm25Scorer.ComputeCorpusStats(_corpus, docCountHint: _corpus.Count);
        return docFreqs;
    }

    [Benchmark]
    public Dictionary<string, int> ComputeCorpusStats_WithoutHint()
    {
        var (_, docFreqs) = Bm25Scorer.ComputeCorpusStats(_corpus);
        return docFreqs;
    }

    [Benchmark]
    public double Score_SingleQuery()
    {
        // Score against the first document in the corpus as a typical case.
        return Bm25Scorer.Score(
            queryTerms: _queryTerms,
            entryTf: _corpus[0]!,
            entryDocLength: _corpus[0]!.Values.Sum(),
            avgDocLength: _stats.AvgDocLen,
            corpusSize: _corpus.Count,
            documentFrequencies: _stats.DocFreqs);
    }
}
