using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="ScriniaCommands.Consolidate"/>'s staleness-count logic — specifically
/// that it reads review status via <c>AttributeReaders.GetReviewStatus</c> (attributes-first)
/// rather than the (now-removed) legacy ReviewAfter member directly.
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class ConsolidateTests : IDisposable
{
    private readonly string _tempDir;

    public ConsolidateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_consolidate_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>Runs the command with --json while capturing stdout, then deserializes the payload.</summary>
    private static async Task<CliConsolidateOutput> RunConsolidateJson(ScriniaCommands cmd, string workspaceRoot)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        int exitCode;
        try
        {
            exitCode = await cmd.Consolidate(workspaceRoot: workspaceRoot, dryRun: true, json: true);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        exitCode.Should().Be(0);
        string output = writer.ToString().Trim();
        return JsonSerializer.Deserialize(output, CliJsonContext.Default.CliConsolidateOutput)!;
    }

    [Fact]
    public async Task Consolidate_ReviewOnlyInAttributes_CountsAsStale()
    {
        // Arrange: seed an entry whose review lives ONLY in Attributes["review"] — the legacy
        // ReviewAfter/ReviewWhen members no longer exist on the model at all. Before the
        // AttributeReaders cutover, Consolidate's stale count read the legacy member directly
        // and would see null, undercounting this entry as not-stale.
        string pastReview = DateTimeOffset.UtcNow.AddDays(-1).ToString("o", CultureInfo.InvariantCulture);
        using (var seedStore = new FileMemoryStore(_tempDir))
        {
            seedStore.Upsert(
                new ArtifactEntry(
                    Name: "attrs-only-review", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
                    CreatedAt: DateTimeOffset.UnixEpoch,
                    Attributes: new Dictionary<string, string[]> { ["review"] = [pastReview] }),
                "local");
        }

        // Act
        var cmd = new ScriniaCommands();
        var result = await RunConsolidateJson(cmd, _tempDir);

        // Assert: the entry is counted stale via Attributes["review"], not the null legacy member.
        result.TotalMemories.Should().Be(1);
        result.StaleCount.Should().Be(1, because: "the review date lives in Attributes[\"review\"]; " +
            "Consolidate must read it via AttributeReaders.GetReviewStatus, not the legacy ReviewAfter member");
    }
}
