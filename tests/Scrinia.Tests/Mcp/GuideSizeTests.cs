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
    /// An explicit editorial ceiling in characters, independent of ordinary tool-response limits.
    /// Raise deliberately when the guide needs more room; never truncate essential guidance.
    /// </summary>
    private const int GuideCeiling = 32_768;

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

        // Compare the entire document, including blank lines and line endings.
        guide.Content.Should().NotBeNull();
        guide.Content.Should().Be(EmbeddedPrompts.LoadGuide(),
            because: "every line of the embedded guide must survive the tool response");
        guide.Content.Should().NotContain("(truncated)",
            because: "guide() must return its document whole, not clipped to the response cap");
        guide.FollowUp.Should().NotContain(f => f.Contains("truncated", StringComparison.OrdinalIgnoreCase),
            because: "an untruncated response must not carry truncation advice");
    }

}
