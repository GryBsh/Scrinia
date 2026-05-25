using System.Globalization;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm;

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

    /// <summary>
    /// Second-stage reranker selection. <see cref="None"/> means first-stage retrieval
    /// only (the historical default); <see cref="SentenceMaxSim"/> applies sentence-
    /// level MaxSim reranking on top-K candidates using the existing embedding model.
    /// </summary>
    public enum RerankMode { None, SentenceMaxSim }

    public static async Task<(ScriniaRetriever Retriever, Mode Mode)> BuildAsync(
        string[] args, string benchmarkSlug, CancellationToken ct)
    {
        Mode mode = ParseMode(Program.Flag(args, "--mode") ?? "bm25_embeddings");
        string? modelDir = Program.Flag(args, "--model-dir");
        EmbedProviderKind embedKind = EmbeddingProviderLoader.ParseKind(
            Program.Flag(args, "--embed-provider"));

        // Ablation knobs default to "off" so the headline run stays apples-to-apples.
        double alphaRecency = ParseDouble(Program.Flag(args, "--alpha-recency"), 0.0);
        double tauDays = ParseDouble(Program.Flag(args, "--tau-days"), RankerOptions.Default.TauDays);
        double diversityLambda = ParseDouble(Program.Flag(args, "--diversity-lambda"), 1.0);
        int diversityInnerLimit = ParseInt(Program.Flag(args, "--diversity-inner-limit"), 0);

        // If --diversity-lambda is set but no inner-limit, pick a sensible default so the
        // MMR pool is bigger than top-K — otherwise MMR has nothing to diversify from.
        if (Program.Flag(args, "--diversity-lambda") is not null && diversityInnerLimit == 0)
            diversityInnerLimit = 50;

        // Chunking knobs. --chunk none (default) reproduces the prior whole-doc baseline.
        // --chunk prod slices haystack docs via Scrinia.Core's TextChunker with the same
        // default window/overlap prod uses, so the bench measures the chunked retrieval
        // path real users hit instead of the monolithic-doc shortcut.
        bool chunked = ParseChunk(Program.Flag(args, "--chunk"));
        int chunkWindow = ParseInt(Program.Flag(args, "--chunk-window"), Scrinia.Core.Embeddings.TextChunker.DefaultWindowSize);
        int chunkOverlap = ParseInt(Program.Flag(args, "--chunk-overlap"), Scrinia.Core.Embeddings.TextChunker.DefaultOverlap);

        // Tier 2 LLM enrichment. --enrich none (default) measures Tier 1 retrieval only —
        // BM25 + embeddings — which is what the academic comparisons measure. --enrich tier2
        // adds ingest-time LLM ops (default: facts + description) so the bench measures
        // scrinia's actual product surface, where Tier 2 enrichment lifts BM25 recall via
        // fact-token TF seeding and the field scorer's description-match signal.
        Tier2Enricher? enricher = null;
        if (ParseEnrich(Program.Flag(args, "--enrich")))
        {
            string provider = Program.Flag(args, "--enrich-provider")
                ?? throw new ArgumentException(
                    "--enrich tier2 requires --enrich-provider (claude-cli | codex-cli | copilot-cli | ollama | openai)");
            string? model = Program.Flag(args, "--enrich-model");
            string? baseUrl = Program.Flag(args, "--enrich-base-url");
            string? apiKey = Program.Flag(args, "--enrich-api-key");
            var ops = EnrichOps.Parse(Program.Flag(args, "--enrich-ops"));
            var backend = await BenchLlmBackendBuilder.BuildAsync(provider, model, baseUrl, apiKey, ct);
            string cacheDir = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "cache");
            string cacheFile = Path.Combine(cacheDir, $"enrich-{backend.ProviderId}-{backend.ModelSlug}.ndjson");
            var cache = new EnrichmentCache(cacheFile);
            enricher = new Tier2Enricher(backend.Llm, cache, ops, backend.ProviderId, backend.ModelSlug);
            Console.Error.WriteLine(
                $"[scrinia-bench] tier2 enrichment enabled: provider={backend.ProviderId} model={backend.ModelSlug} ops={ops.Label()} cache={cacheFile}");
        }

        FusionMode fusion = ParseFusion(Program.Flag(args, "--fusion"));
        RerankMode rerank = ParseRerank(Program.Flag(args, "--rerank"));
        double maxSimWeight = ParseDouble(Program.Flag(args, "--maxsim-weight"),
            SentenceMaxSimReranker.DefaultFirstStageWeight);

        var rankerOptions = RankerOptions.Default with
        {
            AlphaRelevance = 1.0,
            AlphaRecency = alphaRecency,
            TauDays = tauDays,
            Fusion = fusion,
        };

        IEmbeddingProvider? embedder = null;
        if (mode == Mode.Bm25Embeddings)
        {
            embedder = await EmbeddingProviderLoader.LoadAsync(embedKind, modelDir, ct);
            if (embedder is null)
            {
                Console.Error.WriteLine(
                    $"warning: --mode=bm25_embeddings requested but {embedKind} provider " +
                    "could not be loaded (missing files, download failed, or device unavailable). " +
                    "Falling back to BM25-only.");
                mode = Mode.Bm25;
            }
        }

        string root = Path.Combine(Path.GetTempPath(),
            $"scrinia-retbench-{benchmarkSlug}-{Guid.NewGuid().ToString("N")[..8]}");
        // Sentence index built at ingest only when a reranker that consumes it is
        // configured AND we have an embedder. Empty index path is cheap and the
        // reranker no-ops on empty lookups, so this gate is a cost guard, not a
        // correctness one.
        bool buildSentenceIndex = rerank == RerankMode.SentenceMaxSim && embedder is not null;
        var retriever = new ScriniaRetriever(
            rootDir: root,
            rankerOptions: rankerOptions,
            embeddingProvider: embedder,
            diversityLambda: diversityLambda,
            diversityInnerLimit: diversityInnerLimit,
            chunked: chunked,
            chunkWindowSize: chunkWindow,
            chunkOverlap: chunkOverlap,
            enricher: enricher,
            buildSentenceIndex: buildSentenceIndex);

        // Two-step wiring: reranker reads the retriever's sentence index via a
        // delegate, so it can only be constructed AFTER the retriever exists.
        // Set as a property to break the chicken-and-egg cycle cleanly.
        if (rerank == RerankMode.SentenceMaxSim && embedder is not null)
        {
            retriever.Reranker = new SentenceMaxSimReranker(
                embedder,
                retriever.GetSentenceVectors,
                firstStageWeight: maxSimWeight);
        }

        return (retriever, mode);
    }

    /// <summary>
    /// Patch <see cref="RunMetadata"/> with Tier 2 enrichment stats from the retriever's
    /// enricher. Subcommands call this after <c>RunAsync</c> returns and before writing
    /// the result JSON, so a Tier 2 run's output records which provider/model/ops were
    /// active and how much cache work it saved. No-op when the retriever has no enricher.
    /// </summary>
    public static BenchmarkRunResult WithEnrichmentMetadata(BenchmarkRunResult result, ScriniaRetriever retriever)
    {
        if (retriever.Enricher is not { } enr) return result;
        return result with
        {
            RunMetadata = result.RunMetadata with
            {
                EnrichmentProvider = enr.ProviderId,
                EnrichmentModel = enr.ModelSlug,
                EnrichmentOps = enr.Ops.Label(),
                EnrichmentCacheHits = enr.Cache.Hits,
                EnrichmentCacheMisses = enr.Cache.Misses,
                EnrichmentInflightHits = enr.Cache.InflightHits,
            },
        };
    }

    private static bool ParseChunk(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" or "none" or "off" or "false" => false,
        "prod" or "on" or "true" => true,
        _ => throw new ArgumentException($"unknown --chunk '{raw}'. Expected: none | prod"),
    };

    private static bool ParseEnrich(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" or "none" or "off" or "false" => false,
        "tier2" or "on" or "true" => true,
        _ => throw new ArgumentException($"unknown --enrich '{raw}'. Expected: none | tier2"),
    };

    private static FusionMode ParseFusion(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" or "sum" => FusionMode.Sum,
        "rrf" => FusionMode.Rrf,
        _ => throw new ArgumentException($"unknown --fusion '{raw}'. Expected: sum | rrf"),
    };

    // Sentence-MaxSim is the bench default — α=0.3 blend strictly beats Sum-only
    // retrieval on R@5 across the three public benchmarks (convomem +6.3, LongMemEval
    // +0.6, LoCoMo within noise). Pass --rerank none to opt out and run the prior
    // first-stage-only baseline.
    private static RerankMode ParseRerank(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" or "sentence-maxsim" or "maxsim" => RerankMode.SentenceMaxSim,
        "none" or "off" => RerankMode.None,
        _ => throw new ArgumentException($"unknown --rerank '{raw}'. Expected: sentence-maxsim | none"),
    };

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
        // Only tag with the embed-provider if the embedder actually ran. A fallback
        // to Bm25 (model not found / download failed / device unavailable) must not
        // inherit the "vulkan" suffix or the result file looks like a Vulkan run
        // when it was BM25-only.
        if (mode == Mode.Bm25Embeddings
            && Program.Flag(args, "--embed-provider") is { } ep
            && !string.Equals(ep, "model2vec", StringComparison.OrdinalIgnoreCase))
            tags.Add(ep.ToLowerInvariant());
        if (Program.Flag(args, "--chunk") is { } ck
            && !string.Equals(ck, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ck, "off", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ck, "false", StringComparison.OrdinalIgnoreCase))
            tags.Add("chunked");
        // tier2 enrichment tag. Default ops (facts+description) get a plain `tier2`;
        // non-default selections get `tier2-{ops}` so result files distinguish ablations.
        if (Program.Flag(args, "--enrich") is { } en
            && !string.Equals(en, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(en, "off", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(en, "false", StringComparison.OrdinalIgnoreCase))
        {
            var enrichOps = EnrichOps.Parse(Program.Flag(args, "--enrich-ops"));
            tags.Add(enrichOps.Equals(EnrichOps.Default)
                ? "tier2"
                : "tier2-" + enrichOps.Label().Replace(',', '-'));
        }
        if (Program.Flag(args, "--alpha-recency") is { } r) tags.Add($"rec{r}");
        if (Program.Flag(args, "--tau-days") is { } tau) tags.Add($"tau{tau}");
        if (Program.Flag(args, "--alpha-importance") is { } imp) tags.Add($"imp{imp}");
        if (Program.Flag(args, "--diversity-lambda") is { } lam) tags.Add($"div{lam}");
        if (Program.Flag(args, "--fusion") is { } fu
            && !string.Equals(fu, "sum", StringComparison.OrdinalIgnoreCase))
            tags.Add(fu.ToLowerInvariant());
        // Sentence-MaxSim is the bench default — no suffix when active. Opt-out via
        // --rerank none gets the "no-rerank" tag so result files are distinguishable.
        if (Program.Flag(args, "--rerank") is { } rr
            && (string.Equals(rr, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rr, "off", StringComparison.OrdinalIgnoreCase)))
        {
            tags.Add("no-rerank");
        }
        // Non-default α suffix applies whenever the reranker is active (which is the
        // default). Result files for different sweeps don't collide.
        if (Program.Flag(args, "--maxsim-weight") is { } mw) tags.Add($"a{mw}");
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
