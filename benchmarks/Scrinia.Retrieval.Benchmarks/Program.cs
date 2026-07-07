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

            Subcommands:
              convomem      Run ConvoMem (Salesforce) evidence questions
              longmemeval   Run LongMemEval (xiaowu0162/longmemeval-cleaned), the headline benchmark
              membench      Run MemBench (ACL 2025) — 8500 items, 10 categories
              locomo        Run LoCoMo (snap-research/locomo) — 10 convs, 1986 QA pairs

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

            Agent harness flags (simulates a production agent around scrinia — anchors at ingest, query rewrite/HyDE at search):
              --agent <mode>           off (default) | anchors | rewrite | hyde | all — what the agent does.
                                       'anchors' generates retrieval anchors per doc at ingest; 'rewrite' rewrites the query before search;
                                       'hyde' appends a hypothetical answer to the query; 'all' enables every op.
              --agent-provider <p>     Required when --agent != off: openai | anthropic | gemini | ollama | claude-cli | codex-cli | copilot-cli
              --agent-base-url <url>   Optional HTTP endpoint override for ollama/openai.
              --agent-model <m>        Required for ollama/openai/anthropic/gemini; optional for agent CLIs.
              --agent-api-key <key>    Bearer / API key for openai/anthropic/gemini. Ollama doesn't need one.
              --ingest-concurrency <N> Max concurrent IngestAsync calls per question's haystack (default: 1 = sequential). Set to 4-16 when --agent is active to keep wall-time tractable; subscription CLI rate limits and your local CPU cap the useful upper bound.

            Agentic-ingest arm (locomo, longmemeval): the LLM IS the ingestion — a real tool-calling
            episode per session decides what to remember, instead of Scrinia's mechanical per-doc ingest.
            No embedder, no reranker; compare against the BM25 mechanical arm (--mode bm25).
              --agentic-ingest         Enable the agentic-ingest arm. Requires --agent-provider (openai-compatible only). Mutually exclusive with --agent <ops>.
              --episode-max-turns <n> Max tool-call turns per session episode (default 16)
              --episode-max-tokens <n> Max completion tokens per turn — caps reasoning + tool-call args together (default 4096; raise for reasoning models to avoid truncated tool calls)

            Judge answerability flags (longmemeval, locomo):
              --judge                 Enable the LLM-judge answerability metric (needs --judge-provider). Works with or without --agentic-ingest.
              --judge-provider <p>    Judge model provider (openai | anthropic | gemini | ollama | claude-cli | ...). Held constant across arms.
              --judge-model <m>       Judge model (both answerer and grader).
              --judge-base-url <u>    OpenAI-compatible endpoint for the judge.
              --judge-api-key <k>     API key for the judge provider.
              --judge-k <n>           Retrieved items the answerer sees (default 10).

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
