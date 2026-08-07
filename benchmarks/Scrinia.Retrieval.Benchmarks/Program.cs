using System.Globalization;
using System.Text.Json;
using Scrinia.Retrieval.Benchmarks.Cli;

namespace Scrinia.Retrieval.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        string subcommand = args[0];
        string[] rest = args[1..];
        try
        {
            return subcommand switch
            {
                "convomem" => await ConvoMemCommand.RunAsync(rest, CancellationToken.None),
                "longmemeval" => await LongMemEvalCommand.RunAsync(rest, CancellationToken.None),
                "membench" => await MemBenchCommand.RunAsync(rest, CancellationToken.None),
                "locomo" => await LoCoMoCommand.RunAsync(rest, CancellationToken.None),
                "searchcost" => await SearchCostCommand.RunAsync(rest, CancellationToken.None),
                "judge-replay" => await JudgeReplayCommand.RunAsync(rest, CancellationToken.None),
                "rank" => await RankCommand.RunAsync(rest, CancellationToken.None),
                _ => UnknownCommand(subcommand),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int UnknownCommand(string sub)
    {
        Console.Error.WriteLine($"unknown subcommand: {sub}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            scrinia-bench — Scrinia retrieval-quality benchmark harness

            Usage:
              scrinia-bench convomem    --data <dir> [common-flags]
              scrinia-bench longmemeval --data <file> [--split full|dev|held_out] [--granularity session|turn] [common-flags]
              scrinia-bench membench    --data <dir> [--limit <N>] [common-flags]
              scrinia-bench locomo      --data <file> [--granularity session|dialog] [common-flags]
              scrinia-bench searchcost  --data <file> [--conversations <N>] [--queries <N>] [--warmup <N>]
              scrinia-bench judge-replay --input <result.json> --judge --judge-provider <p> [judge flags]
              scrinia-bench rank         [--dir <results-dir>]

            Subcommands:
              convomem      Run ConvoMem (Salesforce) evidence questions
              longmemeval   Run LongMemEval (xiaowu0162/longmemeval-cleaned), the headline benchmark
              membench      Run MemBench (ACL 2025) — 8500 items, 10 categories
              locomo        Run LoCoMo (snap-research/locomo) — 10 convs, 1986 QA pairs
              searchcost    Measure FileMemoryStore.SearchAll cost with a pre-materialized
                            SearchQuery.Candidates list vs Candidates = null. Store-level
                            micro-benchmark against a LoCoMo-seeded corpus — no retriever, no judge.
              judge-replay  Re-run ONLY the LLM judge over a prior run's persisted JudgeContext —
                            no dataset, no retriever, exact live/replay parity. Writes <input>.judged.json.
              rank          Rank scrinia_locomo*.json / *.judged.json result files under --dir by the
                            judge CorrectRate/SurfacedRate metric (legacy Answerable-only files fall
                            back automatically). Reads only each file's RunMetadata+Aggregate prefix,
                            never the (25-180 MB) Questions array.

            Common flags:
              --data <path>            Dataset path (download yourself; see benchmarks README)
              --mode <m>               bm25 | bm25_embeddings (default: bm25_embeddings; falls back to bm25 if model not found)
              --embed-provider <p>     model2vec (static, default) | vulkan (GGUF/sentence-transformer)
              --model-dir <dir>        Root holding m2v-MiniLM-L6-v2/ (model2vec) or all-MiniLM-L6-v2-Q8_0.gguf (vulkan). Auto-downloaded if missing. Default: ./models
              --top-k <K>              Retrieve top-K hits per query (default: 50)
              --output <file>          Write JSON result to this path (default: results/<bench>_<mode>_<UTC>.json)
              --chunk <c>              none (default; whole-doc) | prod (slice via TextChunker matching prod's 1200/200 defaults). Chunks dedup to one hit per parent at retrieval.
              --chunk-window <N>       Override chunk window size (default 1200; only honored when --chunk prod)
              --chunk-overlap <N>      Override chunk overlap (default 200; only honored when --chunk prod)
              --category <name>        Run only questions in this category. Enables per-category process fanout (see run-benchmarks.ps1 -Parallel).

            Agent harness flags (simulates a production agent curating memories at ingest — the same
            attributes map + keywords a real agent hands the `remember` MCP tool, generated in one LLM call per doc):
              --agent                  Bare on/off flag. Present = the agent generates an attributes map
                                       and keywords per doc at ingest (merged with mechanically-extracted keywords);
                                       absent = purely mechanical ingestion. There is no query-side transform.
              --agent-provider <p>     REQUIRED when --agent is present: openai | anthropic | gemini | ollama | claude-cli | codex-cli | copilot-cli
              --agent-base-url <url>   Optional HTTP endpoint override for ollama/openai.
              --agent-model <m>        Required for ollama/openai/anthropic/gemini; optional for agent CLIs.
              --agent-api-key <key>    Bearer / API key for openai/anthropic/gemini. Ollama doesn't need one.
              --agent-max-attempts <n> Tries per doc when the model returns an empty/unparseable reply (default: 1 = retries OFF). Set >1 to enable retries; temperature > 0 so each re-roll differs. A doc that never yields attributes/keywords is left uncached, so a rerun retries it.
              --agent-max-tokens <n>   max_tokens for the ingest call (default: 32768). On an OpenAI-compatible server this bounds the WHOLE request (prompt + reply); the ingest prompt is doc-sized (like an agentic-ingest turn), so the default matches --episode-max-tokens. Set it to your server's context window (n_ctx).
              --ingest-concurrency <N> Max concurrent IngestAsync calls per question's haystack (default: 1 = sequential). Set to 4-16 when --agent is active to keep wall-time tractable; subscription CLI rate limits and your local CPU cap the useful upper bound.

            Agentic-ingest arm (locomo, longmemeval): the LLM IS the ingestion — a real tool-calling
            episode per session decides what to remember, instead of Scrinia's mechanical per-doc ingest.
            Plain --agentic-ingest (or --mode bm25) scores BM25 + fields only. With
            --mode bm25_embeddings it runs the full production ingestion+retrieval pipeline: the
            agent-written memories are embedded and hybrid-reranked (VectorStore + HybridReranker +
            path-segment), same as production.
              --agentic-ingest         Enable the agentic-ingest arm. Requires --agent-provider (openai-compatible only). Mutually exclusive with --agent.
              --episode-max-turns <n> Max tool-call turns per session episode (default 16)
              --episode-max-tokens <n> max_tokens per turn. On an OpenAI-compatible server this bounds the WHOLE request (prompt + generation), and the prompt (guide + full session transcript + tool schemas, growing each turn with recalls/search hits) starts near 5k tokens. Default 32768; set it to your server's context window (n_ctx).

            Judge answerability flags (longmemeval, locomo):
              --judge                 Enable the LLM-judge answerability metric (needs --judge-provider). Works with or without --agentic-ingest.
              --judge-provider <p>    Judge model provider (openai | anthropic | gemini | ollama | claude-cli | ...). Held constant across arms.
              --judge-model <m>       Judge model (both answerer and grader).
              --judge-base-url <u>    OpenAI-compatible endpoint for the judge.
              --judge-api-key <k>     API key for the judge provider.
              --judge-k <n>           Retrieved items the answerer sees (default 10).
              --judge-concurrency <N> Max concurrent judge calls (answerer+grader) per conversation (locomo only; default 1 = sequential). The judge is network-bound and the run's bottleneck, so 8-32 slashes wall-time; the LLM-server rate limit and its context/batch capacity cap the useful upper bound. Retrieval stays sequential; only the judge parallelizes. Output order is unchanged.
              --judge-answer-max-tokens <n>  Answerer max_tokens — bounds the whole request (query + top-k surfaced memories + answer). Default 8192.
              --judge-grade-max-tokens <n>   Grader max_tokens — bounds the whole request (query + gold + candidate answer + verdict). Default 2048.

            ConvoMem flags:
              --limit <N>        Cap questions per category file (default: 50)

            LongMemEval flags:
              --split <s>        full | dev | held_out (default: full)
              --split-file <p>   Path to lme_split_50_450.json (default: alongside --data)
              --granularity <g>  session | turn (default: session)

            Ablation flags (all default to "off" so headline runs are apples-to-apples):
              --alpha-recency <r>            Weight for exp-decay recency term (default 0.0)
              --tau-days <τ>                 Recency time-constant in days (default 14)
              --diversity-lambda <λ>         MMR diversity tradeoff in [0,1] (default 1.0 = no MMR)
              --diversity-inner-limit <N>    MMR candidate pool size (default 50 if --diversity-lambda set)
            """);
    }

    /// <summary>
    /// Tiny argparse — looks up `--flag value` pairs and returns the value or null.
    /// Boolean flags are not supported (every flag must take a value).
    /// </summary>
    public static string? Flag(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    public static int FlagInt(string[] args, string name, int @default)
    {
        string? raw = Flag(args, name);
        return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInvariant, out int parsed) ? parsed : @default;
    }

    public static readonly CultureInfo CultureInvariant = CultureInfo.InvariantCulture;

    public static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
