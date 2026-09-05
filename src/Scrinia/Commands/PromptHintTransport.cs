using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scrinia.Commands;

/// <summary>
/// Pure prompt-hint transport: selects a positional prompt or passes stdin through, then
/// parses and classifies it deterministically for the fail-silent <c>scri hint</c> commit/push
/// advisory.
///
/// <para><see cref="SelectPrompt"/> only decides which input wins — a positional prompt always
/// takes precedence — and returns stdin verbatim when no positional prompt is given; the bounded
/// stdin read happens elsewhere (<c>IBoundedStdinReader</c>).</para>
///
/// <para><see cref="Classify"/> applies one BOM strip and Unicode whitespace trim, extracts the
/// exact case-sensitive string <c>prompt</c> property from a single JSON object when the first
/// trimmed character is <c>{</c> (never falling back to raw text on failure), normalizes NFKC,
/// tokenizes maximal letter/digit runs, and flags commit/push intents against the eight allowed
/// inflections. Non-matching, malformed, nonobject, missing, or nonstring input is
/// <see cref="PromptTransportResult.Silent"/>.</para>
/// </summary>
public static class PromptHintTransport
{
    /// <summary>Maximal Unicode letter/digit runs used to tokenize normalized prompt text.</summary>
    private static readonly Regex Token = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>Commit inflections that fire the commit reminder.</summary>
    private static readonly HashSet<string> CommitTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "commit", "commits", "committed", "committing",
    };

    /// <summary>Push inflections that fire the push reminder.</summary>
    private static readonly HashSet<string> PushTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "push", "pushes", "pushed", "pushing",
    };

    /// <summary>
    /// Decide which input drives the advisory. A non-empty positional prompt always wins;
    /// otherwise the raw stdin is returned as-is (the bounded read happens elsewhere).
    /// </summary>
    public static string? SelectPrompt(string? positionalPrompt, string? stdinRaw)
        => string.IsNullOrEmpty(positionalPrompt) ? stdinRaw : positionalPrompt;

    /// <summary>
    /// Decide which input drives the advisory through a bounded stdin reader. A non-empty
    /// positional prompt always wins and the reader is never invoked; otherwise the reader supplies
    /// stdin verbatim. The reader's <c>null</c> result — oversize, invalid UTF-8, timeout, or read
    /// failure — is returned as-is so the advisory can fail silently.
    /// </summary>
    public static string? SelectPrompt(string? positionalPrompt, IBoundedStdinReader reader)
        => string.IsNullOrEmpty(positionalPrompt)
            ? reader?.ReadBoundedStdin()
            : positionalPrompt;

    /// <summary>
    /// Classify a single selected prompt string. Strips one BOM and trims Unicode whitespace,
    /// extracts the exact case-sensitive string <c>prompt</c> property from a single JSON object
    /// when the first trimmed character is <c>{</c>, normalizes NFKC, tokenizes maximal
    /// letter/digit runs, and flags commit/push intents.
    /// </summary>
    /// <returns>
    /// <see cref="PromptTransportResult.Silent"/> when the input is null/empty, is malformed or
    /// nonobject JSON, is missing or nonstring <c>prompt</c>, has trailing non-whitespace after a
    /// JSON object, or contains no matching token; otherwise <c>Present = true</c> with the
    /// matched intent flags. Negation never suppresses a match.
    /// </returns>
    public static PromptTransportResult Classify(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt))
            return PromptTransportResult.Silent;

        string working = StripBomAndTrim(prompt);
        if (string.IsNullOrEmpty(working))
            return PromptTransportResult.Silent;

        if (working[0] == '{')
        {
            string? extracted = TryExtractJsonPrompt(working);
            if (extracted is null)
                return PromptTransportResult.Silent;
            working = extracted;
        }

        if (string.IsNullOrEmpty(working))
            return PromptTransportResult.Silent;

        working = working.Normalize(NormalizationForm.FormKC);

        bool commit = false, push = false;
        foreach (Match match in Token.Matches(working))
        {
            string token = match.Value;
            if (CommitTokens.Contains(token))
                commit = true;
            else if (PushTokens.Contains(token))
                push = true;
        }

        if (!commit && !push)
            return PromptTransportResult.Silent;

        return new PromptTransportResult(true, working, commit, push);
    }

    /// <summary>
    /// Select then classify: positional wins over stdin, then classify the selected input.
    /// Null/empty selection is <see cref="PromptTransportResult.Silent"/>.
    /// </summary>
    public static PromptTransportResult Parse(string? positionalPrompt, string? stdinRaw)
    {
        string? selected = SelectPrompt(positionalPrompt, stdinRaw);
        if (string.IsNullOrEmpty(selected))
            return PromptTransportResult.Silent;
        return Classify(selected);
    }

    /// <summary>Strip a single leading UTF-8 BOM, then trim Unicode whitespace.</summary>
    private static string StripBomAndTrim(string input)
    {
        string working = input;
        if (working.Length > 0 && working[0] == '\uFEFF')
            working = working.Substring(1);
        return working.Trim();
    }

    /// <summary>
    /// Parse exactly one JSON object and return its exact case-sensitive string <c>prompt</c>
    /// property, or <c>null</c> when the input is malformed, not an object, lacks the property,
    /// or the property is not a string. Trailing non-whitespace after the object is rejected.
    /// </summary>
    private static string? TryExtractJsonPrompt(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (doc.RootElement.TryGetProperty("prompt", out JsonElement promptEl)
                && promptEl.ValueKind == JsonValueKind.String)
            {
                return promptEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed JSON, or trailing non-whitespace after the object: never fall back.
            return null;
        }

        return null;
    }
}

/// <summary>
/// Result of classifying a prompt-hint transport input. <see cref="Present"/> is false only for
/// <see cref="Silent"/>; <see cref="Prompt"/> is the normalized classified text when present.
/// </summary>
/// <param name="Present">Whether a commit or push intent was detected.</param>
/// <param name="Prompt">The normalized (NFKC) classified text, or null when silent.</param>
/// <param name="CommitIntent">Whether the text contains a commit inflection.</param>
/// <param name="PushIntent">Whether the text contains a push inflection.</param>
public sealed record PromptTransportResult(bool Present, string? Prompt, bool CommitIntent, bool PushIntent)
{
    /// <summary>Silent result: no present prompt and no commit/push intent.</summary>
    public static PromptTransportResult Silent => new(false, null, false, false);
}
