using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The structural guarantee this workstream exists to establish: .scrinia/memories/ holds
/// NMP/2 store content and nothing else, so a store scope directory can never contain a
/// foreign .meta.json for SaveIndex's orphan sweep to delete.
/// </summary>
public class StorePurityInvariantTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task MemoriesDirectory_NeverContainsFileEntities()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        await Tools().MemoryRemember(path: "/skill/qa", content: ["skill body"]);
        await Tools().MemoryRemember(path: "/agent/profile", content: ["agent body"]);
        await Tools().MemoryRemember(path: "/findings/note", content: ["ordinary memory"]);

        string memories = store.GetStoreDirForScope("local");
        Directory.EnumerateFiles(memories, "*.md", SearchOption.AllDirectories)
            .Should().BeEmpty(because: "hand-authored Markdown never enters the store");
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public void FileEntityDirectories_SitOutsideTheStore(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        string dir = PathRouter.ToFileEntityDir(kind, store.WorkspaceRoot);
        string memories = store.GetStoreDirForScope("local");

        PathContainment.IsWithin(memories, dir).Should().BeFalse(
            because: $"{kind} is a hand-authored file entity, not store content");
    }

    [Fact]
    public async Task SkillWriteAndRecallResolveToThePathRouterDirectory()
    {
        // The DRY guarantee made executable for the skill kind: ".scrinia/skills/" was
        // previously named at five sites behind two divergent GetScriniaBaseDir
        // implementations that agreed only by coincidence. Writing through the MCP surface
        // and finding the file where PathRouter says it should be proves writer and
        // authority agree; the listing and the load prove the two read paths agree too.
        //
        // Named for the skill kind specifically (not "every reader"): the agent kind's
        // write/recall round-trip is already covered by AgentFileEntityTests, which also
        // exercises the orphan-sweep regression agent's move was made to prevent — adding it
        // here would duplicate that coverage rather than extend it.
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        await Tools().MemoryRemember(path: "/skill/qa", content: ["skill body"]);

        string authoritative = Path.Combine(
            PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot), "qa.md");
        File.Exists(authoritative).Should().BeTrue();

        string listing = await Tools().MemoryRecall(path: "/skill/");
        listing.Should().Contain("qa");

        string loaded = await Tools().MemoryRecall(path: "/skill/qa");
        loaded.Should().Contain("skill body");
    }
}
