using System.Globalization;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Builds a configured <see cref="ScriniaRetriever"/> from CLI args. Shared across
/// subcommands so the <c>--mode</c> / <c>--model-dir</c> contract is consistent.
///
/// <para><b>Headline runs (no ablation flags):</b> recency / MMR all neutralized so
/// the apples-to-apples baseline against MemPalace's "raw" track is reproducible.
/// The workspace-tuned defaults (τ=14d, MMR=0.6) would distort months-scale temporal
/// benchmarks like LongMemEval and LoCoMo.</para>
///
/// <para><b>Ablation runs:</b> <c>--alpha-recency</c>, <c>--tau-days</c>,
/// <c>--diversity-lambda</c>, and <c>--diversity-inner-limit</c> turn the ranker
/// knobs back on so sweeps can isolate the contribution of each Scrinia feature.</para>
/// </summary>
internal static class RetrieverFactory
{
    public enum Mode { Bm25, Bm25Embeddings }

    /// <summary>
    /// Second-stage reranker selection. <see cref="None"/> means first-stage retrieval
    /// only; <see cref="SentenceMaxSim"/> applies sentence-level MaxSim reranking on
    /// top-K candidates using the existing embedding model (the bench default);
    /// <see cref="CrossEncoder"/> runs an ONNX BERT cross-encoder over the top-N
    /// candidates (bench experiment — see docs/research/cross-encoding-reranking.md).
    /// </summary>
    public enum RerankMode { None, SentenceMaxSim, CrossEncoder }

    public static async Task<(IRetriever Retriever, Mode Mode)> BuildAsync(
        string[] args, string benchmarkSlug, CancellationToken ct)
    {
        Mode mode = ParseMode(Program.Flag(args, "--mode") ?? "bm25_embeddings");
        string? modelDir = Program.Flag(args, "--model-dir");
        EmbedProviderKind embedKind = EmbeddingProviderLoader.ParseKind(
            Program.Flag(args, "--embed-provider"));

        // Agentic-ingest arm: the LLM episode IS the ingestion, so this branch is
        // checked EARLY — before the embedder loads — and returns immediately. No
        // embedder, no ScriniaRetriever, no reranker; this arm scores BM25 + fields
        // against the mechanical BM25 arm. --agentic-ingest is a bare boolean flag
        // (no value token), so presence is checked positionally via HasFlag rather
        // than the `--flag value` pair idiom Program.Flag uses.
        if (HasFlag(args, "--agentic-ingest"))
        {
            // Mutually exclusive with query/ingest agent decorations — this arm's
            // ingestion IS the agent episode.
            if (ParseAgentMode(Program.Flag(args, "--agent")).AnyEnabled)
                throw new ArgumentException("--agentic-ingest cannot be combined with --agent <ops>; the episode is the agent.");

            string provider = Program.Flag(args, "--agent-provider")
                ?? throw new ArgumentException("--agentic-ingest requires --agent-provider (openai | anthropic | ...).");
            string? model = Program.Flag(args, "--agent-model");
            string? baseUrl = Program.Flag(args, "--agent-base-url");
            string? apiKey = Program.Flag(args, "--agent-api-key");
            var backend = await AgentClientBuilder.BuildAsync(provider, model, baseUrl, apiKey, ct);

            // The tool-calling client is the OpenAI-compatible client; other providers are v1-unsupported.
            if (backend.Llm is not IToolCallingClient toolClient)
                throw new ArgumentException($"--agentic-ingest requires an OpenAI-compatible provider; '{backend.ProviderId}' does not support tool-calling in this bench.");

            int maxTurns = EpisodeMaxTurns(args);
            var harness = new AgenticIngestHarness(toolClient, maxTurns);
            string epCache = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "cache",
                $"episodes-{backend.ProviderId}-{backend.ModelSlug}.ndjson");
            string agRoot = Path.Combine(Path.GetTempPath(), $"scrinia-retbench-{benchmarkSlug}-ag-{Guid.NewGuid().ToString("N")[..8]}");
            var agentic = new AgenticScriniaRetriever(agRoot, harness, new EpisodeCache(epCache), backend.ModelSlug,
                RankerOptions.Default with { AlphaRelevance = 1.0, AlphaRecency = 0.0 });
            Console.Error.WriteLine($"[scrinia-bench] agentic-ingest enabled: provider={backend.ProviderId} model={backend.ModelSlug} maxTurns={maxTurns} cache={epCache}");
            return (agentic, Mode.Bm25);
        }

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

        FusionMode fusion = ParseFusion(Program.Flag(args, "--fusion"));
        RerankMode rerank = ParseRerank(Program.Flag(args, "--rerank"));
        double maxSimWeight = ParseDouble(Program.Flag(args, "--maxsim-weight"),
            SentenceMaxSimReranker.DefaultFirstStageWeight);

