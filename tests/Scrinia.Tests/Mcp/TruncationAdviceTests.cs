using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests.Mcp;

/// <summary>
/// Task 10, T10-B. <c>ResponseBuilder.DefaultTruncationAdvice</c> ("narrow with search ... or
/// recall a chunk") is meaningless for a caller with no query to narrow and no chunk to recall.
/// <c>search</c>, <c>recall</c> and <c>list</c> each set their own advice via
/// <see cref="ResponseBuilder.WithTruncationAdvice"/> at their own call sites, so the advice that
/// ships names the parameters the emitting tool actually has.
///
/// <para>Every test below drives real content past the response cap and reads the rendered
/// <c>followUp:</c> line — the mechanism under test is the whole pipeline
/// (Success/Warning → ContentTruncated → ToYaml), not just that a string constant exists.</para>
/// </summary>
public class TruncationAdviceTests
{
    private static ScriniaMcpTools Tools() => new();

    private const string DefaultAdviceFragment = "Narrow with search";

    // ── The mechanism itself (ResponseBuilder + ToYaml), independent of any one tool ─────────

    [Fact]
    public void Success_ContentUnderCap_NeverEmitsTruncationAdvice()
    {
        var response = ResponseBuilder.Success("short").WithTruncationAdvice("custom advice");

        response.ToYaml().Should().NotContain("custom advice",
            because: "WithTruncationAdvice must be a no-op until content actually truncates");
    }

    [Fact]
    public void Success_Truncated_WithNoAdviceSet_FallsBackToTheDefault()
    {
        string big = new string('x', ResponseBuilder.MaxResponseChars + 500);

        string yaml = ResponseBuilder.Success(big).ToYaml();

        yaml.Should().Contain(ResponseBuilder.DefaultTruncationAdvice);
    }

    [Fact]
    public void Success_Truncated_WithAdviceSet_UsesItInsteadOfTheDefault()
    {
        string big = new string('x', ResponseBuilder.MaxResponseChars + 500);

        string yaml = ResponseBuilder.Success(big).WithTruncationAdvice("tool-specific remedy").ToYaml();

        yaml.Should().Contain("tool-specific remedy");
        yaml.Should().NotContain(DefaultAdviceFragment,
            because: "a tool that set its own advice must not also carry the generic one");
    }

    // ── recall: real oversized content through the actual tool path ──────────────────────────

    [Fact]
    public async Task Recall_ContentOverCap_GetsRecallSpecificAdvice_NotTheDefault()
    {
        string original = new string('a', ResponseBuilder.MaxResponseChars + 1000);
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        var recalled = ResponseParser.Parse((await Tools().Recall(MemoryAddress.Parse(artifact))).ToYaml());

        recalled.FollowUp.Should().Contain(f => f.Contains("recall({ path, chunk", StringComparison.Ordinal));
        recalled.FollowUp.Should().NotContain(f => f.Contains(DefaultAdviceFragment, StringComparison.Ordinal));
    }

    // ── search: enough real matches to push the rendered body over the cap ───────────────────

    [Fact]
    public async Task Search_ResultsOverCap_GetsSearchSpecificAdvice_NotTheDefault()
    {
        using var scope = new TestHelpers.StoreScope();
        const string marker = "zzzsearchtruncationmarker";
        string filler = string.Concat(Enumerable.Repeat(
            "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november. ", 8));

        for (int i = 0; i < 18; i++)
            await Tools().Remember([$"{marker} entry {i}. {filler}"], MemoryAddress.Parse($"search-trunc-{i}"));

        var searched = ResponseParser.Parse((await Tools().Search(marker, limit: 20)).ToYaml());

        searched.FollowUp.Should().Contain(f => f.Contains("recall a specific hit", StringComparison.Ordinal));
        searched.FollowUp.Should().NotContain(f => f.Contains(DefaultAdviceFragment, StringComparison.Ordinal));
    }

    // ── list: enough entries in full-mode paging to push the table over the cap ──────────────

    [Fact]
    public async Task List_FullModeOverCap_GetsListSpecificAdvice_NotTheDefault()
    {
        using var scope = new TestHelpers.StoreScope();
        for (int i = 0; i < 90; i++)
            await Tools().Remember(
                [$"list truncation fixture entry number {i}. alpha bravo charlie delta echo foxtrot golf hotel."],
                MemoryAddress.Parse($"list-trunc-{i:D3}"));

        var listed = ResponseParser.Parse((await Tools().List(mode: "full", limit: 100)).ToYaml());

        listed.FollowUp.Should().Contain(f => f.Contains("mode: 'full'", StringComparison.Ordinal));
        listed.FollowUp.Should().NotContain(f => f.Contains(DefaultAdviceFragment, StringComparison.Ordinal));
    }
}
