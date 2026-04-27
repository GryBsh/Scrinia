namespace Scrinia.Core;

/// <summary>A single segment in a parsed path.</summary>
/// <param name="Value">The raw text of this segment.</param>
/// <param name="IsEntityType">True when this segment matched a known entity type.</param>
/// <param name="EntityId">The paired ID segment when <see cref="IsEntityType"/> is true; null otherwise.</param>
public record PathSegment(string Value, bool IsEntityType, string? EntityId);

/// <summary>An entity-type / ID pair extracted from a path.</summary>
/// <param name="EntityType">The entity type name (e.g. "goal").</param>
/// <param name="Id">The entity identifier (e.g. "G-5").</param>
public record EntityIdPair(string EntityType, string Id);

/// <summary>The fully-parsed representation of a Scrinia v2 path.</summary>
/// <param name="Segments">Ordered list of path segments.</param>
/// <param name="EntityPairs">Entity-type / ID pairs discovered by pairwise walking.</param>
/// <param name="Tags">Non-entity segments collected as freeform tags.</param>
/// <param name="RawPath">The normalized path string (leading <c>/</c>, no trailing <c>/</c>).</param>
/// <param name="IsEntityPath">True when at least one <see cref="EntityIdPair"/> was found.</param>
/// <param name="LeafSegment">The <see cref="PathSegment.Value"/> of the last segment.</param>
public record ParsedPath(
    IReadOnlyList<PathSegment> Segments,
    IReadOnlyList<EntityIdPair> EntityPairs,
    IReadOnlyList<string> Tags,
    string RawPath,
    bool IsEntityPath,
    string LeafSegment);

/// <summary>
/// Pure, dependency-free parser that converts a Scrinia v2 path string into a
/// structured <see cref="ParsedPath"/>.  The caller supplies the set of known
/// entity types so the parser stays in <c>Scrinia.Core</c> without referencing
/// <c>Scrinia.Mcp</c>.
/// </summary>
public static class PathParser
{
    /// <summary>
    /// Parses <paramref name="rawPath"/> into a <see cref="ParsedPath"/>.
    /// </summary>
    /// <param name="rawPath">
    /// A forward-slash-delimited path such as <c>/goal/G-5/phase/01/task/fix</c>.
    /// </param>
    /// <param name="entityTypes">
    /// Case-insensitive set of known entity type names (e.g. goal, phase, task).
    /// </param>
    /// <returns>A fully-parsed <see cref="ParsedPath"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="rawPath"/> is null, empty, whitespace, or contains
    /// illegal characters / sequences (<c>..</c>, <c>\</c>, <c>:</c>, control chars).
    /// </exception>
    public static ParsedPath Parse(string rawPath, IReadOnlySet<string> entityTypes)
    {
        // --- 1. Validate ---------------------------------------------------------
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("Path must not be null, empty, or whitespace.", nameof(rawPath));

        // Trim before validation so surrounding whitespace is benign.
        var trimmed = rawPath.Trim();

        if (trimmed.Contains('\\'))
            throw new ArgumentException("Path must not contain backslashes.", nameof(rawPath));

        if (trimmed.Contains(':'))
            throw new ArgumentException("Path must not contain colons.", nameof(rawPath));

        if (trimmed.Contains(".."))
            throw new ArgumentException("Path must not contain '..' sequences.", nameof(rawPath));

        if (ContainsControlChars(trimmed))
            throw new ArgumentException("Path must not contain control characters.", nameof(rawPath));

        // --- 2. Normalize --------------------------------------------------------
        var normalized = Normalize(trimmed);

        // --- 3. Split on '/' (skip leading empty segment) ------------------------
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            throw new ArgumentException("Path resolves to empty after normalization.", nameof(rawPath));

        // --- 4. Walk segments pairwise for entity/ID inference --------------------
        var segments = new List<PathSegment>();
        var entityPairs = new List<EntityIdPair>();
        var tags = new List<string>();

        var i = 0;
        while (i < parts.Length)
        {
            var current = parts[i];

            if (entityTypes.Contains(current) && i + 1 < parts.Length)
            {
                var id = parts[i + 1];
                entityPairs.Add(new EntityIdPair(current, id));
                segments.Add(new PathSegment(current, IsEntityType: true, EntityId: id));
                segments.Add(new PathSegment(id, IsEntityType: false, EntityId: null));
                i += 2;
            }
            else
            {
                segments.Add(new PathSegment(current, IsEntityType: false, EntityId: null));
                tags.Add(current);
                i++;
            }
        }

        // --- 5 & 6. Leaf + IsEntityPath -----------------------------------------
        var leaf = segments[^1].Value;
        var isEntity = entityPairs.Count > 0;

        return new ParsedPath(
            Segments: segments,
            EntityPairs: entityPairs,
            Tags: tags,
            RawPath: normalized,
            IsEntityPath: isEntity,
            LeafSegment: leaf);
    }

    // ---- private helpers -------------------------------------------------------

    /// <summary>
    /// Collapses consecutive slashes, ensures a leading <c>/</c>, and strips any
    /// trailing <c>/</c>.
    /// </summary>
    private static string Normalize(string path)
    {
        // Collapse consecutive '/' characters.
        var chars = new char[path.Length];
        var len = 0;
        var prevSlash = false;

        for (var j = 0; j < path.Length; j++)
        {
            var c = path[j];
            if (c == '/')
            {
                if (prevSlash) continue;
                prevSlash = true;
            }
            else
            {
                prevSlash = false;
            }

            chars[len++] = c;
        }

        var result = new string(chars, 0, len);

        // Ensure leading '/'.
        if (result.Length == 0 || result[0] != '/')
            result = "/" + result;

        // Strip trailing '/'.
        if (result.Length > 1 && result[^1] == '/')
            result = result[..^1];

        return result;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> contains any Unicode
    /// control character (categories Cc / Cf), excluding common whitespace that
    /// was already trimmed.
    /// </summary>
    private static bool ContainsControlChars(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
                return true;
        }

        return false;
    }
}
