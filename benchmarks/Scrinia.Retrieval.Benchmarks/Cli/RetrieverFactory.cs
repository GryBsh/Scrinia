using System.Globalization;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Builds a configured <see cref="ScriniaRetriever"/> from CLI args. Shared across
/// subcommands so the <c>--mode</c> / <c>--model-dir</c> contract is consistent.
///
/// <para><b>Headline runs (no ablation flags):</b> recency / importance / MMR all
/// neutralized so the apples-to-apples baseline against MemPalace's "raw" track is
/// reproducible. The workspace-tuned defaults (τ=14d, MMR=0.6) would distort
/// months-scale temporal benchmarks like LongMemEval and LoCoMo.</para>
///
/// <para><b>Ablation runs:</b> <c>--alpha-recency</c>, <c>--tau-days</c>,
/// <c>--alpha-importance</c>, <c>--diversity-lambda</c>, and <c>--diversity-inner-limit</c>
/// turn the ranker knobs back on so commit 5's sweeps can isolate the contribution of
/// each Scrinia feature. Importance ablation requires that the haystack docs already
/// carry importance scores — not produced for benchmark ingest in this commit, so the
/// flag is wired but the effect is null until per-doc importance scoring is added.</para>
/// </summary>
internal static class RetrieverFactory
{
    public enum Mode { Bm25, Bm25Embeddings }

    public static (ScriniaRetriever Retriever, Mode Mode) Build(string[] args, string benchmarkSlug)
    {
        Mode mode = ParseMode(Program.Flag(args, "--mode") ?? "bm25_embeddings");
        string? modelDir = Program.Flag(args, "--model-dir");

        // Ablation knobs default to "off" so the headline run stays apples-to-apples.
        double alphaRecency = ParseDouble(Program.Flag(args, "--alpha-recency"), 0.0);
        double alphaImportance = ParseDouble(Program.Flag(args, "--alpha-importance"), 0.0);
        double tauDays = ParseDouble(Program.Flag(args, "--tau-days"), RankerOptions.Default.TauDays);
        double diversityLambda = ParseDouble(Program.Flag(args, "--diversity-lambda"), 1.0);
        int diversityInnerLimit = ParseInt(Program.Flag(args, "--diversity-inner-limit"), 0);

        // If --diversity-lambda is set but no inner-limit, pick a sensible default so the
        // MMR pool is bigger than top-K — otherwise MMR has nothing to diversify from.
        if (Program.Flag(args, "--diversity-lambda") is not null && diversityInnerLimit == 0)
            diversityInnerLimit = 50;

        var rankerOptions = RankerOptions.Default with
        {
            AlphaRelevance = 1.0,
            AlphaRecency = alphaRecency,
            AlphaImportance = alphaImportance,
            TauDays = tauDays,
        };

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
        var retriever = new ScriniaRetriever(
            rootDir: root,
            rankerOptions: rankerOptions,
            embeddingProvider: embedder,
            diversityLambda: diversityLambda,
            diversityInnerLimit: diversityInnerLimit);
        return (retriever, mode);
    }

    /// <summary>
    /// Builds a mode label suffix for filenames + run metadata that captures any
    /// active ablation knobs. Headline runs (no ablation) get a clean
    /// <c>bm25_embeddings</c>; an ablation run picks up tags like
    /// <c>bm25_embeddings__rec0.5_tau90</c> so result files don't collide.
    /// </summary>
    public static string ModeLabel(Mode mode, string[]? args = null)
    {
        string baseLabel = mode switch
        {
            Mode.Bm25 => "bm25",
            Mode.Bm25Embeddings => "bm25_embeddings",
            _ => mode.ToString().ToLowerInvariant(),
        };
        if (args is null) return baseLabel;

        var tags = new List<string>();
        if (Program.Flag(args, "--alpha-recency") is { } r) tags.Add($"rec{r}");
        if (Program.Flag(args, "--tau-days") is { } tau) tags.Add($"tau{tau}");
        if (Program.Flag(args, "--alpha-importance") is { } imp) tags.Add($"imp{imp}");
        if (Program.Flag(args, "--diversity-lambda") is { } lam) tags.Add($"div{lam}");
        return tags.Count == 0 ? baseLabel : baseLabel + "__" + string.Join("_", tags);
    }

    private static Mode ParseMode(string raw) => raw.ToLowerInvariant() switch
    {
        "bm25" or "raw" => Mode.Bm25,
        "bm25_embeddings" or "embeddings" or "hybrid" => Mode.Bm25Embeddings,
        _ => throw new ArgumentException($"unknown --mode '{raw}'. Expected: bm25 | bm25_embeddings"),
    };

    private static double ParseDouble(string? raw, double @default) =>
        raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : @default;

    private static int ParseInt(string? raw, int @default) =>
        raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : @default;
}
