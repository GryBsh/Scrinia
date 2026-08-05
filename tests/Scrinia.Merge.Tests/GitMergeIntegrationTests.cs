using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

[CollectionDefinition(GitMergeCollection.Name, DisableParallelization = true)]
public sealed class GitMergeCollection
{
    public const string Name = "git-merge-integration";
}

/// <summary>
/// Drives real `git merge` against the built scri-merge binary. Each case spawns processes,
/// so the collection is serialised.
/// </summary>
[Collection(GitMergeCollection.Name)]
public sealed class GitMergeIntegrationTests
{
    private const string Artifact = ".scrinia/memories/local/auth-flow.nmp2";
    private const string Sidecar = ".scrinia/memories/local/auth-flow.meta.json";
    private const string NmpAttributes = "*.meta.json merge=scrinia-meta\n*.nmp2 merge=scrinia-nmp2\n";

    private static string Nmp2(string payload) =>
        $"NMP/2 {payload.Length}B CRC32:00000000 BR+B64 C:1\n##CHUNK:1\n{payload}\n";

    [Fact]
    public void OneSideUnchanged_GitResolvesWithoutInvokingTheDriver()
    {
        using var repo = new GitMergeHarness();
        // A driver that always fails. If git invokes it the merge cannot succeed, so a clean
        // merge is proof that git resolved the path itself by blob OID at the tree level.
        repo.RegisterRawDriver("nmp2", "exit 9");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), oursContent: null, theirsContent: Nmp2("dGhlaXJz"));

        var result = repo.Merge("other");

        result.ExitCode.Should().Be(0,
            because: "when one side is unchanged git never invokes the merge driver");
        repo.ReadFile(Artifact).Should().Contain("dGhlaXJz",
            because: "the side that changed wins");
    }

    [Fact]
    public void MetaDriver_WithSimilarKeywords_AutoResolves()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("meta", passPathname: false);
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.SeedThreeWay(
            Sidecar,
            baseContent: Meta(["a", "b", "c", "d"], "2026-01-01T00:00:00+00:00"),
            oursContent: Meta(["a", "b", "c", "d", "shared"], "2026-02-01T00:00:00+00:00"),
            theirsContent: Meta(["a", "b", "c", "d", "shared"], "2026-03-01T00:00:00+00:00"));

        var result = repo.Merge("other");

        result.ExitCode.Should().Be(0,
            because: "keyword sets above the Jaccard threshold auto-merge");
        string merged = repo.ReadFile(Sidecar);
        merged.Should().Contain("shared");
        merged.Should().Contain("2026-03-01",
            because: "the later updatedAt wins");
    }

    private static string Meta(string[] keywords, string updatedAt)
    {
        string kw = string.Join(",", keywords.Select(k => $"\"{k}\""));
        return $"{{\"name\":\"auth-flow\",\"keywords\":[{kw}],\"originalBytes\":10," +
               $"\"chunkCount\":1,\"updatedAt\":\"{updatedAt}\"}}\n";
    }
}
