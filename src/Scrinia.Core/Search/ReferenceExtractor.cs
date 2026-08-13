using System.Text.RegularExpressions;

namespace Scrinia.Core.Search;

/// <summary>Extracts file path and memory name references from content text.</summary>
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
    [GeneratedRegex(@"(?<![\w""'`=:./\-\\])(?:\.?/)?(?:[\w][\w./\-\\]*)?[\w]+\.(cs|ts|tsx|json|md|yaml|yml|csproj|sln|xml|txt|nmp2|ps1|sh)\b")]
    private static partial Regex FilePathPattern();

    // Memory names: [[wiki-link]] references, e.g. [[/api/auth]], [[session-notes]],
    // or [[~scratch]]. The inner target is a path ('/topic/subject'), a bare local
    // name, or an ephemeral '~name'. Brackets disambiguate refs from prose and file paths.
    [GeneratedRegex(@"\[\[\s*(~?/?[a-z0-9][a-z0-9/_.-]*)\s*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex MemoryNamePattern();

    // Ephemeral memory names: ~name
    [GeneratedRegex(@"(?<!\w)(~[a-z][a-z0-9-]+)")]
    private static partial Regex EphemeralNamePattern();

    /// <summary>Extract file path references from content.</summary>
    public static string[] ExtractFileRefs(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return FilePathPattern().Matches(content)
            .Select(m => m.Value.TrimStart('.', '/'))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Extract memory name references ([[/path]] / [[name]] wiki-links and ~ephemeral) from content.</summary>
    public static string[] ExtractMemoryRefs(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var persistent = MemoryNamePattern().Matches(content)
            .Select(m => m.Groups[1].Value);
        var ephemeral = EphemeralNamePattern().Matches(content)
            .Select(m => m.Groups[1].Value);

        return persistent.Concat(ephemeral)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
