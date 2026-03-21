using System.Text.RegularExpressions;

namespace Scrinia.Core.Search;

/// <summary>Extracts file path and memory name references from content text.</summary>
public static partial class ReferenceExtractor
{
    // File paths: word chars, dots, slashes, hyphens followed by known extensions
    // Anchored to avoid matching inside URLs or other noise
    [GeneratedRegex(@"(?<![""'`=:])(?:\.?/)?(?:[\w][\w./\-\\]*)?[\w]+\.(cs|ts|tsx|json|md|yaml|yml|csproj|sln|xml|txt|nmp2|ps1|sh)\b")]
    private static partial Regex FilePathPattern();

    // Memory names: topic:subject pattern (lowercase, hyphens allowed)
    // Must not be preceded by :// (avoid URLs) or / (avoid file paths)
    [GeneratedRegex(@"(?<![\w:/])([a-z][a-z0-9]*:[a-z][a-z0-9-]+)")]
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

    /// <summary>Extract memory name references (topic:subject and ~ephemeral) from content.</summary>
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
