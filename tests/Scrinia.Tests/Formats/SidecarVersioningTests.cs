using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Formats;

public sealed partial class SidecarVersioningTests
{
    // Mirrors FileStoreJsonContext's options exactly; the store's context is private. A
    // hand-built JsonSerializerOptions (reflection-based) would NOT exercise the constraint that
    // Unknown must be `{ get; set; }` rather than `{ get; init; }` — reflection-based binding
    // does not go through the same deserialization-constructor path that source generation does,
    // so a reflection test would let that requirement ship untested. Source generation is the
    // whole point of this suite.
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ArtifactEntry))]
    private partial class SidecarTestCtx : JsonSerializerContext;

    [Fact]
    public void UnknownKey_SurvivesReadThenWrite()
    {
        const string json = """
        {"name":"seed","uri":"file:///seed.nmp2","originalBytes":5,"chunkCount":1,
         "createdAt":"2026-01-01T00:00:00+00:00","futureThing":{"a":1}}
        """;

        var entry = JsonSerializer.Deserialize(json, SidecarTestCtx.Default.ArtifactEntry)!;
        entry.Unknown.Should().ContainKey("futureThing");

        JsonSerializer.Serialize(entry, SidecarTestCtx.Default.ArtifactEntry).Should().Contain("futureThing");
    }

    [Fact]
    public void Description_TheLiveMergeDriverInstance_SurvivesReadThenWrite()
    {
        // MetaJsonMerger three-way-merges a "description" key that ArtifactEntry has had no
        // member for since the description -> contentPreview rename. Today the merge driver
        // preserves it and the store deletes it on the next read-write. This is that bug.
        const string json = """
        {"name":"seed","uri":"file:///seed.nmp2","originalBytes":5,"chunkCount":1,
         "createdAt":"2026-01-01T00:00:00+00:00","description":"preserved by the merge driver"}
        """;

        var entry = JsonSerializer.Deserialize(json, SidecarTestCtx.Default.ArtifactEntry)!;
        JsonSerializer.Serialize(entry, SidecarTestCtx.Default.ArtifactEntry)
            .Should().Contain("preserved by the merge driver");
    }

    [Fact]
    public void SchemaVersion_RoundTrips()
    {
        const string json = """
        {"name":"seed","uri":"file:///seed.nmp2","originalBytes":5,"chunkCount":1,
         "createdAt":"2026-01-01T00:00:00+00:00","v":2}
        """;

        JsonSerializer.Deserialize(json, SidecarTestCtx.Default.ArtifactEntry)!.SchemaVersion.Should().Be(2);
    }

    [Fact]
    public void V1Entry_EmitsNoVersionKey()
    {
        // The whole no-churn property. If this fails, adopting versioning rewrites every
        // sidecar in every user's store through the merge driver for zero information.
        var entry = new ArtifactEntry("seed", "file:///seed.nmp2", 5, 1, DateTimeOffset.UtcNow);

        entry.SchemaVersion.Should().BeNull();
        JsonSerializer.Serialize(entry, SidecarTestCtx.Default.ArtifactEntry).Should().NotContain("\"v\"");
    }

    [Fact]
    public void EntryWithNoUnknowns_EmitsNoExtraKeys()
    {
        var entry = new ArtifactEntry("seed", "file:///seed.nmp2", 5, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var reparsed = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(entry, SidecarTestCtx.Default.ArtifactEntry));

        reparsed.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("name", "uri", "originalBytes", "chunkCount", "createdAt");
    }

    /// <summary>
    /// Unknown keys are sorted with <see cref="StringComparer.Ordinal"/> on write, alongside the
    /// Keywords / TermFrequencies / FileHashes sorting WriteSidecarCore already does for stable
    /// git diffs.
    ///
    /// <para><b>This test distinguishes the two comparers, rather than merely asserting "sorted".</b>
    /// The three keys are chosen so the expected order is UNREACHABLE under OrdinalIgnoreCase:
    /// ordinal puts every uppercase letter before every lowercase one, giving Alpha, alpha, zeta;
    /// case-insensitive compares Alpha and alpha as equal and yields Alpha/alpha in input order
    /// followed by zeta, so "alpha" before "Alpha" survives. A test using only lowercase keys
    /// would pass under both comparers and pin nothing.</para>
    ///
    /// <para>Ordinal is correct because these keys come from a schema this build cannot reason
    /// about, and JSON keys are case-sensitive: "Alpha" and "alpha" are two different keys, and a
    /// comparer that calls them equal makes their relative order depend on dictionary insertion
    /// order, which is exactly the churn the sorting exists to prevent.</para>
    /// </summary>
    [Fact]
    public void UnknownKeys_AreSortedOrdinally_OnWrite()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        var entry = new ArtifactEntry("sorted", "file:///sorted.nmp2", 5, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
        {
            Unknown = new Dictionary<string, JsonElement>
            {
                ["zeta"] = JsonDocument.Parse("1").RootElement,
                ["alpha"] = JsonDocument.Parse("2").RootElement,
                ["Alpha"] = JsonDocument.Parse("3").RootElement,
            }
        };

        store.Upsert(entry, "local:notes");

        // Read the RAW sidecar text off disk: key ORDER is a property of the bytes, and any
        // assertion that goes back through a Dictionary loses it.
        string raw = File.ReadAllText(
            Path.Combine(scope.TempDir, "notes", "sorted.meta.json"));

        var keys = JsonDocument.Parse(raw).RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => n is "zeta" or "alpha" or "Alpha")
            .ToList();

        keys.Should().Equal("Alpha", "alpha", "zeta");
    }

    // ── Read path: a newer sidecar loads, and forward-fill leaves it alone ──────────────

    [Fact]
    public async Task NewerSidecar_IsNotDroppedFromTheIndex()
    {
        // The C1-class regression guard. If the version gate is evaluated INSIDE
        // ReadSidecarRaw's blanket `catch { return null; }`, a newer sidecar is silently
        // dropped from the index -- which is the previous Critical's failure mode, rebuilt
        // by hand inside the fix for this one.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("ordinary", "local:notes", ["body"]));

        WriteRawSidecar(scope, "local:notes", "from-the-future", schemaVersion: 2);

        ((FileMemoryStore)store).InvalidateCaches();   // NOT on IMemoryStore -- cast required
        store.LoadIndex("local:notes").Select(e => e.Name)
            .Should().Contain("from-the-future");
    }

    [Fact]
    public void NewerSidecar_IsStillReadable_WithItsUnknownFieldsIntact()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        WriteRawSidecar(scope, "local:notes", "from-the-future",
            schemaVersion: 2, extraKey: "futureThing");

        store.LoadIndex("local:notes").Single(e => e.Name == "from-the-future")
            .Unknown.Should().ContainKey("futureThing");
    }

    [Fact]
    public void ForwardFill_DoesNotRewriteANewerSidecar_AndDoesNotThrow()
    {
        // The sidecar MUST be legacy-shaped (a "fields" key rather than "attributes") so
        // AttributeMigration.MigratesTo returns true. That is the only condition under which
        // the forward-fill loop reaches a write at all -- without it this test passes
        // vacuously and proves nothing.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        string path = WriteRawSidecar(scope, "local:notes", "legacy-and-newer",
            schemaVersion: 2, legacyShaped: true);

        byte[] before = File.ReadAllBytes(path);

        var act = () => store.LoadIndex("local:notes");
        // NOTE: this half of the assertion is dormant until the write-path refusal lands --
        // nothing throws ScopeReadOnlyException yet, so it cannot currently fail. The
        // byte-equality assertion below is what carries this test today. Kept because the
        // moment a refusal exists, this is the line that catches it escaping a plain read.
        act.Should().NotThrow("a version refusal must never escape a plain read path");

        File.ReadAllBytes(path).Should().Equal(before);
    }

    [Fact]
    public void ForwardFill_StillRewritesALegacySidecarAtTheCurrentVersion()
    {
        // The control for the test above: prove the skip is conditional on the VERSION, not
        // on the migration having quietly stopped working.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        string path = WriteRawSidecar(scope, "local:notes", "legacy-only",
            schemaVersion: null, legacyShaped: true);

        byte[] before = File.ReadAllBytes(path);
        store.LoadIndex("local:notes");

        File.ReadAllBytes(path).Should().NotEqual(before);
    }

    [Fact]
    public async Task CorruptSidecar_IsStillSkipped_NotRefused()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("ordinary", "local:notes", ["body"]));
        File.WriteAllText(SidecarPath(scope, "local:notes", "broken"), "{ not json");

        store.LoadIndex("local:notes").Select(e => e.Name).Should().Contain("ordinary");
    }

    // ── Write path: inheritance on overwrite ─────────────────────────────────────────────
    //
    // Task 2's round-trip tests above prove one OBJECT round-trips. No ordinary write is a
    // round-trip: StoreAsync/AppendAsync re-derive everything and hand BuildDerivedEntry a
    // brand-new ArtifactEntry. Without `prior` threaded through, the first `remember` on an
    // existing memory silently deletes both SchemaVersion and Unknown — and every test above
    // stays green while it happens. These tests go through the real write path with entirely
    // new content, which is the only thing that can catch that.

    [Fact]
    public async Task ReStoringAMemory_WithEntirelyNewContent_KeepsItsUnknownKeys()
    {
        // NOT a round-trip: StoreAsync re-derives and builds a NEW entry. This is the test that
        // separates a working extension bucket from a decorative one.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        AddUnknownKeyToSidecarOnDisk(scope, "local:notes", "seed", "futureThing");

        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["totally new content"]));

        ReadSidecarJson(scope, "local:notes", "seed").Should().Contain("futureThing");
    }

    [Fact]
    public async Task AppendingToAMemory_KeepsItsUnknownKeys()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        AddUnknownKeyToSidecarOnDisk(scope, "local:notes", "seed", "futureThing");

        await store.AppendAsync(new MemoryWrite("seed", "local:notes", ["second"]));

        ReadSidecarJson(scope, "local:notes", "seed").Should().Contain("futureThing");
    }

    [Fact]
    public async Task ReStoringAMemory_WithEntirelyNewContent_KeepsItsSchemaVersion()
    {
        // The discrimination-proof partner to the Unknown-keys test above, exercised without
        // depending on Task 4's write-path refusal (nothing throws ScopeReadOnlyException yet —
        // see ReStoringAV2Memory_IsRefused below). WriteSidecarCore has no version gate today,
        // so the overwrite succeeds; what this pins is that the version it writes is the PRIOR
        // entry's, not null.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        SetSidecarVersionOnDisk(scope, "local:notes", "seed", 2);

        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["totally new content"]));

        ReadSidecarSchemaVersion(scope, "local:notes", "seed").Should().Be(2);
    }

    [Fact]
    public async Task AppendingToAMemory_KeepsItsSchemaVersion()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        SetSidecarVersionOnDisk(scope, "local:notes", "seed", 2);

        await store.AppendAsync(new MemoryWrite("seed", "local:notes", ["second"]));

        ReadSidecarSchemaVersion(scope, "local:notes", "seed").Should().Be(2);
    }

    [Fact(Skip = "activates with Task 4's write gate — nothing throws ScopeReadOnlyException " +
                 "from a write path yet, so this cannot pass until Task 4 lands the refusal.")]
    public async Task ReStoringAV2Memory_IsRefused_BecauseTheRebuiltEntryInheritsItsVersion()
    {
        // This is the mechanism that makes a PER-ENTRY gate sufficient in place of a whole-scope
        // lockout: the refusal follows the memory, because the version is inherited. Task 3b
        // (this task) only wires the inheritance; ReStoringAMemory_..._KeepsItsSchemaVersion
        // above is the enabled proof that the inheritance itself works. This test documents the
        // consequence Task 4 is expected to wire up, and should be un-skipped there.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        SetSidecarVersionOnDisk(scope, "local:notes", "seed", 2);

        var act = async () => await store.StoreAsync(
            new MemoryWrite("seed", "local:notes", ["overwrite attempt"]));

        await act.Should().ThrowAsync<ScopeReadOnlyException>();
    }

    [Fact(Skip = "activates with Task 4's write gate — depends on the refusal in the test " +
                 "above actually being thrown; see that test's Skip reason.")]
    public async Task AfterThatRefusal_TheBodyOnDiskIsUnchanged()
    {
        // The property that makes leaving body writes ungated safe: StoreAsync's rollback puts
        // the previous content back, so content and metadata still agree after the refusal.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        SetSidecarVersionOnDisk(scope, "local:notes", "seed", 2);
        byte[] before = File.ReadAllBytes(store.ArtifactPath("seed", "local:notes"));

        try { await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["overwrite"])); }
        catch (ScopeReadOnlyException) { }

        File.ReadAllBytes(store.ArtifactPath("seed", "local:notes")).Should().Equal(before);
    }

    [Fact]
    public void TryUpdateEntry_OnAMemoryWithAnUnknownKey_PreservesIt()
    {
        // Step 5's verification, not an assumption: TryUpdateEntry rebuilds with `with`, which
        // copies every member it does not name, so Unknown should survive untouched without any
        // change to TryUpdateEntry itself. This proves that rather than taking it on faith.
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;

        var entry = new ArtifactEntry("tracked", "file:///tracked.nmp2", 5, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
        {
            Unknown = new Dictionary<string, JsonElement>
            {
                ["futureThing"] = JsonDocument.Parse("1").RootElement,
            }
        };
        store.Upsert(entry, "local:notes");

        bool updated = store.TryUpdateEntry("tracked", "local:notes",
            current => current with { ContentPreview = "updated preview" });

        updated.Should().BeTrue();
        ReadSidecarJson(scope, "local:notes", "tracked").Should().Contain("futureThing");
    }

    /// <summary>
    /// Edits an EXISTING on-disk sidecar to add a key this build's <see cref="ArtifactEntry"/>
    /// has no member for, then invalidates the store's in-process index cache so the next read
    /// goes back to disk. Without the invalidation, <c>LoadIndex</c> would keep serving the
    /// warm <see cref="FileMemoryStore.CachedIndex"/> populated by the seeding
    /// <c>StoreAsync</c> above — which predates this edit — and the write-path test using this
    /// helper would fail for a caching reason that has nothing to do with
    /// <c>BuildDerivedEntry</c>.
    /// </summary>
    /// <summary>
    /// The rebuilt entry's Unknown dictionary must be a COPY of the prior entry's, not the same
    /// instance.
    ///
    /// <para>This constraint was deliberate, commented, load-bearing — and completely untested:
    /// replacing the copy with a plain alias left the entire suite green (1862 passed, 0 failed).
    /// <c>NotBeSameAs</c> is the assertion that discriminates; <c>BeEquivalentTo</c> alone passes
    /// under both.</para>
    ///
    /// <para>Why it matters: Unknown is <c>{ get; set; }</c> and Upsert installs the rebuilt entry
    /// into <c>_indexCache</c>, which LoadIndex hands out BY REFERENCE. An alias puts one mutable
    /// Dictionary in two places, one of them shared cache state.</para>
    /// </summary>
    [Fact]
    public async Task ReStoring_DoesNotAliasThePriorEntrysUnknownDictionary()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        AddUnknownKeyToSidecarOnDisk(scope, "local:notes", "seed", "futureThing");
        var prior = store.LoadIndex("local:notes").Single(e => e.Name == "seed");

        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["new content"]));
        var rebuilt = store.LoadIndex("local:notes").Single(e => e.Name == "seed");

        rebuilt.Unknown.Should().NotBeSameAs(prior.Unknown);
        rebuilt.Unknown.Should().BeEquivalentTo(prior.Unknown);
    }

    private static void AddUnknownKeyToSidecarOnDisk(
        TestHelpers.StoreScope scope, string storeScope, string name, string key)
    {
        string path = SidecarPath(scope, storeScope, name);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node[key] = "present";
        File.WriteAllText(path, node.ToJsonString());
        ((FileMemoryStore)TestHelpers.Store).InvalidateCaches();
    }

    /// <summary>Same on-disk-edit-plus-invalidate shape as <see cref="AddUnknownKeyToSidecarOnDisk"/>,
    /// for the <c>"v"</c> key instead of an unknown one.</summary>
    private static void SetSidecarVersionOnDisk(
        TestHelpers.StoreScope scope, string storeScope, string name, int version)
    {
        string path = SidecarPath(scope, storeScope, name);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["v"] = version;
        File.WriteAllText(path, node.ToJsonString());
        ((FileMemoryStore)TestHelpers.Store).InvalidateCaches();
    }

    /// <summary>Raw sidecar bytes as text, straight off disk — no cache, no re-parse through
    /// <see cref="ArtifactEntry"/> — so an assertion on it reflects exactly what got written.</summary>
    private static string ReadSidecarJson(TestHelpers.StoreScope scope, string storeScope, string name)
        => File.ReadAllText(SidecarPath(scope, storeScope, name));

    /// <summary>The sidecar's <c>"v"</c> value, parsed rather than string-matched so the
    /// assertion does not depend on JSON whitespace formatting.</summary>
    private static int? ReadSidecarSchemaVersion(TestHelpers.StoreScope scope, string storeScope, string name)
    {
        var root = JsonDocument.Parse(ReadSidecarJson(scope, storeScope, name)).RootElement;
        return root.TryGetProperty("v", out var v) ? v.GetInt32() : null;
    }

    /// <summary>
    /// Composes a <c>.meta.json</c> sidecar BY HAND, bypassing <see cref="ArtifactEntry"/>
    /// entirely. That is deliberate: the typed model never CONSTRUCTS a legacy "fields" key
    /// alongside a "v" key (LegacyFields and SchemaVersion coexist fine on the type, but
    /// nothing in production ever constructs that combination), and this suite needs exactly
    /// that combination to reach the forward-fill write path under a version the current
    /// build must not touch.
    /// </summary>
    private static string WriteRawSidecar(
        TestHelpers.StoreScope scope, string storeScope, string name,
        int? schemaVersion = null, string? extraKey = null, bool legacyShaped = false)
    {
        var obj = new JsonObject
        {
            ["name"] = name,
            ["uri"] = $"file:///{name}.nmp2",
            ["originalBytes"] = 5,
            ["chunkCount"] = 1,
            ["createdAt"] = "2026-01-01T00:00:00+00:00",
        };
        if (schemaVersion is not null)
            obj["v"] = schemaVersion.Value;
        if (legacyShaped)
            obj["fields"] = new JsonObject { ["tag"] = new JsonArray("legacy-tag") };
        if (extraKey is not null)
            obj[extraKey] = "present";

        string path = SidecarPath(scope, storeScope, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, obj.ToJsonString());
        return path;
    }

    /// <summary>Where a sidecar for <paramref name="name"/> in <paramref name="storeScope"/>
    /// lives on disk, without going through the store at all -- <c>local:notes</c> maps to
    /// <c>{TempDir}/notes/&lt;name&gt;.meta.json</c>.</summary>
    private static string SidecarPath(TestHelpers.StoreScope scope, string storeScope, string name)
    {
        int colon = storeScope.IndexOf(':');
        string sub = colon >= 0 ? storeScope[(colon + 1)..] : storeScope;
        return Path.Combine(scope.TempDir, sub, name + ".meta.json");
    }
}
