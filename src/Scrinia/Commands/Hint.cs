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
/// <c>search({ query: '...' })</c> would be useful.
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
    /// SearchAll returns the top <paramref name="innerLimit"/> candidates, MMR with
    /// <paramref name="diversityLambda"/> reranks down to <paramref name="topK"/> — the
    /// surface the <c>Scrinia:Hint:DiversityLambda</c> / <c>:InnerLimit</c> config keys
    /// flow into. Tests pass defaults positionally; the CLI passes config-derived values.
    /// </summary>
    public HintResult Compute(
        string? rawPrompt,
        double minScore,
        int minPromptChars,
        int topK = DefaultTopK,
        int innerLimit = DefaultInnerLimit,
        double diversityLambda = DefaultDiversityLambda)
    {
        string prompt = (rawPrompt ?? "").Trim();
        if (prompt.Length < minPromptChars)
            return HintResult.Empty;

        // SearchAll over a wider pool than topK so MMR has alternatives to choose from when
        // breaking up a single-source flood. Below the score floor → no point feeding into
        // MMR; filter first. Accept both EntryResult and ChunkEntryResult — SearchAll
        // dedupes per memory so each candidate is one or the other (whichever scored higher
        // for that memory), and dropping ChunkEntryResult would silently lose memories whose
        // best signal came from a chunk match.
        int searchLimit = Math.Max(topK, innerLimit);
        var raw = _store.SearchAll(prompt, scopes: null, limit: searchLimit);
        var pool = raw
            .Where(r => r is EntryResult or ChunkEntryResult)
            .Where(r => r.Score >= minScore)
            .ToList();
        if (pool.Count == 0)
            return HintResult.Empty;

        var diversified = MmrReranker.Rerank(pool, topK, diversityLambda);
        if (diversified.Count == 0)
            return HintResult.Empty;

        return new HintResult(
            Emitted: true,
            Matches: diversified
                .Select(r => r switch
                {
                    EntryResult er => new HintMatch(er.Item.Scope, er.Item.Entry.Name, er.Score),
                    ChunkEntryResult cr => new HintMatch(cr.ParentItem.Scope, cr.ParentItem.Entry.Name, cr.Score),
                    _ => null!,
                })
                .Where(m => m is not null)
                .ToList());
    }

    /// <summary>
    /// Format the structured result as a client-tailored hook payload. Wraps an imperative,
    /// second-person callout in <c>&lt;scrinia-hint&gt;</c> tags and (for clients whose hook
    /// protocol reads stdout JSON) nests it inside the <c>hookSpecificOutput.additionalContext</c>
    /// shape shared by Claude Code, Codex, and Copilot.
    ///
    /// <para>The payload is derived from one template, varied per client by
    /// <see cref="HintCommand.ProfileFor"/> on three axes (emit / terseness / envelope):</para>
    /// <list type="bullet">
    ///   <item><b>Default / Codex / Copilot</b> — the validated reference output (full payload +
    ///   JSON envelope). Codex follows this verbatim; <c>FormatHook(result)</c> with no client is
    ///   byte-identical to the historical output so existing installs never regress.</item>
    ///   <item><b>Claude Code</b> — terse payload. Claude's <c>additionalContext</c> accumulates a
    ///   fresh <c>&lt;system-reminder&gt;</c> every turn, so the per-turn hint is kept short.</item>
    ///   <item><b>OpenCode</b> — bare payload, no JSON envelope: the OpenCode plugin injects the
    ///   string directly via <c>chat.messages.transform</c> rather than emitting hook stdout.</item>
    /// </list>
    ///
    /// <para>Design intent (unchanged): the payload reads as an instruction to the model —
    /// concrete verb, justification, copy-paste-shaped tool call — not a passive log line that
    /// models triage as ignorable status output.</para>
    /// </summary>
    public static string FormatHook(
        HintResult result, string hookEventName = "UserPromptSubmit", HookClient client = HookClient.Default)
    {
        if (!result.Emitted || result.Matches.Count == 0)
            return string.Empty;

        var profile = ProfileFor(client);
        if (!profile.Emit)
            return string.Empty;

        var names = result.Matches.Select(m => m.Name).ToList();
        string countWord = names.Count == 1 ? "memory" : "memories";
        string list = string.Join(", ", names);
        string firstName = names[0];

        string payload = profile.Terse
            ? $"<scrinia-hint>{names.Count} matching {countWord}: {list}. " +
              $"Call search({{ query: '{firstName}' }}) before answering.</scrinia-hint>"
            : $"<scrinia-hint priority=\"high\">\n" +
              $"The user has {names.Count} stored {countWord} that match this prompt: {list}.\n" +
              $"Before answering, call search({{ query: '{firstName}' }}) to retrieve them — they " +
              $"contain prior decisions and context the user expects you to remember.\n" +
              $"</scrinia-hint>";

        return profile.Envelope ? BuildHookEnvelope(hookEventName, payload) : payload;
    }

    /// <summary>
    /// Format the SessionStart restore payload (the prior-session memory list, already rendered
    /// to <paramref name="yaml"/>) as a client-tailored hook payload. Same client profile axes as
    /// <see cref="FormatHook"/>: Claude gets a terser framing line (its injection accumulates),
    /// envelope-less clients get the bare payload. <c>FormatRestoreHook(yaml)</c> with no client is
    /// byte-identical to the historical <c>scri restore --hook</c> output.
    /// </summary>
    public static string FormatRestoreHook(string yaml, HookClient client = HookClient.Default)
    {
        var profile = ProfileFor(client);
        string framing = profile.Terse
            ? "Memories from prior sessions in this workspace. " +
              "Call search({ query: '<name>' }) to retrieve full content."
            : "The following memories were saved during prior sessions in this workspace. " +
              "Reference them when relevant; call search({ query: '<name>' }) to retrieve " +
              "the full content of any entry below.";

        string payload =
            "<scrinia-restored-memory>\n" +
            framing + "\n\n" +
            yaml.TrimEnd() + "\n" +
            "</scrinia-restored-memory>";

        return profile.Envelope ? BuildHookEnvelope("SessionStart", payload) : payload;
    }

    /// <summary>
    /// Maps a <see cref="HookClient"/> to its formatting profile. Default/Codex/Copilot share the
    /// frozen reference shape; Claude Code is terse (accumulation); OpenCode drops the JSON envelope
    /// (the plugin injects the bare string). Copilot stays on the reference shape until its hook
    /// channel is verified — see the per-client plan notes.
    /// </summary>
    internal static HookClientProfile ProfileFor(HookClient client) => client switch
    {
        HookClient.ClaudeCode => new HookClientProfile(Emit: true, Terse: true, Envelope: true),
        HookClient.OpenCode => new HookClientProfile(Emit: true, Terse: false, Envelope: false),
        // Default, Codex, Copilot — validated reference output, kept byte-identical.
        _ => new HookClientProfile(Emit: true, Terse: false, Envelope: true),
    };

    /// <summary>
    /// Parses the <c>--client</c> flag value (case-insensitive) into a <see cref="HookClient"/>.
    /// Unknown or null values fall back to <see cref="HookClient.Default"/> so an unrecognized
    /// client degrades to the safe reference format rather than failing.
    /// </summary>
    public static HookClient ParseClient(string? client) => client?.Trim().ToLowerInvariant() switch
    {
        "codex" => HookClient.Codex,
        "claude" or "claudecode" or "claude-code" => HookClient.ClaudeCode,
        "opencode" => HookClient.OpenCode,
        "copilot" => HookClient.Copilot,
        _ => HookClient.Default,
    };

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
            $"Run search({{ query: '{names[0]}' }}) to retrieve.";
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

/// <summary>
/// Agent CLI whose hook/injection channel a hint or restore payload is being formatted for.
/// Selected via the <c>--client</c> flag on <c>scri hint</c> / <c>scri restore</c>, stamped into
/// each CLI's hook command at install time (and baked into the OpenCode plugin). <see cref="Default"/>
/// is the safe reference format used when no client is specified.
/// </summary>
public enum HookClient { Default, Codex, ClaudeCode, OpenCode, Copilot }

/// <summary>
/// Per-client formatting profile — the three axes the one payload template varies on.
/// </summary>
/// <param name="Emit">Whether to emit a payload at all (false suppresses on a dead channel).</param>
/// <param name="Terse">Use the short payload variant (Claude Code, to limit per-turn accumulation).</param>
/// <param name="Envelope">Wrap in the <c>hookSpecificOutput</c> JSON envelope (false = bare payload,
/// for clients like OpenCode whose plugin injects the string directly).</param>
internal readonly record struct HookClientProfile(bool Emit, bool Terse, bool Envelope);
