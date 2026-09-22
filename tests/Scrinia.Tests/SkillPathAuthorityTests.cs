using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Skills resolve through PathRouter and nowhere else, and the legacy NMP/2 skill fallback
/// is gone. Before this change the location ".scrinia/skills/" was hardcoded at five sites
/// behind two divergent GetScriniaBaseDir implementations.
/// </summary>
public class SkillPathAuthorityTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task SkillWrite_LandsInThePathRouterDirectory()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await Tools().MemoryRemember(path: "/skill/api-reviewer", content: ["review the API"]);

        string expected = Path.Combine(
            PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot), "api-reviewer.md");
        File.Exists(expected).Should().BeTrue(
            because: "the writer must resolve through the single authority");
        (await File.ReadAllTextAsync(expected)).Should().Contain("review the API");
    }

    [Fact]
    public async Task SkillWrite_StaysOutOfTheMemoryStore()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await Tools().MemoryRemember(path: "/skill/api-reviewer", content: ["review the API"]);

        string memories = store.GetStoreDirForScope("local");
        Directory.Exists(Path.Combine(memories, "skill")).Should().BeFalse(
            because: "hand-authored file entities never enter a store scope directory");
    }

    [Fact]
    public async Task SkillLoad_LegacyNmp2Entry_IsNotFound()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        // Seed an NMP/2 entry under the legacy local:skill scope. Nothing may read it back:
        // the fallback is deleted, and such entries only exist in unreleased dev builds.
        string legacyScope = MemoryNaming.BuildScopedTopicScope("skill");
        await store.WriteArtifactAsync(
            "ghost", legacyScope, Scrinia.Core.Encoding.Nmp2ChunkedEncoder.Encode("legacy body"),
            ct: CancellationToken.None);

        string result = await Tools().MemoryRecall(path: "/skill/ghost");

        result.Should().NotContain("legacy body",
            because: "the local:skill NMP/2 read fallback is deleted");
    }

    [Fact]
    public async Task MemoryRecall_AutoPromotesAndDeletesLegacyLocalAgentEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        // A legacy local:agent entry with no sidecar — exactly what old builds stored.
        await store.WriteArtifactAsync("ghost", "local:agent", Nmp2ChunkedEncoder.Encode("legacy agent body"), ct: CancellationToken.None);

        string result = await Tools().MemoryRecall(path: "/agent/ghost");

        result.Should().Contain("legacy agent body",
            because: "the promoted file serves the legacy body");

        string published = Path.Combine(PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot), "ghost.md");
        File.Exists(published).Should().BeTrue(
            because: "promotion publishes the .md file entity to the agent directory");

        await FluentActions.Invoking(() =>
                store.ReadArtifactAsync(MemoryAddress.Parse("/agent/ghost"), CancellationToken.None))
            .Should().ThrowAsync<FileNotFoundException>(
            because: "promotion deletes the legacy NMP/2 entry after publishing");

        string second = await Tools().MemoryRecall(path: "/agent/ghost");
        second.Should().Contain("legacy agent body",
            because: "the second recall reads the published file on disk, not re-promote or re-delete");
    }

    [Fact]
    public async Task MemoryRecall_DiskHitDoesNotTouchLegacyLocalAgentEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        Directory.CreateDirectory(agentDir);
        string fileContent = "authoritative agent config";
        await File.WriteAllTextAsync(Path.Combine(agentDir, "ghost.md"), fileContent, CancellationToken.None);

        await store.WriteArtifactAsync("ghost", "local:agent", Nmp2ChunkedEncoder.Encode("legacy agent body"), ct: CancellationToken.None);

        string result = await Tools().MemoryRecall(path: "/agent/ghost");

        result.Should().Contain(fileContent,
            because: "the on-disk file is authoritative and wins over the legacy entry");
        result.Should().NotContain("legacy agent body");

        await FluentActions.Invoking(() =>
                store.ReadArtifactAsync(MemoryAddress.Parse("/agent/ghost"), CancellationToken.None))
            .Should().NotThrowAsync<FileNotFoundException>(
            because: "a disk hit never promotes or deletes the legacy entry");
    }

    [Fact]
    public async Task MemoryRecall_LegacyMissStillReturnsNotFound()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().MemoryRecall(path: "/agent/ghost");

        result.Should().Contain("NOT_FOUND",
            because: "with neither a file nor a legacy entry the not-found path is unchanged");
    }
}
