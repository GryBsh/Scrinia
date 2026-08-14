using System.Text.RegularExpressions;

namespace Scrinia.Core.Search;

/// <summary>Extracts file path and memory name references from content text, scanning outside
/// fenced code blocks only (see <see cref="ProseScanner"/>) — a stack trace or diff pasted into a
/// fenced block no longer yields file or memory references. YAML frontmatter and inline-code spans
/// are prose, not stripped, so a <c>file:</c> line in frontmatter and a backtick-quoted
/// <c>[[wiki-link]]</c> or <c>~ephemeral</c> still extract.</summary>
public static partial class ReferenceExtractor
{
    // File paths: word chars, dots, slashes, hyphens followed by known extensions
    // Anchored to avoid matching inside URLs or other noise
    //
    // The lookbehind must exclude word AND path characters, not just quote/=/: — excluding only
    // quote/=/: does not suppress a match after one of those, it SHIFTS it: on "file:src/new.cs"
    // the engine fails at 's' (preceded by ':'), advances one character, and succeeds at 'r' —
    // yielding the corrupt path "rc/new.cs". A legitimate path starts after whitespace, a line
    // start, or punctuation like '('; if the preceding character is a word or path character the
    // engine is mid-token and must not match.
    //
    // The prefix group is two independent widenings, not one:
    //  - "0-2 leading dots" (not just one) lets a parent-relative form ("../src/a.cs") match from
    //    its own start. Excluding '.' from the lookbehind above means the engine can no longer coast
    //    in from a later restart position the way the old narrower lookbehind coincidentally allowed
    //    (it used to match "../src/a/b.cs" by restarting at index 1, i.e. at the SECOND dot), so the
    //    prefix group now has to consume the whole ".." itself.
    //  - "[/\\]" (forward slash OR backslash) is what keeps ".\src\a.cs" matching under the WIDER
    //    lookbehind. Not because a backslash path never matched before — it did: the old lookbehind
    //    permitted '\', so the engine failed at the leading '.', restarted, and succeeded at 's',
    //    matching "src\a.cs" and trimming to the same value this pattern produces. That coasting is
    //    exactly what the new lookbehind blocks, and with a slash-only prefix group the pattern then
    //    finds NO match at all in ".\src\a.cs" (verified against both patterns directly). The
    //    backslash alternative is what lets the match start at the path's own beginning instead.
    //  - The group is a Kleene star ("*"), not "?" (0-or-1): a single optional repetition only ever
    //    restores ONE level ("../"), so "../../src/a.cs" still matched nothing under an earlier round
    //    of this fix. The star lets the prefix repeat, so "../../", "..\..\", and deeper chains all
    //    match their own full prefix, not just the first segment of it.
    [GeneratedRegex(@"(?<![\w""'`=:./\-\\])(?:\.{0,2}[/\\])*(?:[\w][\w./\-\\]*)?[\w]+\.(cs|ts|tsx|json|md|yaml|yml|csproj|sln|xml|txt|nmp2|ps1|sh)\b")]
    private static partial Regex FilePathPattern();

    // Memory names: [[wiki-link]] references, e.g. [[/api/auth]], [[session-notes]],
    // or [[~scratch]]. The inner target is a path ('/topic/subject'), a bare local
    // name, or an ephemeral '~name'. Brackets disambiguate refs from prose and file paths.
    [GeneratedRegex(@"\[\[\s*(~?/?[a-z0-9][a-z0-9/_.-]*)\s*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex MemoryNamePattern();

    // Ephemeral memory names: ~name
    [GeneratedRegex(@"(?<!\w)(~[a-z][a-z0-9-]+)")]
    private static partial Regex EphemeralNamePattern();

    /// <summary>Extract file path references from content, outside fenced code blocks (see
    /// <see cref="ProseScanner"/>). Runs per prose line — <see cref="FilePathPattern"/> cannot span
    /// a newline.</summary>
    public static string[] ExtractFileRefs(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return ProseScanner.ProseLines(content)
            .SelectMany(line => FilePathPattern().Matches(line))
            .Select(m => m.Value.TrimStart('.', '/', '\\'))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Extract memory name references ([[/path]] / [[name]] wiki-links and ~ephemeral)
    /// from content, outside fenced code blocks (see <see cref="ProseScanner"/>).
    /// <see cref="MemoryNamePattern"/> runs over the prose lines rejoined with <c>"\n"</c> — its
    /// <c>\s*</c> matches a newline, so a link wrapped across lines still extracts, while a fenced
    /// block stays excluded. <see cref="EphemeralNamePattern"/> runs per prose line — it cannot
    /// span a newline.</summary>
    public static string[] ExtractMemoryRefs(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        string[] proseLines = [.. ProseScanner.ProseLines(content)];

        var persistent = MemoryNamePattern().Matches(string.Join("\n", proseLines))
            .Select(m => m.Groups[1].Value);
        var ephemeral = proseLines
            .SelectMany(line => EphemeralNamePattern().Matches(line))
            .Select(m => m.Groups[1].Value);

        return persistent.Concat(ephemeral)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
