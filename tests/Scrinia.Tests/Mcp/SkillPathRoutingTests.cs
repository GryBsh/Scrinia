using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests.Mcp;

/// <summary>
/// The /skill/ namespace answers the same way through every tool that routes it.
/// Before this, remember and recall routed and forget did not, so forget said
/// NOT_FOUND for a skill recall had just returned — the namespace was write-only.
/// And remember silently dropped attributes/keywords after ~2 KB of schema prose
/// describing what they do.
/// </summary>
public class SkillPathRoutingTests
{
    private static ScriniaMcpTools Tools() => new();

    private static string AnyBuiltInSkill() =>
        EmbeddedPrompts.LoadAllSkills().Keys.OrderBy(k => k, StringComparer.Ordinal).First();

    [Fact]
    public async Task Forget_DeletesASkillThatRecallReturns()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().MemoryRemember(path: "/skill/temp-helper", content: ["do the thing"]);

        var before = ResponseParser.Parse(await Tools().MemoryRecall(path: "/skill/temp-helper"));
        before.Status.Should().NotBe("error", "the skill was just written");

        var forget = ResponseParser.Parse(await Tools().MemoryForget(path: "/skill/temp-helper"));
        forget.Status.Should().NotBe("error",
            "a namespace that remember and recall both route must not answer NOT_FOUND on forget");
        forget.Action.Should().Be("forgotten");

        var after = ResponseParser.Parse(await Tools().MemoryRecall(path: "/skill/temp-helper"));
        after.Status.Should().Be("error", "the skill file was deleted");
        after.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Forget_RemovesTheSkillFileAndItsSidecarFromDisk()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await Tools().MemoryRemember(path: "/skill/temp-helper", content: ["do the thing"]);
        string filePath = Path.Combine(
            PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot), "temp-helper.md");
        File.Exists(filePath).Should().BeTrue("precondition: remember wrote the skill");

        await Tools().MemoryForget(path: "/skill/temp-helper");

        File.Exists(filePath).Should().BeFalse("forget on a /skill/ path deletes the file");
        File.Exists(Path.ChangeExtension(filePath, ".meta.json")).Should().BeFalse(
            "the metadata sidecar must not outlive the skill it describes");
    }

    [Fact]
    public async Task Forget_OnABuiltInSkill_SaysSoRatherThanNotFound()
    {
        using var scope = new TestHelpers.StoreScope();
        string builtIn = AnyBuiltInSkill();

        var response = ResponseParser.Parse(await Tools().MemoryForget(path: $"/skill/{builtIn}"));

        response.Status.Should().Be("error");
        response.Error.Should().Contain("built-in",
            "NOT_FOUND is a false statement about a skill that demonstrably exists");
        response.ErrorCode.Should().NotBe(ErrorCodes.NotFound,
            "recall returns this skill, so forget must not claim it does not exist");
    }

    [Fact]
    public async Task Forget_OnAnOverrideOfABuiltIn_DeletesTheOverrideAndRestoresTheBuiltIn()
    {
        using var scope = new TestHelpers.StoreScope();
        string builtIn = AnyBuiltInSkill();

        await Tools().MemoryRemember(path: $"/skill/{builtIn}", content: ["project-specific override text"]);
        var shadowed = ResponseParser.Parse(await Tools().MemoryRecall(path: $"/skill/{builtIn}"));
        shadowed.Content.Should().Contain("project-specific override text",
            "precondition: the disk skill shadows the built-in");

        var forget = ResponseParser.Parse(await Tools().MemoryForget(path: $"/skill/{builtIn}"));
        forget.Status.Should().NotBe("error",
            "a disk override is a file the caller wrote, so the caller may delete it");

        var after = ResponseParser.Parse(await Tools().MemoryRecall(path: $"/skill/{builtIn}"));
        after.Status.Should().NotBe("error");
        after.Content.Should().NotContain("project-specific override text");
        after.Info.Should().Contain(i => i.Contains("built-in", StringComparison.OrdinalIgnoreCase),
            "deleting the override falls back to the built-in rather than leaving a hole");
    }

    [Fact]
    public async Task Forget_OnAnUnknownSkill_IsStillNotFound()
    {
        using var scope = new TestHelpers.StoreScope();

        var response = ResponseParser.Parse(await Tools().MemoryForget(path: "/skill/no-such-skill"));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Forget_OnTheBareSkillPath_AsksForANameRatherThanDeletingAnything()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "/skill/keep-me", content: ["instructions"]);

        var response = ResponseParser.Parse(await Tools().MemoryForget(path: "/skill/"));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.MalformedAddress,
            "the typed address parser rejects a nameless reserved path before skill routing");
        ResponseParser.Parse(await Tools().MemoryRecall(path: "/skill/keep-me"))
            .Status.Should().NotBe("error", "a nameless forget must not delete the namespace");
    }

    [Fact]
    public async Task Remember_OnASkillPath_DropsAttributesAndKeywords()
    {
        using var scope = new TestHelpers.StoreScope();

        string yaml = await Tools().MemoryRemember(
            path: "/skill/noted",
            content: ["instructions"],
            attributes: new Dictionary<string, string[]> { ["topic"] = ["x"] },
            keywords: ["alpha"]);
        var response = ResponseParser.Parse(yaml);

        response.Status.Should().NotBe("error");
        response.Info.Should().BeEmpty("reserved skill writes do not report ignored general-memory fields");
    }

    [Fact]
    public async Task Remember_OnASkillPath_DoesNotReportIgnoredKeywords()
    {
        using var scope = new TestHelpers.StoreScope();

        var response = ResponseParser.Parse(await Tools().MemoryRemember(
            path: "/skill/half-noted",
            content: ["instructions"],
            keywords: ["alpha"]));

        response.Info.Should().BeEmpty("reserved skill writes do not report ignored general-memory fields");
    }

    [Fact]
    public async Task Remember_OnASkillPath_WithoutThoseArguments_DoesNotWarn()
    {
        using var scope = new TestHelpers.StoreScope();

        var response = ResponseParser.Parse(
            await Tools().MemoryRemember(path: "/skill/quiet", content: ["instructions"]));

        response.Status.Should().NotBe("error");
        response.Info.Should().BeEmpty(
            "a warning on every skill write would train the caller to ignore warnings");
    }
}
