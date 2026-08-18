using FluentAssertions;
using Scrinia.Mcp;
using Xunit;

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
        // course of aligning the sections.
        guide.Content.Should().Contain("Scrinia");
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

        restore.Status.Should().NotBe("error");
    }
}
