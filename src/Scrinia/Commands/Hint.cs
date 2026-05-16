using System.Text;
using System.Text.Json;
using Scrinia.Core;
using Scrinia.Core.Search;
using Spectre.Console;

namespace Scrinia.Commands;

/// <summary>
/// Pre-send relevance hint: given the user's about-to-be-submitted prompt, emit a
/// one-line marker telling the agent which stored memories look relevant. No retrieval,
/// no LLM call — just a fast BM25 top-K lookup so the agent (or the user) knows
/// <c>memory('search', '...')</c> would be useful.
///
/// <para>Wired into agent CLIs via the <c>UserPromptSubmit</c> /
/// <c>userPromptSubmitted</c> hook by the hook installers. The CLI invokes
/// <c>scri hint</c> with the prompt piped on stdin or passed as a positional arg;
/// the hint goes back to the CLI as plain stdout, which each CLI's hook protocol
/// then injects into the agent's context.</para>
///
/// <para>Thresholds (<c>Scrinia:Hint:MinPromptChars</c>,
/// <c>Scrinia:Hint:MinScore</c>) keep noise down — short prompts ("hi", "thanks")
/// and weak matches don't fire. The user can disable globally with
/// <c>Scrinia:Hint:Enabled false</c>.</para>
/// </summary>
public sealed class HintCommand
{
    /// <summary>Default BM25 score floor — empirically tuned; tweak via config.</summary>
    public const double DefaultMinScore = 10.0;

    /// <summary>Default minimum prompt length — anything shorter skips the lookup entirely.</summary>
    public const int DefaultMinPromptChars = 8;

    /// <summary>Default output count returned to the agent.</summary>
    public const int DefaultTopK = 3;

    /// <summary>
    /// Default inner-K — how many candidates pass through MMR rerank. Larger gives the
    /// reranker more diversity choices; smaller is faster. 10 is the project default.
    /// </summary>
    public const int DefaultInnerLimit = 10;

    /// <summary>
    /// Default MMR λ. 0.6 = "favor relevance, but break single-source floods" — empirically
    /// good for the documented "one chatty session dominates top-K" failure pattern.
    /// </summary>
    public const double DefaultDiversityLambda = 0.6;

    private readonly IMemoryStore _store;

    public HintCommand(IMemoryStore store) => _store = store;

    /// <summary>
    /// Resolve a hint for <paramref name="rawPrompt"/> and return the structured result.
    /// Pure function for testability — the CLI wrapper handles stdin / stdout / config.
    /// </summary>
    public HintResult Compute(string? rawPrompt, double minScore, int minPromptChars)
        => Compute(rawPrompt, minScore, minPromptChars, DefaultTopK, DefaultInnerLimit, DefaultDiversityLambda);

    /// <summary>
    /// Resolve a hint with explicit MMR parameters. SearchAll returns the top
    /// <paramref name="innerLimit"/> candidates, MMR with <paramref name="diversityLambda"/>
    /// rerank down to <paramref name="topK"/> — this is the surface the
    /// <c>Scrinia:Hint:DiversityLambda</c> / <c>:InnerLimit</c> config keys flow into.
    /// </summary>
    public HintResult Compute(
        string? rawPrompt,
        double minScore,
        int minPromptChars,
        int topK,
        int innerLimit,
        double diversityLambda)
    {
        string prompt = (rawPrompt ?? "").Trim();
        if (prompt.Length < minPromptChars)
            return HintResult.Empty;

        // SearchAll over a wider pool than topK so MMR has alternatives to choose from when
        // breaking up a single-source flood. Below the score floor → no point feeding into
        // MMR; filter first.
        int searchLimit = Math.Max(topK, innerLimit);
        var raw = _store.SearchAll(prompt, scopes: null, limit: searchLimit);
        var pool = raw
            .OfType<EntryResult>()
            .Where(r => r.Score >= minScore)
            .Cast<SearchResult>()
            .ToList();
        if (pool.Count == 0)
            return HintResult.Empty;

        var diversified = MmrReranker.Rerank(pool, topK, diversityLambda);
        if (diversified.Count == 0)
            return HintResult.Empty;

        return new HintResult(
            Emitted: true,
            Matches: diversified
                .OfType<EntryResult>()
                .Select(e => new HintMatch(e.Item.Scope, e.Item.Entry.Name, e.Score))
                .ToList());
    }

