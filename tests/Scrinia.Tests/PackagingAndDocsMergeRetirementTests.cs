using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>Task 5 guard for retiring merge packaging and living documentation surfaces.</summary>
public sealed class PackagingAndDocsMergeRetirementTests
{
    private const string HistoricalMultiUserSetupSha256 =
        "e11853259a54c1870c8358a39780fd6d7efdd586dc81206375c38f7da5b28f99";

    private static readonly (string[] Path, string Sha256)[] HistoricalFiles =
    [
        (["docs", "superpowers", "plans", "2026-09-01-merge-retirement.md"],
            "e2bea104ff4ef825f9e2550965f4c292807077b4dc2d7d9e44f2e62115badcc0"),
        (["docs", "superpowers", "specs", "2026-08-31-shared-memory-global-tool-design.md"],
            "bac9b87c6acc942721e7078159dc13eb1811ca3cb9ed9be65870fcbc0245b6bb"),
        (["docs", "superpowers", "reviews", "2026-08-31-shared-memory-global-tool-spec-review.md"],
            "20e5385518a06bdf1cf9fe3a0dfe5a995de7421ee4638fd3740139bc9e13b57c"),
        (["CHANGELOG.md"], "3641e5f52e2fc5aa4b8e5e0d3ad001abf5cb230f2475fa703c7740f25d9b4f08"),
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
        const string heading = "## Manual Git conflict cleanup after merge retirement";

        troubleshooting.Should().Contain(heading);
        troubleshooting.Should().Contain("git -C \"<workspace-root>\" config --local --unset-all merge.scrinia-meta.driver");
        troubleshooting.Should().Contain("git -C \"<workspace-root>\" config --local --unset-all merge.scrinia-nmp2.driver");
        troubleshooting.Should().Contain("Scrinia does not execute them.");
        troubleshooting.Should().NotContain("scri-merge");
        troubleshooting.Should().NotContain("reconcile");
        troubleshooting.Should().NotContain("--multi-user");
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
