using System.Security.Cryptography;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for ProjectTools.ScanStaleness and ProjectTools.ScanDrift helpers.
/// </summary>
public sealed class StalenessAndDriftTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly FileMemoryStore _store;

    public StalenessAndDriftTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "scrinia-sd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        _store = new FileMemoryStore(_workspaceDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { }
    }

    // ── ScanStaleness ────────────────────────────────────────────────────────

    [Fact]
    public void ScanStaleness_NoEntries_ReturnsZeros()
    {
        var (stale, review) = ScriniaProjectTools.ScanStaleness(_store);
        stale.Should().Be(0);
        review.Should().Be(0);
    }

    [Fact]
    public void ScanStaleness_NormalEntries_NotCounted()
    {
        _store.Upsert(new ArtifactEntry("normal1", "file://n1", 100, 1,
            DateTimeOffset.UtcNow, "A normal entry"));
        _store.Upsert(new ArtifactEntry("normal2", "file://n2", 200, 1,
            DateTimeOffset.UtcNow, "Another normal entry"));

        var (stale, review) = ScriniaProjectTools.ScanStaleness(_store);
        stale.Should().Be(0);
        review.Should().Be(0);
    }

    [Fact]
    public void ScanStaleness_PastReviewAfter_CountedAsStale()
    {
        _store.Upsert(new ArtifactEntry("stale1", "file://s1", 100, 1,
            DateTimeOffset.UtcNow, "Stale entry",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(-1)));
        _store.Upsert(new ArtifactEntry("stale2", "file://s2", 100, 1,
            DateTimeOffset.UtcNow, "Another stale entry",
            ReviewAfter: DateTimeOffset.UtcNow.AddHours(-1)));

        var (stale, review) = ScriniaProjectTools.ScanStaleness(_store);
        stale.Should().Be(2);
        review.Should().Be(0);
    }

    [Fact]
    public void ScanStaleness_FutureReviewAfter_NotCountedAsStale()
    {
        _store.Upsert(new ArtifactEntry("future", "file://f1", 100, 1,
            DateTimeOffset.UtcNow, "Not stale yet",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(30)));

        var (stale, review) = ScriniaProjectTools.ScanStaleness(_store);
        stale.Should().Be(0);
        review.Should().Be(0);
    }

    [Fact]
    public void ScanStaleness_ReviewWhenSet_CountedAsReview()
    {
        _store.Upsert(new ArtifactEntry("conditional", "file://c1", 100, 1,
            DateTimeOffset.UtcNow, "Needs review when auth changes",
            ReviewWhen: "when auth changes"));

        var (stale, review) = ScriniaProjectTools.ScanStaleness(_store);
        stale.Should().Be(0);
        review.Should().Be(1);
    }

    [Fact]
    public void ScanStaleness_BothReviewAfterAndReviewWhen_DateStaleTakesPrecedence()
    {
        // Entry is both date-stale AND has ReviewWhen — should count as stale only
        _store.Upsert(new ArtifactEntry("both", "file://b1", 100, 1,
            DateTimeOffset.UtcNow, "Both conditions met",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(-1),
            ReviewWhen: "when config changes"));

        var (stale, review) = ScriniaProjectTools.ScanStaleness(_store);
        stale.Should().Be(1, because: "date-stale entries are not double-counted as review");
        review.Should().Be(0);
    }

    [Fact]
    public void ScanStaleness_MixedEntries_CorrectCounts()
    {
        // 2 stale
        _store.Upsert(new ArtifactEntry("stale1", "file://s1", 100, 1,
            DateTimeOffset.UtcNow, "Stale 1",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(-5)));
        _store.Upsert(new ArtifactEntry("stale2", "file://s2", 100, 1,
            DateTimeOffset.UtcNow, "Stale 2",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(-1)));

        // 1 review
        _store.Upsert(new ArtifactEntry("review1", "file://r1", 100, 1,
            DateTimeOffset.UtcNow, "Review 1",
            ReviewWhen: "when API changes"));

        // 1 normal
        _store.Upsert(new ArtifactEntry("normal1", "file://n1", 100, 1,
            DateTimeOffset.UtcNow, "Normal 1"));

        var (stale, review) = ScriniaProjectTools.ScanStaleness(_store);
        stale.Should().Be(2);
        review.Should().Be(1);
    }

    [Fact]
    public void ScanStaleness_TopicScoped_CountsAllScopes()
    {
        // Entry in local scope
        _store.Upsert(new ArtifactEntry("local-entry", "file://l1", 100, 1,
            DateTimeOffset.UtcNow, "Local stale",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(-1)));

        // Entry in a topic scope
        _store.Upsert(
            new ArtifactEntry("topic-entry", "file://t1", 100, 1,
                DateTimeOffset.UtcNow, "Topic review",
                ReviewWhen: "when schema changes"),
            "local-topic:api");

        var (stale, review) = ScriniaProjectTools.ScanStaleness(_store);
        stale.Should().Be(1);
        review.Should().Be(1);
    }

    // ── ScanDrift ────────────────────────────────────────────────────────────

    [Fact]
    public void ScanDrift_NoEntries_ReturnsZeros()
    {
        var (drift, missing) = ScriniaProjectTools.ScanDrift(_store);
        drift.Should().Be(0);
        missing.Should().Be(0);
    }

    [Fact]
    public void ScanDrift_NoCodeRefs_ReturnsZeros()
    {
        _store.Upsert(new ArtifactEntry("no-refs", "file://nr1", 100, 1,
            DateTimeOffset.UtcNow, "No code refs"));

        var (drift, missing) = ScriniaProjectTools.ScanDrift(_store);
        drift.Should().Be(0);
        missing.Should().Be(0);
    }

    [Fact]
    public void ScanDrift_FileUnchanged_NoDrift()
    {
        // Create a tracked file in the workspace
        string relPath = "src/test-file.txt";
        string fullPath = Path.Combine(_workspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "hello world");
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)));

        _store.Upsert(new ArtifactEntry("tracked", "file://t1", 100, 1,
            DateTimeOffset.UtcNow, "Tracked file",
            CodeRefs: new Dictionary<string, string> { [relPath] = hash }));

        var (drift, missing) = ScriniaProjectTools.ScanDrift(_store);
        drift.Should().Be(0);
        missing.Should().Be(0);
    }

    [Fact]
    public void ScanDrift_FileModified_DetectedAsDrift()
    {
        string relPath = "src/drifted.txt";
        string fullPath = Path.Combine(_workspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "original content");
        string originalHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)));

        _store.Upsert(new ArtifactEntry("drifted", "file://d1", 100, 1,
            DateTimeOffset.UtcNow, "Will drift",
            CodeRefs: new Dictionary<string, string> { [relPath] = originalHash }));

        // Modify the file
        File.WriteAllText(fullPath, "modified content");

        var (drift, missing) = ScriniaProjectTools.ScanDrift(_store);
        drift.Should().Be(1);
        missing.Should().Be(0);
    }

    [Fact]
    public void ScanDrift_FileDeleted_DetectedAsMissing()
    {
        string relPath = "src/deleted.txt";
        string fullPath = Path.Combine(_workspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "will be deleted");
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)));

        _store.Upsert(new ArtifactEntry("deleted", "file://del1", 100, 1,
            DateTimeOffset.UtcNow, "Will be deleted",
            CodeRefs: new Dictionary<string, string> { [relPath] = hash }));

        // Delete the file
        File.Delete(fullPath);

        var (drift, missing) = ScriniaProjectTools.ScanDrift(_store);
        drift.Should().Be(0);
        missing.Should().Be(1);
    }

    [Fact]
    public void ScanDrift_MixedState_CorrectCounts()
    {
        // File 1: unchanged
        string okPath = "src/ok.txt";
        string okFull = Path.Combine(_workspaceDir, okPath);
        Directory.CreateDirectory(Path.GetDirectoryName(okFull)!);
        File.WriteAllText(okFull, "stable");
        string okHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(okFull)));

        // File 2: will be modified
        string driftPath = "src/drift.txt";
        string driftFull = Path.Combine(_workspaceDir, driftPath);
        File.WriteAllText(driftFull, "original");
        string driftHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(driftFull)));

        // File 3: will be deleted
        string missingPath = "src/missing.txt";
        string missingFull = Path.Combine(_workspaceDir, missingPath);
        File.WriteAllText(missingFull, "goodbye");
        string missingHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(missingFull)));

        _store.Upsert(new ArtifactEntry("multi-ref", "file://m1", 100, 1,
            DateTimeOffset.UtcNow, "Multiple refs",
            CodeRefs: new Dictionary<string, string>
            {
                [okPath] = okHash,
                [driftPath] = driftHash,
                [missingPath] = missingHash,
            }));

        // Modify one, delete another
        File.WriteAllText(driftFull, "changed!");
        File.Delete(missingFull);

        var (drift, missing) = ScriniaProjectTools.ScanDrift(_store);
        drift.Should().Be(1);
        missing.Should().Be(1);
    }

    [Fact]
    public void ScanDrift_NonexistentRef_DetectedAsMissing()
    {
        _store.Upsert(new ArtifactEntry("phantom", "file://p1", 100, 1,
            DateTimeOffset.UtcNow, "Points to nowhere",
            CodeRefs: new Dictionary<string, string>
            {
                ["src/does-not-exist.txt"] = "0000000000000000000000000000000000000000000000000000000000000000"
            }));

        var (drift, missing) = ScriniaProjectTools.ScanDrift(_store);
        drift.Should().Be(0);
        missing.Should().Be(1);
    }
}
