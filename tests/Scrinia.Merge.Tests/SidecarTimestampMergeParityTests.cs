using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The timestamp rule exists twice and must pick once. These cases include the two the old
/// implementations disagreed about: equal instants written in different offsets, and a createdAt
/// that differs between sides.
///
/// <para><b>What this cannot see.</b> Core is handed <c>string?</c>s and the driver hand-written
/// JSON — two inputs, not one — and <c>reconcile()</c> is absent. reconcile reaches this same rule
/// for the merged pair, but selects its BASE NODE with a bare
/// <c>DateTimeOffset.TryParse(oursUpdated, …)</c> (<c>MemoryTools.Lifecycle.cs:617-618</c>) — no
/// <c>InvariantCulture</c>, no <c>AssumeUniversal|AdjustToUniversal</c> — which is a THIRD parsing
/// rule, and the one that decides which side's unmerged fields survive. Nothing here runs it.
/// <c>Scrinia.Tests.MetaMergeSymmetricParityTests</c> drives one input through both mergers.</para>
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

    /// <summary>
    /// A <c>createdAt</c> that is present but not a JSON string (here, a Unix-epoch number — a
    /// hand-edited or foreign sidecar) is MALFORMED, not ABSENT. Plain <c>GetString</c> cannot tell
    /// the two apart and would silently treat this as absent, letting theirs' value win with no
    /// conflict raised — while reconcile()'s auto-resolver refuses the same input through
    /// <c>MemoryTools.Lifecycle.TryReadTimestampString</c>, which checks PRESENCE before inspecting
    /// the value. (Not, as an earlier version of this comment claimed, an exception thrown inside
    /// <c>JsonNode.GetValue&lt;string&gt;()</c>: that mechanism was true only for a non-string value
    /// like this one and false for an explicit JSON null, which is why it was replaced —
    /// <c>MetaJsonMerger.TryGetTimestampString</c> spells the correction out.) The driver must
    /// refuse too, so the two mergers agree on malformed input the same way they now agree on
    /// well-formed timestamps.
    /// </summary>
    [Fact]
    public void MalformedCreatedAt_NonStringValue_RefusesTheMerge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ts-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string ours = Path.Combine(dir, "ours.meta.json");
            string theirs = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null, null));
            File.WriteAllText(ours,
                "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1," +
                "\"keywords\":[\"auth\"],\"createdAt\":1735689600,\"updatedAt\":\"2026-01-02T00:00:00Z\"}");
            File.WriteAllText(theirs, Sidecar("2026-01-01T00:00:00Z", "2026-01-03T00:00:00Z"));

            MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: "createdAt is present but not a JSON string — the driver cannot safely " +
                             "interpret one side's intent, so it must escalate rather than silently " +
                             "treat it as absent and let theirs' value win unchallenged");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Regression pin for fix round 2. An explicit <c>"createdAt": null</c> is a distinct shape
    /// from an absent key: <c>TryGetPropertyValue</c> returns true for a present-but-null key, and
    /// the value is then not a <c>JsonValue</c> at all (a JSON null is a null node reference), so
    /// <c>TryGetTimestampString</c> already reports MALFORMED for it — this was true before fix
    /// round 2 too. What fix round 2 actually closed was reconcile()'s side reading the identical
    /// input as ABSENT (via the <c>node?[key]</c> indexer, which cannot distinguish "absent" from
    /// "present but null") and silently auto-resolving. This test exists so the driver's half of
    /// that parity is pinned explicitly rather than only asserted in the fix-round report.
    /// </summary>
    [Fact]
    public void MalformedCreatedAt_ExplicitNull_RefusesTheMerge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ts-null-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string ours = Path.Combine(dir, "ours.meta.json");
            string theirs = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null, null));
            File.WriteAllText(ours,
                "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1," +
                "\"keywords\":[\"auth\"],\"createdAt\":null,\"updatedAt\":\"2026-01-02T00:00:00Z\"}");
            File.WriteAllText(theirs, Sidecar("2026-01-01T00:00:00Z", "2026-01-03T00:00:00Z"));

            MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: "createdAt: null is present but not a JSON string — the driver cannot " +
                             "safely interpret one side's intent, so it must escalate rather than " +
                             "silently treat it as absent and let theirs' value win unchallenged");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
