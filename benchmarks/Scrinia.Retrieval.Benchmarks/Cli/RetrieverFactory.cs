using System.Globalization;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Judge;
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

        // Entity-graph recall probe. Off unless --entity-graph on|true. Parsed up here because
        // BOTH arms consume it: the mechanical arm (below) and the agentic arm (next branch).
        // The graph nodes are the memories' typed attributes; the mechanical arm sources them
        // from the attributes the ingest agent generates (BuildAgentAsync treats --entity-graph
        // as implying --agent), the agentic arm from the attributes its episode writes.
        bool entityGraph = ParseBool(Program.Flag(args, "--entity-graph"));
        double entityGraphAlpha = ParseDouble(Program.Flag(args, "--entity-graph-alpha"), EntityGraphScoreContributor.DefaultAlpha);
        int entityGraphIterations = ParseInt(Program.Flag(args, "--entity-graph-iterations"), EntityGraphScoreContributor.DefaultIterations);
        double entityGraphWeight = ParseDouble(Program.Flag(args, "--entity-graph-weight"), EntityGraphScoreContributor.DefaultWeight);

        // Agentic-ingest arm: the LLM episode IS the ingestion, so this branch is
        // checked EARLY, before ScriniaRetriever's own embedder/reranker setup below.
        // By default (--mode bm25) it scores BM25 + fields, same as the mechanical BM25
        // arm. With --mode bm25_embeddings it also loads the real embedder (below) and
        // retrieves through the production hybrid (VectorStore + HybridReranker +
        // path-segment) — the same components production uses. --agentic-ingest is a
        // bare boolean flag (no value token), so presence is checked positionally via
        // HasFlag rather than the `--flag value` pair idiom Program.Flag uses.
        if (HasFlag(args, "--agentic-ingest"))
        {
            // Mutually exclusive with the mechanical agent harness — this arm's
            // ingestion IS the agent episode.
            if (HasFlag(args, "--agent"))
                throw new ArgumentException("--agentic-ingest cannot be combined with --agent; the episode is the agent.");

            // The cross-encoder / sentence-maxsim rerankers operate on ScriniaRetriever's
            // sentence index, which the agentic arm does not build. The agentic arm's
            // semantic rerank is now the production hybrid, enabled via --mode bm25_embeddings.
            if (Program.Flag(args, "--rerank") is { } rerankFlag && ParseRerank(rerankFlag) != RerankMode.None)
                throw new ArgumentException(
                    "--agentic-ingest does not use the --rerank rerankers (cross-encoder / sentence-maxsim); its semantic rerank is the production hybrid, enabled with --mode bm25_embeddings. Drop --rerank or pass --rerank none.");

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
            int maxTokens = EpisodeMaxTokens(args);
            var harness = new AgenticIngestHarness(toolClient, maxTurns, maxTokens);
            string epCache = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "cache",
                $"episodes-{backend.ProviderId}-{backend.ModelSlug}.ndjson");
            string agRoot = Path.Combine(Path.GetTempPath(), $"scrinia-retbench-{benchmarkSlug}-ag-{Guid.NewGuid().ToString("N")[..8]}");

            // Load the embedder for the agentic arm's query-time semantic rerank, mirroring
            // the mechanical arm below. Falls back to BM25-only if the embedder can't load.
            Mode agenticMode = mode;
            Scrinia.Core.Embeddings.IEmbeddingProvider? agenticEmbedder = null;
            if (agenticMode == Mode.Bm25Embeddings)
            {
                agenticEmbedder = await EmbeddingProviderLoader.LoadAsync(embedKind, modelDir, ct);
                if (agenticEmbedder is null)
                {
                    Console.Error.WriteLine(
                        "warning: --agentic-ingest --mode bm25_embeddings requested but the embedder could not be loaded " +
                        "(missing model files or unavailable device); falling back to BM25-only for the agentic arm.");
                    agenticMode = Mode.Bm25;
                }
            }

            var agentic = new AgenticScriniaRetriever(agRoot, harness, new EpisodeCache(epCache), backend.ModelSlug,
                RankerOptions.Default with { AlphaRelevance = 1.0, AlphaRecency = 0.0 },
                embedder: agenticEmbedder,
                embeddingOptions: agenticEmbedder is null ? null : new EmbeddingOptions(),
                entityGraphEnabled: entityGraph,
                entityGraphAlpha: entityGraphAlpha,
                entityGraphIterations: entityGraphIterations,
                entityGraphWeight: entityGraphWeight);
            string embeddingsStatus = agenticEmbedder is null
                ? "off"
                : $"on(dims={agenticEmbedder.Dimensions},weight={new EmbeddingOptions().SemanticWeight})";
            Console.Error.WriteLine($"[scrinia-bench] agentic-ingest enabled: provider={backend.ProviderId} model={backend.ModelSlug} maxTurns={maxTurns} maxTokens={maxTokens} cache={epCache} embeddings={embeddingsStatus}");
            return (agentic, agenticMode);
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
    /// <see cref="NoOpAgentHarness.Instance"/> when the bare <c>--agent</c> flag is absent.
    /// When present, requires <c>--agent-provider</c> and dispatches to
    /// <see cref="AgentClientBuilder.BuildAsync"/> to construct the LLM transport. The
    /// harness owns its own <see cref="LlmResponseCache"/> at
    /// <c>cache/agent-{provider}-{model}.ndjson</c>.
    /// </summary>
    public static async Task<IAgentHarness> BuildAgentAsync(string[] args, CancellationToken ct)
    {
        // `--agent` is a bare on/off flag. Its single op is ingest-time curation — the agent
        // generates the attributes map + keywords a production `remember` call would carry.
        // The mechanical entity-graph probe sources its nodes from those generated attributes,
        // so --entity-graph (on the mechanical arm) implies --agent; without a curating agent
        // there'd be no attributes for the graph to walk. Scoped to the mechanical arm: the
        // agentic arm's episode writes its own attributes, so it never needs this harness.
        bool agentOn = HasFlag(args, "--agent")
            || (ParseBool(Program.Flag(args, "--entity-graph")) && !HasFlag(args, "--agentic-ingest"));
        if (!agentOn) return NoOpAgentHarness.Instance;

        string provider = Program.Flag(args, "--agent-provider")
            ?? throw new ArgumentException(
                "--agent requires --agent-provider " +
                "(openai | anthropic | gemini | ollama | claude-cli | codex-cli | copilot-cli)");
        string? model = Program.Flag(args, "--agent-model");
        string? baseUrl = Program.Flag(args, "--agent-base-url");
        string? apiKey = Program.Flag(args, "--agent-api-key");
        int maxAttempts = ParseInt(Program.Flag(args, "--agent-max-attempts"), LlmAgentHarness.DefaultMaxAttempts);
        int ingestMaxTokens = ParseInt(Program.Flag(args, "--agent-max-tokens"), LlmAgentHarness.DefaultIngestMaxTokens);
        var backend = await AgentClientBuilder.BuildAsync(provider, model, baseUrl, apiKey, ct);
        string cacheDir = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "cache");
        string cacheFile = Path.Combine(cacheDir, $"agent-{backend.ProviderId}-{backend.ModelSlug}.ndjson");
        var cache = new LlmResponseCache(cacheFile);
        var failures = new FailureSink(FailureSink.PathForCache(cacheFile));
        var harness = new LlmAgentHarness(backend.Llm, cache, backend.ProviderId, backend.ModelSlug, maxAttempts, ingestMaxTokens, failures);
        Console.Error.WriteLine(
            $"[scrinia-bench] agent harness enabled: provider={backend.ProviderId} model={backend.ModelSlug} op=ingest maxAttempts={maxAttempts} maxTokens={ingestMaxTokens} cache={cacheFile}");
        return harness;
    }

    /// <summary>Bare boolean-presence check for flags with no value token (e.g. <c>--agentic-ingest</c>).
    /// Unlike <see cref="Program.Flag"/> (which expects <c>--flag value</c> pairs and never matches a
    /// flag in the final array slot), this matches the flag anywhere in <paramref name="args"/>
    /// regardless of position.</summary>
    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    /// <summary>Max tool-call turns per agentic-ingest session episode. Internal so it's
    /// unit-testable.</summary>
    internal static int EpisodeMaxTurns(string[] args) => ParseInt(Program.Flag(args, "--episode-max-turns"), 16);

    /// <summary>The <c>max_tokens</c> sent on every agentic-ingest turn. On an
    /// OpenAI-compatible server (llama.cpp, vLLM, …) this bounds the ENTIRE request — the
    /// accumulating prompt (system guide + full session transcript + every prior turn's
    /// tool calls and tool results) plus the tokens generated this turn — not just the
    /// completion. The transcript grows each turn as recalled memories and search hits are
    /// appended, so an undersized cap starves generation after the first turn or two. The
    /// base prompt alone (~2.3k-token guide + tool schemas + up to a ~1.4k-token session)
    /// already approaches 5k tokens, so the old 4096 default could not even fit turn one.
    /// Default 32768; set it to your server's context window (n_ctx) and no higher.</summary>
    internal static int EpisodeMaxTokens(string[] args) => ParseInt(Program.Flag(args, "--episode-max-tokens"), 32768);

    /// <summary>Prints the agentic-ingest run's whole-run stats block to stdout. Shared by
    /// <c>LoCoMoCommand</c> and <c>LongMemEvalCommand</c> so the format stays consistent.</summary>
    public static void PrintAgenticStats(AgenticIngestStats s)
    {
        Console.WriteLine();
        Console.WriteLine("=== agentic-ingest stats ===");
        Console.WriteLine($"episodes:              {s.Episodes}");
        Console.WriteLine($"  agentic (LLM/cache):  {s.AgenticEpisodes}");
        Console.WriteLine($"  failed (unstored):    {s.FailedEpisodes}");
        Console.WriteLine($"memories written:      {s.MemoriesWritten}");
        Console.WriteLine($"memories without refs: {s.MemoriesWithoutRefs}");
        Console.WriteLine($"unresolvable refs:     {s.UnresolvableRefs}");
        Console.WriteLine($"tool-call errors:      {s.ToolCallErrors}");
    }

    /// <summary>Prints the mechanical <c>--agent</c> ingest tally so a run can reconcile
    /// docs-seen against attributes/keywords produced and confirm nothing was silently dropped
    /// on the (default-silent) empty-reply path. No-op unless the agent harness actually ran.</summary>
    public static void PrintAgentStats(AgentIngestStats s)
    {
        Console.WriteLine();
        Console.WriteLine("=== agent ingest stats ===");
        Console.WriteLine($"docs seen:        {s.Seen}");
        Console.WriteLine($"  empty content:  {s.EmptyContent}  (skipped, no call)");
        Console.WriteLine($"  cache hits:     {s.CacheHits}");
        Console.WriteLine($"  llm docs:       {s.LlmDocs}  (attempts={s.LlmAttempts})");
        Console.WriteLine($"produced:         {s.Produced}");
        Console.WriteLine($"empty/failed:     {s.Failed}  (reached model, no usable attributes/keywords; uncached)");
        long reconcile = s.EmptyContent + s.CacheHits + s.LlmDocs;
        string ok = reconcile == s.Seen && s.Produced == s.CacheHits + (s.LlmDocs - s.Failed) ? "ok" : "MISMATCH";
        Console.WriteLine($"reconcile:        seen={s.Seen} == empty+cache+llm={reconcile}; cache lines added this run={s.LlmDocs - s.Failed} [{ok}]");
        if (s.Failed > 0 && s.FailuresPath is not null)
            Console.WriteLine($"failures logged:  {s.Failed} → {s.FailuresPath}  (full prompt + raw per attempt)");
    }

    internal static int JudgeK(string[] args) => ParseInt(Program.Flag(args, "--judge-k"), 10);

    /// <summary>The <c>max_tokens</c> for the answerer call. Like the ingest turns, this
    /// bounds the whole request on an OpenAI-compatible server: the query plus the
    /// top-<c>--judge-k</c> surfaced memory contents, plus the answer itself. Ten full
    /// memories can run to several thousand tokens, so the old 512 default truncated the
    /// prompt before the model could answer. Default 8192.</summary>
    internal static int JudgeAnswerMaxTokens(string[] args) => ParseInt(Program.Flag(args, "--judge-answer-max-tokens"), 8192);

    /// <summary>The <c>max_tokens</c> for the grader call. Bounds the whole request: the
    /// query + gold answer + candidate answer prompt, plus the verdict token. The old 16
    /// left no room for the prompt at all on a server that counts prompt+completion
    /// together, so every grade failed. Default 2048.</summary>
    internal static int JudgeGradeMaxTokens(string[] args) => ParseInt(Program.Flag(args, "--judge-grade-max-tokens"), 2048);

    /// <summary>Builds the answerability judge when --judge is set, else null. One model
    /// (via --judge-provider/--judge-model/--judge-base-url/--judge-api-key) drives both the
    /// answerer and grader; warns if it equals --agent-model.</summary>
    public static async Task<AnswerabilityJudge?> BuildJudgeAsync(string[] args, CancellationToken ct)
    {
        if (!HasFlag(args, "--judge")) return null;

        string provider = Program.Flag(args, "--judge-provider")
            ?? throw new ArgumentException("--judge requires --judge-provider (openai | anthropic | gemini | ollama | claude-cli | ...).");
        string? model = Program.Flag(args, "--judge-model");
        string? baseUrl = Program.Flag(args, "--judge-base-url");
        string? apiKey = Program.Flag(args, "--judge-api-key");

        if (model is not null && string.Equals(model, Program.Flag(args, "--agent-model"), StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine("[scrinia-bench] WARNING: --judge-model equals --agent-model; the model that authored the store is also judging it (self-preference risk).");

        var backend = await AgentClientBuilder.BuildAsync(provider, model, baseUrl, apiKey, ct);
        string cacheFile = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "cache",
            $"judge-{backend.ProviderId}-{backend.ModelSlug}.ndjson");
        int answerMax = JudgeAnswerMaxTokens(args);
        int gradeMax = JudgeGradeMaxTokens(args);
        var failures = new FailureSink(FailureSink.PathForCache(cacheFile));
        var judge = new AnswerabilityJudge(backend.Llm, new LlmResponseCache(cacheFile), backend.ProviderId, backend.ModelSlug, JudgeK(args), answerMax, gradeMax, failures);
        Console.Error.WriteLine($"[scrinia-bench] judge enabled: provider={backend.ProviderId} model={backend.ModelSlug} judgeK={JudgeK(args)} answerMaxTokens={answerMax} gradeMaxTokens={gradeMax} cache={cacheFile}");
        return judge;
    }

    /// <summary>Prints the judge answerability summary block. Shared by <c>LoCoMoCommand</c>
    /// and <c>LongMemEvalCommand</c> so the format stays consistent.</summary>
    public static void PrintJudgeSummary(AnswerabilityJudge judge, BenchmarkRunResult result)
    {
        Console.WriteLine();
        Console.WriteLine("=== judge answerability ===");
        Console.WriteLine($"correct@{judge.JudgeK}:  {result.Aggregate.CorrectRate:P1}");
        Console.WriteLine($"surfaced@{judge.JudgeK}:  {result.Aggregate.SurfacedRate:P1}");
        Console.WriteLine($"  judged:   {judge.JudgedCount}");
        Console.WriteLine($"  no gold:  {judge.NoGoldCount}");
        Console.WriteLine($"  failed:   {judge.FailedCount}");
        if (judge.FailedCount > 0 && judge.FailuresPath is not null)
            Console.WriteLine($"  failures logged: {judge.FailedCount} → {judge.FailuresPath}  (full prompt + raw per attempt)");
    }

    private static bool ParseChunk(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" or "none" or "off" or "false" => false,
        "prod" or "on" or "true" => true,
        _ => throw new ArgumentException($"unknown --chunk '{raw}'. Expected: none | prod"),
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
        // Agent harness tag — bare `agent` when the ingest-curation harness is active.
        // Off (the default) emits no tag so baseline result files stay clean.
        if (HasFlag(args, "--agent")) tags.Add("agent");
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
