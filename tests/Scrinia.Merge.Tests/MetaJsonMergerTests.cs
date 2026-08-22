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

    /// <summary>
    /// <c>agentKeywords</c> is unioned with <c>keywords</c>, not preserved from ours like the other
    /// unknown fields. It NAMES members of the keyword list, so taking ours whole while the union
    /// pulls in theirs leaves a branch's curated keyword recorded as auto-extracted — and a
    /// compaction reads exactly this field to decide what keeps agent weight. A field that
    /// cross-references another cannot ride along as an opaque blob.
    /// </summary>
    [Fact]
    public void Merge_UnionsAgentKeywordsAlongsideKeywords()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json");
        string ours = Path.Combine(dir, "ours.meta.json");
        string theirs = Path.Combine(dir, "theirs.meta.json");

        File.WriteAllText(a, """{"name":"m","keywords":["auth","jwt"],"agentKeywords":["auth"]}""");
        File.WriteAllText(ours, """{"name":"m","keywords":["auth","jwt","local"],"agentKeywords":["auth","local"]}""");
        File.WriteAllText(theirs, """{"name":"m","keywords":["auth","jwt","remote"],"agentKeywords":["auth","remote"]}""");

        MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig(JaccardThreshold: 0.5))
            .Should().Be(MetaJsonMerger.MergeResult.Resolved);

        using var doc = JsonDocument.Parse(File.ReadAllText(ours));
        doc.RootElement.GetProperty("agentKeywords").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["auth", "local", "remote"],
                because: "both branches' curated keywords survived into the merged keyword list, " +
                         "so both must survive into the record of which ones are curated");
    }

    /// <summary>
    /// ONE side pre-provenance is the case that matters most, and it must come out UNKNOWN.
    ///
    /// <para>The merged keyword list holds both sides' curated keywords, but only one side can say
    /// which of its own were curated. Keeping that side's list looks like "the union of what is
    /// known" and is in fact a positive claim about the whole merged list: every keyword not in it
    /// was auto-extracted. Readers act on that — <c>Compact</c> runs its recovery heuristic only
    /// when the field is null — so the unrecorded side's curated keywords are demoted +5 → +2 on
    /// the next content rewrite, permanently, with nothing left to recover from. Under-claiming a
    /// SET is benign; flipping unknown to known is the one thing the null state forbids.</para>
    ///
    /// <para>Asserted in both directions: whichever side is "ours" is an accident of which branch
    /// the merge runs on, and this is exactly the migration window the null state was designed
    /// for — one machine upgraded, one not.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Merge_LeavesAgentKeywordsAbsentWhenOnlyOneSideRecordedIt(bool unrecordedSideIsOurs)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json");
        string ours = Path.Combine(dir, "ours.meta.json");
        string theirs = Path.Combine(dir, "theirs.meta.json");

        const string Unrecorded = """{"name":"m","keywords":["auth","jwt","portcullis"]}""";
        const string Recorded = """{"name":"m","keywords":["auth","jwt","gatehouse"],"agentKeywords":["gatehouse"]}""";

        File.WriteAllText(a, """{"name":"m","keywords":["auth","jwt"]}""");
        File.WriteAllText(ours, unrecordedSideIsOurs ? Unrecorded : Recorded);
        File.WriteAllText(theirs, unrecordedSideIsOurs ? Recorded : Unrecorded);

        MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig(JaccardThreshold: 0.5))
            .Should().Be(MetaJsonMerger.MergeResult.Resolved);

        using var doc = JsonDocument.Parse(File.ReadAllText(ours));
        doc.RootElement.GetProperty("keywords").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("portcullis",
                because: "precondition: the unrecorded side's curated keyword survives the union, " +
                         "so there is something for a wrong provenance record to mislabel");
        doc.RootElement.TryGetProperty("agentKeywords", out _).Should().BeFalse(
            because: "the merged entry genuinely does not know which of its keywords were " +
                     "curated, and recording a half-answer as a whole one cannot be undone");
    }

    /// <summary>Two pre-provenance sidecars merge to a pre-provenance sidecar. Emitting an empty
    /// array would assert "nothing was curated" for a memory where nobody wrote it down, which is
    /// the one claim the null state exists to avoid making.</summary>
    [Fact]
    public void Merge_LeavesAgentKeywordsAbsentWhenNeitherSideRecordedIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json");
        string ours = Path.Combine(dir, "ours.meta.json");
        string theirs = Path.Combine(dir, "theirs.meta.json");

        File.WriteAllText(a, """{"name":"m","keywords":["auth"]}""");
        File.WriteAllText(ours, """{"name":"m","keywords":["auth","jwt"]}""");
        File.WriteAllText(theirs, """{"name":"m","keywords":["auth","oauth"]}""");

        MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig(JaccardThreshold: 0.3))
            .Should().Be(MetaJsonMerger.MergeResult.Resolved);

        using var doc = JsonDocument.Parse(File.ReadAllText(ours));
        doc.RootElement.TryGetProperty("agentKeywords", out _).Should().BeFalse();
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

    [Fact]
    public void Merge_OriginalBytesExceedsIntMaxValue_IsPreserved()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json"), ours = Path.Combine(dir, "ours.meta.json"), theirs = Path.Combine(dir, "theirs.meta.json");
        // originalBytes exceeds int.MaxValue (2,147,483,647) — must survive the merge as a long.
        string json = """{"name":"m","keywords":["auth","jwt"],"originalBytes":3000000000}""";
        File.WriteAllText(a, json);
        File.WriteAllText(ours, json);
        File.WriteAllText(theirs, json);

        var result = MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig());
        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        using var doc = JsonDocument.Parse(File.ReadAllText(ours));
        doc.RootElement.GetProperty("originalBytes").GetInt64().Should().Be(3_000_000_000L);
    }

    [Fact]
    public void Merge_BothSidesNoKeywords_OmitsKeywordsProperty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json"), ours = Path.Combine(dir, "ours.meta.json"), theirs = Path.Combine(dir, "theirs.meta.json");
        string json = """{"name":"m","keywords":[]}""";
        File.WriteAllText(a, json);
        File.WriteAllText(ours, json);
        File.WriteAllText(theirs, json);

        var result = MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig());
        result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

        using var doc = JsonDocument.Parse(File.ReadAllText(ours));
        doc.RootElement.TryGetProperty("keywords", out _).Should().BeFalse();
    }

    /// <summary>
    /// Different schema versions on the two sides ESCALATE rather than auto-resolve.
    ///
    /// <para>This resolver overlays merged fields onto OURS and writes ours, so theirs' version
    /// and theirs' unknown keys never reach the result. Machine A on a newer build writes v:2 and
    /// fields this build has no member for; machine B edits the same memory and merges. A silent
    /// resolve would hand back a sidecar with no version and none of A's data, which B's store
    /// would then rewrite freely. Cross-machine merge is the designed workflow.</para>
    /// </summary>
    [Fact]
    public void Merge_SidesDeclaringDifferentSchemaVersions_Conflicts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json"), ours = Path.Combine(dir, "ours.meta.json"), theirs = Path.Combine(dir, "theirs.meta.json");
        string oursJson = """{"name":"m","keywords":["auth","jwt"]}""";
        File.WriteAllText(a, oursJson);
        File.WriteAllText(ours, oursJson);
        File.WriteAllText(theirs, """{"name":"m","keywords":["auth","jwt"],"v":2}""");

        MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig())
            .Should().Be(MetaJsonMerger.MergeResult.Conflict);
        File.ReadAllText(ours).Should().Be(oursJson);
    }

    /// <summary>
    /// A key only THEIRS carries, that neither this driver nor ArtifactEntry models, survives the
    /// merge. Without this the driver silently deleted the other machine's data on every
    /// auto-resolve — the exact loss the sidecar extension bucket exists to prevent, one layer up.
    /// </summary>
    [Fact]
    public void Merge_PreservesAnUnknownKeyThatOnlyTheirsCarries()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json"), ours = Path.Combine(dir, "ours.meta.json"), theirs = Path.Combine(dir, "theirs.meta.json");
        File.WriteAllText(a, """{"name":"m","keywords":["auth","jwt"]}""");
        File.WriteAllText(ours, """{"name":"m","keywords":["auth","jwt"],"contentPreview":"OURS"}""");
        File.WriteAllText(theirs, """{"name":"m","keywords":["auth","jwt"],"futureThing":{"a":1}}""");

        MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig())
            .Should().Be(MetaJsonMerger.MergeResult.Resolved);

        // The VALUE, not just the key: `ours[kv.Key] = null` would satisfy a Contain check while
        // still having thrown the other machine's data away.
        using var merged = JsonDocument.Parse(File.ReadAllText(ours));
        merged.RootElement.GetProperty("futureThing").GetProperty("a").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// The control for the test above: a key the merge logic deliberately REMOVES must stay
    /// removed. A first version of the preservation copied every theirs-only key and resurrected
    /// exactly these deliberate deletions, breaking four tests.
    /// </summary>
    [Fact]
    public void Merge_DoesNotResurrectAModelledKeyTheMergeLogicRemoved()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.meta.json"), ours = Path.Combine(dir, "ours.meta.json"), theirs = Path.Combine(dir, "theirs.meta.json");
        File.WriteAllText(a, """{"name":"m","keywords":["auth","jwt"]}""");
        File.WriteAllText(ours, """{"name":"m","keywords":["auth","jwt"]}""");
        File.WriteAllText(theirs, """{"name":"m","keywords":["auth","jwt"],"agentKeywords":["auth"]}""");

        MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig())
            .Should().Be(MetaJsonMerger.MergeResult.Resolved);

        File.ReadAllText(ours).Should().NotContain("agentKeywords",
            "one-sided agentKeywords is deliberately left absent; the unknown-key copy must not " +
            "reach back in and undo that");
    }
}
