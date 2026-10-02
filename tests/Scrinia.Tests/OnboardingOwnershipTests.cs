using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// Phase 4, Task 5 — independent disjointness gate. The shared MCP surface stays
/// prompt-isolated (no prompt or correlation field), the Phase 4 edits introduce no Git
/// process or spawn into the owned surfaces, and the four dated historical documents from
/// the Phase 6 guard remain byte-for-byte unchanged.
/// </summary>
public sealed class OnboardingOwnershipTests
{
    private const string HistoricalChangelogSha256 =
        "1dce5eb7fdf8e817bd5d0a6509f297a548e0fa17a2dc2ff551546ccd93f1403e";

    private static readonly (string[] Path, string Sha256)[] HistoricalFiles =
    [
        (["docs", "superpowers", "plans", "2026-09-01-merge-retirement.md"],
            "272df673359ca1087ab2fdb995a30055c1e681a4b614a95b282dc82b6780dd13"),
        (["docs", "superpowers", "specs", "2026-08-31-shared-memory-global-tool-design.md"],
            "f61e2fbfe3b268cbd0097ec3a7efb2ba191d0c12f8895c618631776dba555f8a"),
        (["docs", "superpowers", "reviews", "2026-08-31-shared-memory-global-tool-spec-review.md"],
            "3d1364da2278ee7652debcbd88de33b8c6e4a0ebb86ab7344344b10568f6f00a"),
    ];

    private static readonly string[] OwnedPhase4Surfaces =
    [
        "README.md",
        "docs/getting-started.md",
        "docs/security.md",
        "docs/multi-user-setup.md",
    ];

    [Fact]
    public void SharedMcpSurface_HasNoPromptOrCorrelationField()
    {
        // The shared MCP server instructions are the canonical shared surface (spec §7.3): it
        // must never expose a prompt or correlation field to any handler.
        string instructions = ScriniaPrimer.McpInstructions;
        instructions.Should().NotContain("prompt", "the shared MCP surface takes no initiating prompt");
        instructions.Should().NotContain("correlation", "the shared MCP surface has no correlation field");
    }

    [Fact]
    public void OwnedPhase4Surfaces_ContainNoGitProcessOrSpawn()
    {
        foreach (string relative in OwnedPhase4Surfaces)
        {
            string text = Read(relative);
            text.Should().NotContain("spawn", $"Phase 4 edits to {relative} never launch a process");
            text.Should().NotContain("child_process", $"Phase 4 edits to {relative} never spawn a child process");
            text.Should().NotContain("Process.Start", $"Phase 4 edits to {relative} never start a Git process");
            text.Should().NotContain("scri-merge", $"Phase 4 edits to {relative} never reference the retired scri-merge binary");
        }
    }

    [Fact]
    public void DatedHistoricalDocuments_RemainByteIdentical()
    {
        foreach (var (pathSegments, expectedSha256) in HistoricalFiles)
        {
            string path = RepoPaths.Combine(pathSegments);
            byte[] bytes = File.ReadAllBytes(path);
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(
                expectedSha256, $"dated historical document {Path.Combine(pathSegments)} must not be rewritten");
        }

        byte[] changelog = File.ReadAllBytes(RepoPaths.Combine("CHANGELOG.md"));
        const string firstDatedEntry = "## [0.6.0] — 2026-08-27";
        byte[] marker = Encoding.UTF8.GetBytes(firstDatedEntry);
        int historicalStart = FindBytes(changelog, marker);

        historicalStart.Should().BeGreaterThanOrEqualTo(0,
            "the changelog must retain a dated historical section after the current release section");
        Convert.ToHexString(SHA256.HashData(changelog[historicalStart..])).ToLowerInvariant().Should().Be(
            HistoricalChangelogSha256, "dated changelog entries must remain byte-identical");
    }

    private static int FindBytes(byte[] source, byte[] value)
    {
        for (int start = 0; start <= source.Length - value.Length; start++)
        {
            if (source.AsSpan(start, value.Length).SequenceEqual(value))
            {
                return start;
            }
        }

        return -1;
    }

    private static string Read(string relative) =>
        File.ReadAllText(RepoPaths.Combine(relative), Encoding.UTF8);
}
