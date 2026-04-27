using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class MetaJsonMergerTests : IDisposable
{
    private readonly string _tempDir;
    // Source-gen context in MetaJsonMerger uses PascalCase (default), so test data must match
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public MetaJsonMergerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-merge-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteMeta(string fileName, MetaEntry entry)
    {
        string path = Path.Combine(_tempDir, fileName);
        string json = JsonSerializer.Serialize(entry, JsonOptions);
        File.WriteAllText(path, json);
        return path;
    }

    private static MetaEntry? ReadMeta(string path)
    {
        return JsonSerializer.Deserialize<MetaEntry>(File.ReadAllText(path));
    }

    [Fact]
    public void Merge_HighJaccard_AutoMerges()
    {
        // ours and theirs share 5 of 6 keywords → Jaccard = 5/6 ≈ 0.833, above 0.7 threshold
        var ancestor = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "auth", "token", "api", "login", "user" }, "2026-01-01T00:00:00Z");
        var ours = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "auth", "token", "api", "login", "user", "session" }, "2026-03-01T00:00:00Z");
        var theirs = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "auth", "token", "api", "login", "user", "jwt" }, "2026-03-02T00:00:00Z");

        string ancestorPath = WriteMeta("ancestor.meta.json", ancestor);
        string oursPath = WriteMeta("ours.meta.json", ours);
        string theirsPath = WriteMeta("theirs.meta.json", theirs);

        var config = new MergeConfig(JaccardThreshold: 0.7);
        var result = MetaJsonMerger.Merge(ancestorPath, oursPath, theirsPath, config);

        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        // Read back merged result from ours path
        var merged = ReadMeta(oursPath);
        merged!.Keywords.Should().Contain("session");
        merged.Keywords.Should().Contain("jwt");
    }

    [Fact]
    public void Merge_LowJaccard_ReturnsConflict()
    {
        // Keywords with very low overlap → below threshold
        var ancestor = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "original" }, "2026-01-01T00:00:00Z");
        var ours = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "auth", "token", "api" }, "2026-03-01T00:00:00Z");
        var theirs = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "database", "migration", "schema" }, "2026-03-02T00:00:00Z");

        string ancestorPath = WriteMeta("ancestor.meta.json", ancestor);
        string oursPath = WriteMeta("ours.meta.json", ours);
        string theirsPath = WriteMeta("theirs.meta.json", theirs);

        var config = new MergeConfig(JaccardThreshold: 0.7);
        var result = MetaJsonMerger.Merge(ancestorPath, oursPath, theirsPath, config);

        result.Should().Be(MetaJsonMerger.MergeResult.Conflict);
    }

    [Fact]
    public void Merge_UnionKeywords_Sorted()
    {
        var ancestor = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "zebra", "alpha" }, "2026-01-01T00:00:00Z");
        var ours = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "zebra", "alpha", "delta" }, "2026-03-01T00:00:00Z");
        var theirs = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "zebra", "alpha", "beta" }, "2026-03-02T00:00:00Z");

        string ancestorPath = WriteMeta("ancestor.meta.json", ancestor);
        string oursPath = WriteMeta("ours.meta.json", ours);
        string theirsPath = WriteMeta("theirs.meta.json", theirs);

        var config = new MergeConfig(JaccardThreshold: 0.3);
        var result = MetaJsonMerger.Merge(ancestorPath, oursPath, theirsPath, config);

        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        var merged = ReadMeta(oursPath);
        merged!.Keywords.Should().BeInAscendingOrder();
        merged.Keywords.Should().BeEquivalentTo(new[] { "alpha", "beta", "delta", "zebra" });
    }

    [Fact]
    public void Merge_LatestUpdatedAt_Wins()
    {
        var ancestor = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "auth" }, "2026-01-01T00:00:00Z");
        var ours = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "auth" }, "2026-03-01T12:00:00Z");
        var theirs = new MetaEntry("test", "uri://test", 100, 1, "2026-01-01T00:00:00Z", "desc",
            new[] { "auth" }, "2026-03-15T08:00:00Z");

        string ancestorPath = WriteMeta("ancestor.meta.json", ancestor);
        string oursPath = WriteMeta("ours.meta.json", ours);
        string theirsPath = WriteMeta("theirs.meta.json", theirs);

        var config = new MergeConfig(JaccardThreshold: 0.5);
        var result = MetaJsonMerger.Merge(ancestorPath, oursPath, theirsPath, config);

        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        var merged = ReadMeta(oursPath);
        merged!.UpdatedAt.Should().Be("2026-03-15T08:00:00Z",
            because: "theirs has the later timestamp");
    }
}