    /// <summary>
    /// Format the structured result as the hook-output JSON envelope each big-3 agent CLI
    /// understands. Wraps an imperative, second-person callout in <c>&lt;scrinia-hint&gt;</c>
    /// tags then nests that inside the <c>hookSpecificOutput.additionalContext</c> shape
    /// shared across Claude Code, Codex, and Copilot (where supported).
    ///
    /// <para>Two design moves driven by tool-ergonomics research: (1) JSON envelope keeps
    /// the injection off the user's transcript on Claude Code while still reaching the
    /// model's context (plain stdout is wrapped in a visible <c>&lt;system-reminder&gt;</c>;
    /// the JSON shape is added more discretely). (2) The payload reads as an instruction
    /// to the model — concrete verb, justification, copy-paste-shaped tool call — rather
    /// than a passive log line, which models triage as ignorable status output.</para>
    /// </summary>
    public static string FormatHook(HintResult result, string hookEventName = "UserPromptSubmit")
    {
        if (!result.Emitted || result.Matches.Count == 0)
            return string.Empty;

        var names = result.Matches.Select(m => m.Name).ToList();
        string countWord = names.Count == 1 ? "memory" : "memories";
        string list = string.Join(", ", names);
        string firstName = names[0];

        string payload =
            $"<scrinia-hint priority=\"high\">\n" +
            $"The user has {names.Count} stored {countWord} that match this prompt: {list}.\n" +
            $"Before answering, call memory('search', '{firstName}') to retrieve them — they " +
            $"contain prior decisions and context the user expects you to remember.\n" +
            $"</scrinia-hint>";

        return BuildHookEnvelope(hookEventName, payload);
    }

    /// <summary>
    /// Builds the <c>{"hookSpecificOutput":{"hookEventName":"...","additionalContext":"..."}}</c>
    /// envelope shape understood by Claude Code, Codex, and Copilot (where supported).
    /// Uses <see cref="Utf8JsonWriter"/> so we don't pay the trim-warning tax that comes
    /// with <c>JsonSerializer.Serialize&lt;T&gt;</c> for one ad-hoc shape — the writer
    /// handles all JSON-string escaping (newlines, quotes, control chars) natively.
    /// </summary>
    internal static string BuildHookEnvelope(string hookEventName, string additionalContext)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("hookSpecificOutput");
            writer.WriteString("hookEventName", hookEventName);
            writer.WriteString("additionalContext", additionalContext);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Plain single-line hint string for human inspection (e.g. <c>scri hint "auth" --plain</c>).
    /// NOT what the hook emits — agent CLIs get <see cref="FormatHook"/> output instead, which
    /// reads as an instruction to the model rather than a log line.
    /// </summary>
    public static string FormatPlain(HintResult result)
    {
        if (!result.Emitted || result.Matches.Count == 0)
            return string.Empty;

        var names = result.Matches.Select(m => m.Name).ToList();
        string countWord = names.Count == 1 ? "memory" : "memories";
        return $"[scrinia] {names.Count} {countWord} match: {string.Join(", ", names)}. " +
            $"Run memory('search', '{names[0]}') to retrieve.";
    }
}

/// <summary>Structured hint output. Returned by <see cref="HintCommand.Compute"/>.</summary>
/// <param name="Emitted">False when filters (length / score / enabled) suppressed the hint.</param>
/// <param name="Matches">Ordered top-K matches above the score floor; empty when not emitted.</param>
public sealed record HintResult(bool Emitted, IReadOnlyList<HintMatch> Matches)
{
    public static HintResult Empty { get; } = new(false, []);
}

/// <param name="Scope">Memory's scope (e.g. <c>local</c>, <c>local-topic:api</c>).</param>
/// <param name="Name">Memory's name within scope.</param>
/// <param name="Score">BM25 + field-match score from the searcher.</param>
public sealed record HintMatch(string Scope, string Name, double Score);
