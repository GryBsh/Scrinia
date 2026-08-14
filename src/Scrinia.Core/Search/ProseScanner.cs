namespace Scrinia.Core.Search;

/// <summary>
/// The single line-scanning rule shared by every mechanical prose scan in
/// <see cref="AttributeExtractor"/> (and, from Task 6, by fence-aware reference extraction):
/// skip fenced code blocks, keep everything else verbatim. YAML frontmatter and inline-code
/// spans are treated as prose, not stripped — both are deliberate. Frontmatter carries real
/// attributes (<c>status:</c>, <c>verdict:</c>, ...) that <see cref="AttributeExtractor.Extract"/>
/// is meant to read. Inline code is exactly the kind of place a real reference lives — a
/// backtick-quoted <c>[[wiki-link]]</c> or a <c>~ephemeral</c> path extracts today, and there is
/// no reason a fenced block gets that but an inline span does not.
/// </summary>
/// <remarks>
/// <para>Two gaps are inherited from the two loops this replaces, and both are deliberately left
/// rather than fixed here:</para>
/// <list type="bullet">
/// <item>Only ``` opens or closes a fence. A <c>~~~</c> fence is invisible to this scanner and its
/// contents are read as prose.</item>
/// <item>The fence test is <see cref="string.StartsWith(string, StringComparison)"/> with no
/// leading-whitespace trim, so a fence indented inside a list item or blockquote (<c>  ```</c>) is
/// not recognized as a fence at all — every line "inside" it, including the fences themselves, is
/// read as prose. In practice this gap matters more than the <c>~~~</c> one.</item>
/// </list>
/// <para>Both are existing behaviour, not new; widening either is a behaviour change with its own
/// blast radius, so this comment exists to make sure the next reader treats them as known rather
/// than rediscovers them as bugs.</para>
/// </remarks>
public static class ProseScanner
{
    /// <summary>
    /// Splits <paramref name="content"/> on <c>'\n'</c>, trims a trailing <c>'\r'</c> from each
    /// line, and yields every line that is not a ``` fence delimiter and not inside a fenced
    /// block. Null or empty content yields nothing. An unterminated fence (no closing
    /// ```) runs to the end of the content — the safe direction, since it excludes rather than
    /// includes whatever follows.
    /// </summary>
    public static IEnumerable<string> ProseLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
            yield break;

        bool inFence = false;
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
                continue;

            yield return line;
        }
    }
}
