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

    private readonly IMemoryStore _store;

    public HintCommand(IMemoryStore store) => _store = store;

    /// <summary>
    /// Resolve a hint for <paramref name="rawPrompt"/> and return the formatted output
    /// plus a flag indicating whether a hint was emitted at all. Pure function for
    /// testability — the CLI wrapper handles stdin / stdout / config-reading.
    /// </summary>
    public HintResult Compute(string? rawPrompt, double minScore, int minPromptChars)
    {
        string prompt = (rawPrompt ?? "").Trim();
        if (prompt.Length < minPromptChars)
            return HintResult.Empty;

        var results = _store.SearchAll(prompt, scopes: null, limit: 3);
        var entries = results
            .OfType<EntryResult>()
            .Where(r => r.Score >= minScore)
            .ToList();
        if (entries.Count == 0)
            return HintResult.Empty;

        return new HintResult(
            Emitted: true,
            Matches: entries.Select(e => new HintMatch(e.Item.Scope, e.Item.Entry.Name, e.Score)).ToList());
    }

    /// <summary>Format the structured result as the single-line stdout hint string.</summary>
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
