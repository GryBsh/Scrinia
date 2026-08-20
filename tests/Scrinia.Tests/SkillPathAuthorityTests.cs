using FluentAssertions;
using Scrinia.Core;
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
}
