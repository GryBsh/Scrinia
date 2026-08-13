using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The timestamp rule exists twice and must pick once. These cases include the two the old
/// implementations disagreed about: equal instants written in different offsets, and a createdAt
/// that differs between sides.
/// </summary>
public sealed class SidecarTimestampMergeParityTests
{
    public static TheoryData<string, string?, string?, string?, string?> Cases() => new()
    {
        // name                      oursCreated               oursUpdated               theirsCreated             theirsUpdated
        { "ours older, newer update", "2026-01-01T00:00:00Z",  "2026-03-01T00:00:00Z",   "2026-02-01T00:00:00Z",   "2026-02-05T00:00:00Z" },
        { "theirs older",             "2026-02-01T00:00:00Z",  "2026-02-05T00:00:00Z",   "2026-01-01T00:00:00Z",   "2026-03-01T00:00:00Z" },
        { "identical",                "2026-01-01T00:00:00Z",  "2026-02-01T00:00:00Z",   "2026-01-01T00:00:00Z",   "2026-02-01T00:00:00Z" },
        { "same instant, different offset", "2026-01-01T10:00:00+10:00", "2026-02-01T00:00:00Z", "2026-01-01T00:00:00Z", "2026-02-01T00:00:00Z" },
        { "ours missing created",     null,                    "2026-02-01T00:00:00Z",   "2026-01-01T00:00:00Z",   "2026-02-01T00:00:00Z" },
        { "theirs missing updated",   "2026-01-01T00:00:00Z",  "2026-02-01T00:00:00Z",   "2026-01-01T00:00:00Z",   null },
        { "both missing updated",     "2026-01-01T00:00:00Z",  null,                     "2026-01-01T00:00:00Z",   null },
        { "unparseable ours",         "not-a-date",            "2026-02-01T00:00:00Z",   "2026-01-01T00:00:00Z",   "2026-02-01T00:00:00Z" },
        { "unparseable both",         "not-a-date",            "also-not",               "still-not",              "nope" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(
        string caseName, string? oursCreated, string? oursUpdated,
        string? theirsCreated, string? theirsUpdated)
    {
        var expected = SidecarTimestampMerge.Select(oursCreated, oursUpdated, theirsCreated, theirsUpdated);
        var actual = ThroughTheMergeDriver(oursCreated, oursUpdated, theirsCreated, theirsUpdated);

        actual.CreatedAt.Should().Be(expected.CreatedAt,
            because: $"[{caseName}] createdAt must be the EARLIEST of the two — a merge that moves " +
                     "it forward claims the memory did not exist during a period it did");
        actual.UpdatedAt.Should().Be(expected.UpdatedAt,
            because: $"[{caseName}] updatedAt must be the latest of the two");
    }

    private static (string? CreatedAt, string? UpdatedAt) ThroughTheMergeDriver(
        string? oursCreated, string? oursUpdated, string? theirsCreated, string? theirsUpdated)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ts-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string ours = Path.Combine(dir, "ours.meta.json");
            string theirs = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null, null));
            File.WriteAllText(ours, Sidecar(oursCreated, oursUpdated));
            File.WriteAllText(theirs, Sidecar(theirsCreated, theirsUpdated));

            MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Resolved);

            using var doc = JsonDocument.Parse(File.ReadAllText(ours));
            return (ReadString(doc.RootElement, "createdAt"), ReadString(doc.RootElement, "updatedAt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Sidecar(string? createdAt, string? updatedAt)
    {
        var fields = new List<string>
        {
            "\"name\":\"m\"", "\"uri\":\"\"", "\"originalBytes\":100", "\"chunkCount\":1",
            "\"keywords\":[\"auth\"]",
        };
        if (createdAt is not null) fields.Add($"\"createdAt\":{JsonSerializer.Serialize(createdAt)}");
        if (updatedAt is not null) fields.Add($"\"updatedAt\":{JsonSerializer.Serialize(updatedAt)}");
        return "{" + string.Join(",", fields) + "}";
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
