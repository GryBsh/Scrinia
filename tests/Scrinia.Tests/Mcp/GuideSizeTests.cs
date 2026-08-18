using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests.Mcp;

/// <summary>
/// Task 10, T10-A. <c>guide.md</c> is embedded at build time — bounded by construction, not by a
/// caller's query — so <c>guide()</c> is exempt from the response-size cap
/// (<see cref="ResponseBuilder.SuccessUntruncated"/>) rather than silently dropping its last few
/// dozen characters on every call, the way it did at 8046 chars against a 7992-char cap
/// (<c>ResponseBuilder.MaxResponseChars</c>(8192) - YamlOverhead(200)).
///
/// <para>Exempting the document from the cap only keeps its guarantee true if something still
/// bounds its growth — otherwise "bounded by construction" is an assumption, not a checked fact.
/// This file carries both halves: the document itself must stay under a ceiling, AND the tool
/// must actually be the one exempted (not some other path that happens not to truncate today).</para>
/// </summary>
public class GuideSizeTests
{
    private static ScriniaMcpTools Tools() => new();

    /// <summary>
    /// Twice <see cref="ResponseBuilder.MaxResponseChars"/> — deliberately generous headroom over
    /// today's 8046 chars (guide.md grows slowly and by hand, not by accretion of query results),
    /// but still finite, so a runaway edit (e.g. an accidental duplicate paste of the whole
    /// document) trips this rather than shipping silently. If guide.md ever needs to exceed this,
    /// that is a decision to make explicitly by raising the ceiling, not a silent truncation.
    /// </summary>
    private const int GuideCeiling = 2 * ResponseBuilder.MaxResponseChars;

    [Fact]
    public void GuideDocument_StaysUnderTheCeiling()
    {
        string guide = EmbeddedPrompts.LoadGuide()
            ?? throw new InvalidOperationException("Built-in guide not found in embedded resources");

        guide.Length.Should().BeLessThanOrEqualTo(GuideCeiling,
            because: "guide() is exempt from the per-response truncation cap, so nothing else " +
                     "stops this document from growing without bound except this test");
    }

    [Fact]
    public async Task Guide_ToolResponse_IsNeverTruncated()
    {
        using var scope = new TestHelpers.StoreScope();

        var guide = ResponseParser.Parse(await Tools().Guide());

        // The tail sentence guide.md actually loses today, character for character: at the
        // 7992-char cap the "... (truncated)" cut lands inside this exact final paragraph
        // (see /findings/guide-md-exceeds-response-cap). Asserting the true final words survive
        // pins the regression this fix closes without depending on byte-for-byte line-ending
        // fidelity through the YAML round trip — a separate, pre-existing quirk (CRLF content
        // combined with ToYaml's own Environment.NewLine-based AppendLine folds every blank line
        // into an extra line break once YamlDotNet re-normalizes it) that predates this branch and
        // affects every multi-line CRLF response, not just guide()'s. Recorded for later triage,
        // not fixed here — T10-A is about whether content is cut short, not how newlines round-trip.
        guide.Content.Should().Contain(
            "Scrinia writes to `.scrinia/`. Commit those changes — that is how memory travels with the code.",
            because: "this is the exact tail the 7992-char cap used to cut off mid-sentence");
        guide.Content.Should().NotContain("(truncated)",
            because: "guide() must return its document whole, not clipped to the response cap");
        guide.FollowUp.Should().NotContain(f => f.Contains("truncated", StringComparison.OrdinalIgnoreCase),
            because: "an untruncated response must not carry truncation advice");
    }
}
