using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>Task 5 guard for retiring merge packaging and living documentation surfaces.</summary>
public sealed class PackagingAndDocsMergeRetirementTests
{
    private const string HistoricalMultiUserSetupSha256 =
        "4ffb120fe214463e82bfe95257346b5ac37c2f79d724954f1ea3df74cd7cdd82";

    private static readonly (string[] Path, string Sha256)[] HistoricalFiles =
    [
        (["docs", "superpowers", "plans", "2026-09-01-merge-retirement.md"],
            "272df673359ca1087ab2fdb995a30055c1e681a4b614a95b282dc82b6780dd13"),
        (["docs", "superpowers", "specs", "2026-08-31-shared-memory-global-tool-design.md"],
            "f61e2fbfe3b268cbd0097ec3a7efb2ba191d0c12f8895c618631776dba555f8a"),
        (["docs", "superpowers", "reviews", "2026-08-31-shared-memory-global-tool-spec-review.md"],
            "3d1364da2278ee7652debcbd88de33b8c6e4a0ebb86ab7344344b10568f6f00a"),
        (["CHANGELOG.md"], "2c5b75cad9600c32418223d67e684f942a0c1bd757e747969ebcdc7aebf31b1e"),
    ];

    [Fact]
    public void PackagingManifestAndReleaseWorkflow_ContainNoMergeSurface()
    {
        string manifest = Read("publish.projects.tsv");
        string workflow = Read(".github", "workflows", "release.yml");
        string powershell = Read("publish.ps1");
        string shell = Read("publish.sh");

        foreach (string source in new[] { manifest, workflow, powershell, shell })
        {
            source.Should().NotContain("scri-merge");
            source.Should().NotContain("Scrinia.Merge");
        }

        workflow.Should().NotContain("merge driver");
    }

    [Fact]
    public void LivingDocumentation_ContainsNoRetiredActiveSurface()
    {
        string cli = Read("docs", "cli-reference.md");
        string gettingStarted = Read("docs", "getting-started.md");
        string mergeSafety = RepoPaths.Combine("src", "Scrinia.Mcp", "skills", "merge-safety.md");

        foreach (string source in new[] { cli, gettingStarted })
        {
            source.Should().NotContain("reconcile");
            source.Should().NotContain("--multi-user");
            source.Should().NotContain("merge-safety");
            source.Should().NotContain("merge driver");
        }

        File.Exists(mergeSafety).Should().BeFalse("the active merge-safety skill is retired");
    }

    [Fact]
    public void Troubleshooting_ProvidesManualGuidanceOnly()
    {
        string troubleshooting = Read("docs", "troubleshooting.md");
        const string heading = "## Manual retired merge-driver cleanup and conflict resolution";
        string section = troubleshooting[troubleshooting.IndexOf(heading, StringComparison.Ordinal)..];

        section.Should().Contain(heading);
        section.Should().Contain("git -C \"<workspace-root>\" config --local --unset-all merge.scrinia-meta.driver");
        section.Should().Contain("git -C \"<workspace-root>\" config --local --unset-all merge.scrinia-nmp2.driver");
        section.Should().Contain("Scrinia does not execute them.");
        section.Should().Contain("resolve the conflicted files manually");
        section.Should().NotContain("scri-merge");
        section.Should().NotContain("reconcile");
        section.Should().NotContain("--multi-user");
        section.Should().NotContain("shared");
        section.Should().NotContain("serialize");
        section.Should().NotContain("staging");
        section.Should().NotContain("stage");
        section.Should().NotContain("commit");
    }

    [Fact]
    public void Phase4MultiUserDocumentation_RemainsByteIdentical()
    {
        string path = RepoPaths.Combine("docs", "multi-user-setup.md");
        byte[] bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            .Should().Be(HistoricalMultiUserSetupSha256);
    }

    [Fact]
    public void HistoricalPlanningAndReleaseFiles_RemainByteIdentical()
    {
        foreach (var (pathSegments, expectedHash) in HistoricalFiles)
        {
            string path = RepoPaths.Combine(pathSegments);
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                .Should().Be(expectedHash, $"historical file {Path.Combine(pathSegments)} must not be rewritten");
        }
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(RepoPaths.Combine(segments), Encoding.UTF8);
}
