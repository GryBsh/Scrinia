namespace Scrinia.Mcp;

/// <summary>
/// Soft-warning detector for paths that look like a malformed reserved prefix
/// (case-mismatch, singular/plural, or bare prefix without a leaf). Returns
/// a human-readable suggestion string or null when the path is fine.
///
/// Reserved prefixes are documented in <c>prompts/guide.md</c> and listed below.
/// This guard is intentionally lenient — no false positives on legitimate
/// non-reserved paths like <c>/api/auth</c> or <c>/findings-archive/x</c>.
/// </summary>
internal static class ReservedPrefixGuard
{
    private static readonly string[] Reserved =
    [
        "skill", "agent", "patterns", "findings", "learn",
        "sessions", "checkpoint", "temp"
    ];

    /// <summary>
    /// Maps a malformed first segment to its canonical reserved counterpart.
    /// Covers case differences and singular/plural mismatches that we expect
    /// agents to typo most often.
    /// </summary>
    private static readonly Dictionary<string, string> NearMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        // singular ↔ plural mismatches against the canonical reserved spelling
        ["agents"] = "agent",
        ["skills"] = "skill",
        ["pattern"] = "patterns",
        ["finding"] = "findings",
        ["session"] = "sessions",
        ["checkpoints"] = "checkpoint",
        ["temps"] = "temp",
        ["learns"] = "learn",
        ["lesson"] = "learn",
        ["lessons"] = "learn",
    };

    /// <summary>
    /// Returns a soft-warning hint when <paramref name="path"/> looks like a
    /// malformed reserved-prefix write. Returns null when no hint is warranted.
    /// </summary>
    public static string? HintFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.StartsWith('/')) return null;  // legacy topic:subject form, no hint

        string firstSegment = ExtractFirstSegment(path);
        if (firstSegment.Length == 0) return null;

        // Exact reserved match (case-sensitive) — no hint.
        foreach (var reserved in Reserved)
        {
            if (firstSegment == reserved) return null;
        }

        // Case-insensitive match → suggest the canonical case.
        foreach (var reserved in Reserved)
        {
            if (firstSegment.Equals(reserved, StringComparison.OrdinalIgnoreCase))
            {
                return $"Path segment '/{firstSegment}/' should be lower-case '/{reserved}/' to use the reserved namespace. " +
                       "Stored as-is; rename via memory('remember') with the canonical path if intended.";
            }
        }

        // Near-match (plural/singular swap) → suggest the canonical reserved name.
        if (NearMatches.TryGetValue(firstSegment, out var canonical))
        {
            return $"Path segment '/{firstSegment}/' is close to the reserved prefix '/{canonical}/'. " +
                   "Stored as-is — if you intended the reserved namespace, use the canonical spelling.";
        }

        return null;
    }

    private static string ExtractFirstSegment(string path)
    {
        // path is "/something/..." or "/something" — return the first non-empty segment.
        int start = 1;
        int end = path.IndexOf('/', start);
        return end < 0 ? path[start..] : path[start..end];
    }
}
