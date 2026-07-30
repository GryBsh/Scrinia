using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class MetaJsonMergerTests : IDisposable
{
    private readonly string _tempDir;

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

    // Writes a real-shape camelCase .meta.json sidecar (the on-disk format MetaJsonMerger reads).
    private string WriteMeta(string fileName, string name, string uri, int originalBytes, int chunkCount,
        string createdAt, string description, string[] keywords, string updatedAt)
    {
        var obj = new JsonObject
        {
            ["name"] = name,
            ["uri"] = uri,
            ["originalBytes"] = originalBytes,
            ["chunkCount"] = chunkCount,
            ["createdAt"] = createdAt,
            ["description"] = description,
            ["keywords"] = new JsonArray(keywords.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["updatedAt"] = updatedAt,
        };
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, obj.ToJsonString());
        return path;
    }

    private static JsonElement ReadMeta(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Merge_HighJaccard_AutoMerges()
    {
        // ours and theirs share 5 of 6 keywords → Jaccard = 5/6 ≈ 0.833, above 0.7 threshold
        string ancestorPath = WriteMeta("ancestor.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["auth", "token", "api", "login", "user"], "2026-01-01T00:00:00Z");
        string oursPath = WriteMeta("ours.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["auth", "token", "api", "login", "user", "session"], "2026-03-01T00:00:00Z");
        string theirsPath = WriteMeta("theirs.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["auth", "token", "api", "login", "user", "jwt"], "2026-03-02T00:00:00Z");

        var config = new MergeConfig(JaccardThreshold: 0.7);
        var result = MetaJsonMerger.Merge(ancestorPath, oursPath, theirsPath, config);

        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        var merged = ReadMeta(oursPath);
        var keywords = merged.GetProperty("keywords").EnumerateArray().Select(e => e.GetString()).ToArray();
        keywords.Should().Contain("session");
        keywords.Should().Contain("jwt");
    }

    [Fact]
    public void Merge_LowJaccard_ReturnsConflict()
    {
        // Keywords with very low overlap → below threshold
        string ancestorPath = WriteMeta("ancestor.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["original"], "2026-01-01T00:00:00Z");
        string oursPath = WriteMeta("ours.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["auth", "token", "api"], "2026-03-01T00:00:00Z");
        string theirsPath = WriteMeta("theirs.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["database", "migration", "schema"], "2026-03-02T00:00:00Z");

        var config = new MergeConfig(JaccardThreshold: 0.7);
        var result = MetaJsonMerger.Merge(ancestorPath, oursPath, theirsPath, config);

        result.Should().Be(MetaJsonMerger.MergeResult.Conflict);
    }

    [Fact]
    public void Merge_UnionKeywords_Sorted()
    {
        string ancestorPath = WriteMeta("ancestor.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["zebra", "alpha"], "2026-01-01T00:00:00Z");
        string oursPath = WriteMeta("ours.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["zebra", "alpha", "delta"], "2026-03-01T00:00:00Z");
        string theirsPath = WriteMeta("theirs.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["zebra", "alpha", "beta"], "2026-03-02T00:00:00Z");

        var config = new MergeConfig(JaccardThreshold: 0.3);
        var result = MetaJsonMerger.Merge(ancestorPath, oursPath, theirsPath, config);

        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        var merged = ReadMeta(oursPath);
        var keywords = merged.GetProperty("keywords").EnumerateArray().Select(e => e.GetString()).ToArray();
        keywords.Should().BeInAscendingOrder();
        keywords.Should().BeEquivalentTo(new[] { "alpha", "beta", "delta", "zebra" });
    }

    [Fact]
    public void Merge_LatestUpdatedAt_Wins()
    {
        string ancestorPath = WriteMeta("ancestor.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["auth"], "2026-01-01T00:00:00Z");
        string oursPath = WriteMeta("ours.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["auth"], "2026-03-01T12:00:00Z");
        string theirsPath = WriteMeta("theirs.meta.json", "test", "uri://test", 100, 1,
            "2026-01-01T00:00:00Z", "desc", ["auth"], "2026-03-15T08:00:00Z");

        var config = new MergeConfig(JaccardThreshold: 0.5);
        var result = MetaJsonMerger.Merge(ancestorPath, oursPath, theirsPath, config);

        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        var merged = ReadMeta(oursPath);
        merged.GetProperty("updatedAt").GetString().Should().Be("2026-03-15T08:00:00Z",
            because: "theirs has the later timestamp");
    }

    [Fact]
    public void Merge_PreservesUnknownFields_AndUnionsKeywords()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json");
        string ours = Path.Combine(dir, "ours.meta.json");
        string theirs = Path.Combine(dir, "theirs.meta.json");

        // Real-shape camelCase sidecars with fields the old MetaEntry never modeled.
        string oursJson = """
        {"name":"m","uri":"","originalBytes":100,"chunkCount":1,"keywords":["auth","jwt"],
         "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z",
         "contentPreview":"PRESERVE ME","termFrequencies":{"auth":3},"attributes":{"topic":["x"]}}
        """;
        string theirsJson = """
        {"name":"m","uri":"","originalBytes":100,"chunkCount":1,"keywords":["auth","jwt","oauth"],
         "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-03T00:00:00Z",
         "contentPreview":"PRESERVE ME","termFrequencies":{"auth":3},"attributes":{"topic":["x"]}}
        """;
        File.WriteAllText(a, oursJson);       // ancestor == ours (only theirs changed keywords)
        File.WriteAllText(ours, oursJson);
        File.WriteAllText(theirs, theirsJson);

        var result = MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig(JaccardThreshold: 0.5));
        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        using var doc = JsonDocument.Parse(File.ReadAllText(ours));
        var root = doc.RootElement;
        // Unknown fields preserved verbatim:
        root.GetProperty("contentPreview").GetString().Should().Be("PRESERVE ME");
        root.GetProperty("termFrequencies").GetProperty("auth").GetInt32().Should().Be(3);
        root.GetProperty("attributes").GetProperty("topic")[0].GetString().Should().Be("x");
        // Keywords unioned:
        root.GetProperty("keywords").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["auth", "jwt", "oauth"]);
        // updatedAt = latest:
        root.GetProperty("updatedAt").GetString().Should().Be("2026-01-03T00:00:00Z");
    }

    [Fact]
    public void Merge_DissimilarKeywords_IsConflict_FileUntouched()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json"), ours = Path.Combine(dir, "ours.meta.json"), theirs = Path.Combine(dir, "theirs.meta.json");
        string oursJson = """{"name":"m","keywords":["auth","jwt"],"contentPreview":"OURS"}""";
        string theirsJson = """{"name":"m","keywords":["billing","invoice"],"contentPreview":"THEIRS"}""";
        File.WriteAllText(a, """{"name":"m","keywords":[]}"""); File.WriteAllText(ours, oursJson); File.WriteAllText(theirs, theirsJson);

        MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig()).Should().Be(MetaJsonMerger.MergeResult.Conflict);
        File.ReadAllText(ours).Should().Be(oursJson); // untouched
    }
}
