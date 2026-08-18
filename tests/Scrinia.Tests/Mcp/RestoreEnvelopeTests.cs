using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests.Mcp;

public class RestoreEnvelopeTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task GuideAndRestore_PutTheRestoreContextInTheSameField()
    {
        using var scope = new TestHelpers.StoreScope();

        var guide = ResponseParser.Parse(await Tools().Guide());
        var restore = ResponseParser.Parse(await Tools().MemoryRestore());

        // The skills list is the payload that visibly swapped fields. It is emitted
        // unconditionally from BuiltInSkills (Lifecycle.cs:1257), so no fixture setup
        // is needed to make it appear.
        guide.Info.Should().Contain(i => i.Contains("Skills available", StringComparison.Ordinal));
        restore.Info.Should().Contain(i => i.Contains("Skills available", StringComparison.Ordinal),
            "the same payload from a sibling tool must arrive under the same field");
    }

    [Fact]
    public async Task Guide_KeepsTheGuideDocumentAsItsContent()
    {
        using var scope = new TestHelpers.StoreScope();

        var guide = ResponseParser.Parse(await Tools().Guide());

        // Guarded because the fix must not move the guide text out of content: in the
        // course of aligning the sections. Matches the guide's actual heading rather than a
        // bare "Scrinia" substring, which Success("Scrinia") alone would also satisfy.
        guide.Content.Should().Contain("# Scrinia — Agent Memory Guide");
    }

    [Fact]
    public async Task Restore_ContentIsASummaryLine_NotTheSections()
    {
        using var scope = new TestHelpers.StoreScope();

        var restore = ResponseParser.Parse(await Tools().MemoryRestore());

        restore.Content.Should().NotContain("Skills available");
        restore.Content.Should().NotBeNullOrWhiteSpace(
            "an empty content: field is a worse envelope than the one being replaced");
    }

    [Fact]
    public async Task Restore_StillReportsItsAction()
    {
        using var scope = new TestHelpers.StoreScope();

        var restore = ResponseParser.Parse(await Tools().MemoryRestore());

        // Pins the actual action value, not just "not an error" — a response mutated to report
        // the wrong action (e.g. "guide" instead of "restored") would still pass a NotBe("error")
        // check, and did, until this assertion was tightened.
        restore.Action.Should().Be("restored");
        restore.Status.Should().NotBe("error");
    }

    [Fact]
    public async Task Restore_SummaryLine_ReportsCountsThatVary_NotAConstantSectionCount()
    {
        using var scope = new TestHelpers.StoreScope();

        var restore = ResponseParser.Parse(await Tools().MemoryRestore());

        // A fresh local workspace has no follow-up items or warnings yet, but BuiltInSkills is
        // always non-empty, so ContentSections.Count is always exactly 1 regardless of whether
        // anything was actually found. The summary must report the counts that actually vary
        // (follow-up items, warnings), not that constant section count.
        //
        // This pins the "something was found" shape. The complementary "nothing was found" shape
        // is pinned in RemoteStoreToolTests.Guide_And_Restore_ReportNoStoredContext_OnARemoteStore
        // — a remote store is the only reachable path where every local-only section, including
        // the always-non-empty built-in skills list, is skipped, so ContentSections genuinely
        // ends up empty before the fallback fires.
        restore.Content.Should().Be("Restored workspace context: 0 follow-up items, 0 warnings. See info for details.");
    }
}
