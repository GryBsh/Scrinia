namespace Scrinia.Core.Search;

/// <summary>
/// The single line-scanning rule shared by every mechanical prose scan in
/// <see cref="AttributeExtractor"/> and <see cref="ReferenceExtractor"/>: skip properly closed
/// fenced code blocks, keep everything else verbatim. YAML frontmatter and inline-code
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
    /// line, and yields every line that is not part of a real — opened AND closed — fenced block.
    /// Null or empty content yields nothing.
    /// </summary>
    /// <remarks>
    /// A ``` opener's fate is not decidable until its closer arrives, or doesn't, so lines after an
    /// opener are held back rather than dropped immediately. A matching closer means that block was
    /// a real fence — its lines, opener included, are simply never yielded, excluded as before.
    /// Reaching end-of-content still inside an opened fence means it never closed, so it was never a
    /// fence at all: an unterminated ``` is ordinary text like any other line, and this replays the
    /// OPENER LINE ITSELF followed by every line after it, verbatim and in the original order —
    /// exactly as if the ``` had never been recognized as a delimiter. (No other ``` line can appear
    /// in that replayed tail: the next one encountered while scanning would already have closed the
    /// fence and reset state, so an opener with no closer by EOF has nothing else to skip over.)
    /// <c>Split('\n')</c> already materializes every line up front, so the opener's index is tracked
    /// instead of copying lines into a separate buffer — no extra allocation for the common case of a
    /// properly closed fence.
    /// </remarks>
    public static IEnumerable<string> ProseLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
            yield break;

        string[] rawLines = content.Split('\n');
        bool inFence = false;
        int openerIndex = -1;

        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].TrimEnd('\r');

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                if (inFence)
                    openerIndex = i;
                continue;
            }

            if (inFence)
                continue;

            yield return line;
        }

        // EOF still inside an opened fence: it never closed, so replay the opener and everything
        // after it as ordinary prose.
        if (inFence)
        {
            for (int i = openerIndex; i < rawLines.Length; i++)
                yield return rawLines[i].TrimEnd('\r');
        }
    }
}
