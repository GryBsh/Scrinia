using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Builds a configured <see cref="ScriniaRetriever"/> from CLI args. Shared across
/// subcommands so the <c>--mode</c> / <c>--model-dir</c> contract is consistent.
/// Headline runs neutralize Scrinia's workspace-tuned ranker defaults so the
/// apples-to-apples baseline against MemPalace's "raw" track is reproducible —
/// recency / importance / MMR are off by default and turned on only in the
/// ablation commit.
/// </summary>
internal static class RetrieverFactory
{
    public enum Mode { Bm25, Bm25Embeddings }

    public static (ScriniaRetriever Retriever, Mode Mode) Build(string[] args, string benchmarkSlug)
    {
        Mode mode = ParseMode(Program.Flag(args, "--mode") ?? "bm25_embeddings");
        string? modelDir = Program.Flag(args, "--model-dir");

        // Headline parity: zero α_recency and α_importance for benchmarks. The default
        // RankerOptions are tuned for workspace memory (τ=14d) which would distort
        // months-scale temporal benchmarks. The ablation commit (commit 5) sweeps
        // these knobs explicitly.
        var rankerOptions = new RankerOptions(
            AlphaRelevance: 1.0,
            AlphaRecency: 0.0,
            AlphaImportance: 0.0);

        IEmbeddingProvider? embedder = null;
        if (mode == Mode.Bm25Embeddings)
        {
            embedder = EmbeddingProviderLoader.TryLoadModel2Vec(modelDir);
            if (embedder is null)
            {
                Console.Error.WriteLine(
                    "warning: --mode=bm25_embeddings requested but Model2Vec model not found. " +
                    "Run `scri setup` or download m2v-MiniLM-L6-v2 to ./models/. " +
                    "Falling back to BM25-only.");
                mode = Mode.Bm25;
            }
        }

        string root = Path.Combine(Path.GetTempPath(),
            $"scrinia-retbench-{benchmarkSlug}-{Guid.NewGuid().ToString("N")[..8]}");
        var retriever = new ScriniaRetriever(root, rankerOptions, embedder);
        return (retriever, mode);
    }

    public static string ModeLabel(Mode mode) => mode switch
    {
        Mode.Bm25 => "bm25",
        Mode.Bm25Embeddings => "bm25_embeddings",
        _ => mode.ToString().ToLowerInvariant(),
    };

    private static Mode ParseMode(string raw) => raw.ToLowerInvariant() switch
    {
        "bm25" or "raw" => Mode.Bm25,
        "bm25_embeddings" or "embeddings" or "hybrid" => Mode.Bm25Embeddings,
        _ => throw new ArgumentException($"unknown --mode '{raw}'. Expected: bm25 | bm25_embeddings"),
    };
}
