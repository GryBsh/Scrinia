using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed partial class ArtifactEntrySerializationTests
{
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ArtifactEntry))]
    private partial class TestCtx : JsonSerializerContext;

    private static ArtifactEntry Sample(Dictionary<string, string[]>? attrs) => new(
        Name: "n", Uri: "mem://n", OriginalBytes: 10, ChunkCount: 1,
        CreatedAt: DateTimeOffset.UnixEpoch, Attributes: attrs);

    [Fact]
    public void Serializes_attributes_under_new_key_and_never_writes_fields()
    {
        var json = JsonSerializer.Serialize(
            Sample(new() { ["review"] = ["cond"] }), TestCtx.Default.ArtifactEntry);
        json.Should().Contain("\"attributes\":");
        json.Should().NotContain("\"fields\":");
    }

    [Fact]
    public void Reads_new_attributes_key()
    {
        const string json = """{"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,"createdAt":"1970-01-01T00:00:00+00:00","description":"d","attributes":{"tag":["x"]}}""";
        var e = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!.NormalizeAttributesKey();
        e.Attributes.Should().ContainKey("tag");
    }

    [Fact]
    public void Reads_legacy_fields_key_into_attributes()
    {
        const string json = """{"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,"createdAt":"1970-01-01T00:00:00+00:00","description":"d","fields":{"tag":["x"]}}""";
        var e = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!.NormalizeAttributesKey();
        e.Attributes.Should().ContainKey("tag");
    }

    [Fact]
    public void New_key_wins_when_both_present()
    {
        const string json = """{"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,"createdAt":"1970-01-01T00:00:00+00:00","description":"d","attributes":{"tag":["new"]},"fields":{"tag":["old"]}}""";
        var e = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!.NormalizeAttributesKey();
        e.Attributes!["tag"].Should().Equal("new");
    }

    [Fact]
    public void Normalized_entry_reserializes_without_fields_key()
    {
        const string json = """{"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,"createdAt":"1970-01-01T00:00:00+00:00","description":"d","fields":{"tag":["x"]}}""";
        var e = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!.NormalizeAttributesKey();
        var round = JsonSerializer.Serialize(e, TestCtx.Default.ArtifactEntry);
        round.Should().Contain("\"attributes\":");
        round.Should().NotContain("\"fields\":");
    }

    [Fact]
    public void SourceGen_resolves_ArtifactEntry_and_attributes_dictionary()
    {
        TestCtx.Default.GetTypeInfo(typeof(ArtifactEntry)).Should().NotBeNull();
        TestCtx.Default.GetTypeInfo(typeof(Dictionary<string, string[]>)).Should().NotBeNull(
            "attributes/legacy dict needs source-gen metadata for trimmed publishes");
    }

    /// <summary>
    /// Merge 3 Task 7 back-compat round-trip: a pre-removal sidecar on disk can still carry the
    /// now-deleted "description"/"tags"/"reviewAfter"/"reviewWhen"/"retrievalAnchors" JSON keys
    /// (their values already live in "attributes"/"contentPreview" post-migration) alongside the
    /// legacy "fields" key. STJ silently drops unmapped keys on read, so this must deserialize
    /// without throwing, and NormalizeAttributesKey() must still fold "fields" into Attributes —
    /// the removed keys are simply ignored, with no data corruption.
    /// </summary>
    [Fact]
    public void Reads_legacy_sidecar_with_all_removed_keys_and_fields_key_WithoutThrowing()
    {
        const string json = """
            {"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,
            "createdAt":"1970-01-01T00:00:00+00:00","description":"old desc",
            "tags":["old-tag"],"reviewAfter":"2026-01-01T00:00:00+00:00",
            "reviewWhen":"when old","retrievalAnchors":["old anchor"],
            "contentPreview":"kept preview","fields":{"tag":["from-fields"]}}
            """;

        var act = () => JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry);
        act.Should().NotThrow("STJ must silently drop the unmapped removed-member keys");

        var raw = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!;
        raw.ContentPreview.Should().Be("kept preview", "the surviving ContentPreview key must still deserialize");
        raw.LegacyFields.Should().NotBeNull("the legacy \"fields\" key must still populate LegacyFields pre-normalize");

        var normalized = raw.NormalizeAttributesKey();
        normalized.Attributes.Should().ContainKey("tag").WhoseValue.Should().Equal("from-fields");
        normalized.LegacyFields.Should().BeNull("normalization must clear the legacy holder");
    }
}