        // Entity-graph recall probe. Off unless --entity-graph on|true. The entity source
        // is the agent's `entity:` typed keywords, so BuildAgentAsync force-enables typed
        // extraction whenever this is set (see below).
        bool entityGraph = ParseBool(Program.Flag(args, "--entity-graph"));
        double entityGraphAlpha = ParseDouble(Program.Flag(args, "--entity-graph-alpha"), 0.15);
        int entityGraphIterations = ParseInt(Program.Flag(args, "--entity-graph-iterations"), 20);
        double entityGraphWeight = ParseDouble(Program.Flag(args, "--entity-graph-weight"), 50.0);

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
        // configured AND we have an embedder. The cross-encoder reads raw doc text at
        // rerank time, not sentence vectors, so it never needs this index. Empty index
        // path is cheap and the reranker no-ops on empty lookups, so this gate is a
        // cost guard, not a correctness one.
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
            buildSentenceIndex: buildSentenceIndex,
            entityGraphEnabled: entityGraph,
            entityGraphAlpha: entityGraphAlpha,
            entityGraphIterations: entityGraphIterations,
            entityGraphWeight: entityGraphWeight);

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
        else if (rerank == RerankMode.CrossEncoder)
        {
            int depth = ParseInt(Program.Flag(args, "--rerank-depth"), CrossEncoderReranker.DefaultDepth);
            int maxSeqLen = ParseInt(Program.Flag(args, "--rerank-max-seq"), CrossEncoderReranker.DefaultMaxSeqLen);
            int threads = ParseInt(Program.Flag(args, "--rerank-threads"), 4);
            string modelUrl = Program.Flag(args, "--rerank-model-url") ?? CrossEncoderModelManager.DefaultModelUrl;
            string vocabUrl = Program.Flag(args, "--rerank-vocab-url") ?? CrossEncoderModelManager.DefaultVocabUrl;
            string? rerankModelDir = Program.Flag(args, "--rerank-model-dir");
            try
            {
                var (modelPath, vocabPath) = await CrossEncoderModelManager.EnsureAsync(rerankModelDir, modelUrl, vocabUrl, ct);
                retriever.Reranker = CrossEncoderReranker.Load(modelPath, vocabPath, depth, maxSeqLen, threads);
                Console.Error.WriteLine(
                    $"[scrinia-bench] cross-encoder reranker enabled: model={modelPath} depth={depth} maxSeq={maxSeqLen} threads={threads}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"reranker: cross-encoder load failed ({ex.Message}); falling back to no reranking.");
                Console.Error.WriteLine("       fix: pass --rerank-model-url / --rerank-vocab-url, or drop model.onnx + vocab.txt into --rerank-model-dir.");
            }
        }

        return (retriever, mode);
    }

    /// <summary>
    /// Builds an <see cref="IAgentHarness"/> from CLI args. Returns
    /// <see cref="NoOpAgentHarness.Instance"/> for the default <c>--agent off</c>.
    /// When any agent op is enabled, requires <c>--agent-provider</c> and dispatches
    /// to <see cref="AgentClientBuilder.BuildAsync"/> to construct the LLM transport.
    /// The harness owns its own <see cref="LlmResponseCache"/> at
    /// <c>cache/agent-{provider}-{model}.ndjson</c>.
    /// </summary>
    public static async Task<IAgentHarness> BuildAgentAsync(string[] args, CancellationToken ct)
    {
        AgentModes modes = ParseAgentMode(Program.Flag(args, "--agent"));
        // The entity-graph probe sources its entities from the `entity:` typed keywords, so
        // force typed extraction on whenever --entity-graph is set — even if the user didn't
        // list `typed` in --agent. Without this the graph would be empty.
        if (ParseBool(Program.Flag(args, "--entity-graph")))
            modes = modes with { TypedKeywords = true };
        if (!modes.AnyEnabled) return NoOpAgentHarness.Instance;

        string provider = Program.Flag(args, "--agent-provider")
            ?? throw new ArgumentException(
                "--agent " + modes.Label() + " requires --agent-provider " +
                "(openai | anthropic | gemini | ollama | claude-cli | codex-cli | copilot-cli)");
        string? model = Program.Flag(args, "--agent-model");
        string? baseUrl = Program.Flag(args, "--agent-base-url");
        string? apiKey = Program.Flag(args, "--agent-api-key");
        var backend = await AgentClientBuilder.BuildAsync(provider, model, baseUrl, apiKey, ct);
        string cacheDir = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "cache");
        string cacheFile = Path.Combine(cacheDir, $"agent-{backend.ProviderId}-{backend.ModelSlug}.ndjson");
        var cache = new LlmResponseCache(cacheFile);
        var harness = new LlmAgentHarness(backend.Llm, cache, backend.ProviderId, backend.ModelSlug, modes);
        Console.Error.WriteLine(
            $"[scrinia-bench] agent harness enabled: provider={backend.ProviderId} model={backend.ModelSlug} ops={modes.Label()} cache={cacheFile}");
        return harness;
    }

    /// <summary>Bare boolean-presence check for flags with no value token (e.g. <c>--agentic-ingest</c>).
    /// Unlike <see cref="Program.Flag"/> (which expects <c>--flag value</c> pairs and never matches a
    /// flag in the final array slot), this matches the flag anywhere in <paramref name="args"/>
    /// regardless of position.</summary>
    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    /// <summary>Max tool-call turns per agentic-ingest session episode. Mirrors the
    /// <see cref="ParseAgentMode"/> public/internal helper style so it's unit-testable.</summary>
    internal static int EpisodeMaxTurns(string[] args) => ParseInt(Program.Flag(args, "--episode-max-turns"), 16);

    /// <summary>Prints the agentic-ingest run's whole-run stats block to stdout. Shared by
    /// <c>LoCoMoCommand</c> and <c>LongMemEvalCommand</c> so the format stays consistent.</summary>
    public static void PrintAgenticStats(AgenticIngestStats s)
    {
        Console.WriteLine();
        Console.WriteLine("=== agentic-ingest stats ===");
        Console.WriteLine($"episodes:              {s.Episodes}");
        Console.WriteLine($"  agentic (LLM/cache):  {s.AgenticEpisodes}");
        Console.WriteLine($"  fallback (mechanical):{s.FallbackEpisodes}");
        Console.WriteLine($"memories written:      {s.MemoriesWritten}");
        Console.WriteLine($"memories without refs: {s.MemoriesWithoutRefs}");
        Console.WriteLine($"unresolvable refs:     {s.UnresolvableRefs}");
        Console.WriteLine($"tool-call errors:      {s.ToolCallErrors}");
    }

    private static bool ParseChunk(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" or "none" or "off" or "false" => false,
        "prod" or "on" or "true" => true,
        _ => throw new ArgumentException($"unknown --chunk '{raw}'. Expected: none | prod"),
    };

    /// <summary>Parses <c>--agent</c> into an <see cref="AgentModes"/>. Delegates to
    /// <see cref="AgentModes.Parse"/> so the parse logic lives with the type and stays unit-testable.</summary>
    public static AgentModes ParseAgentMode(string? raw) => AgentModes.Parse(raw);

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
        "cross-encoder" or "cross" or "ce" => RerankMode.CrossEncoder,
        _ => throw new ArgumentException($"unknown --rerank '{raw}'. Expected: sentence-maxsim | cross-encoder | none"),
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
        // Agent harness tag — one of agent-anchors / agent-rewrite / agent-hyde /
        // agent-all when the harness is active. Off (the default) emits no tag so
        // baseline result files stay clean.
        if (Program.Flag(args, "--agent") is { } ag)
        {
            var modes = ParseAgentMode(ag);
            if (modes.AnyEnabled) tags.Add("agent-" + modes.Label());
        }
        // Agentic-ingest tag so its result files never collide with the mechanical arms.
        if (HasFlag(args, "--agentic-ingest")) tags.Add("agentic-ingest");
        if (Program.Flag(args, "--alpha-recency") is { } r) tags.Add($"rec{r}");
        if (Program.Flag(args, "--tau-days") is { } tau) tags.Add($"tau{tau}");
        if (Program.Flag(args, "--diversity-lambda") is { } lam) tags.Add($"div{lam}");
        if (Program.Flag(args, "--fusion") is { } fu
            && !string.Equals(fu, "sum", StringComparison.OrdinalIgnoreCase))
            tags.Add(fu.ToLowerInvariant());
        // Sentence-MaxSim is the bench default — no suffix when active. Opt-out via
        // --rerank none gets the "no-rerank" tag; --rerank cross-encoder gets a "ce"
        // tag so result files for the reranker A/B don't collide.
        if (Program.Flag(args, "--rerank") is { } rr)
        {
            if (string.Equals(rr, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rr, "off", StringComparison.OrdinalIgnoreCase))
                tags.Add("no-rerank");
            else if (string.Equals(rr, "cross-encoder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rr, "cross", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rr, "ce", StringComparison.OrdinalIgnoreCase))
                tags.Add("ce");
        }
        // Non-default α suffix applies whenever the reranker is active (which is the
        // default). Result files for different sweeps don't collide.
        if (Program.Flag(args, "--maxsim-weight") is { } mw) tags.Add($"a{mw}");
        // Entity-graph probe tag, with optional alpha/iterations suffixes so sweep cells
        // don't collide (e.g. entgraph, entgrapha0.25, entgraphi40).
        if (Program.Flag(args, "--entity-graph") is { } eg && ParseBool(eg))
        {
            string egLabel = "entgraph";
            if (Program.Flag(args, "--entity-graph-alpha") is { } ega) egLabel += $"a{ega}";
            if (Program.Flag(args, "--entity-graph-iterations") is { } egi) egLabel += $"i{egi}";
            tags.Add(egLabel);
        }
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

    private static bool ParseBool(string? raw) => raw?.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        _ => false,
    };
}
