using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Server.Models;
using Scrinia.Server.Services;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// Verifies <see cref="MemoryOrchestrator.StoreAsync"/>/<see cref="MemoryOrchestrator.AppendAsync"/>
/// carry unified Fields with the same contract as the MCP Store/Append path (routed keyword
/// fields, content-extracted fields, and reference fields, merged and seeded into term
/// frequencies).
/// </summary>
public sealed class MemoryOrchestratorFieldsTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public MemoryOrchestratorFieldsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_orch_fields_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia", "store", "local"));
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task StoreAsync_And_AppendAsync_CarryUnifiedFields()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["status: active\nBody."], "server-fields", Keywords: ["person:Nick"]));

        var entry = _store.LoadIndex("local").Single(e => e.Name == "server-fields");
        entry.Attributes!["status"].Should().BeEquivalentTo(["active"]);
        entry.Attributes["person"].Should().BeEquivalentTo(["Nick"]);
        entry.Keywords.Should().NotContain(k => k.StartsWith("person:"));

        await MemoryOrchestrator.AppendAsync(_store, "server-fields", "status: done\nperson: Maria\nMore.");
        entry = _store.LoadIndex("local").Single(e => e.Name == "server-fields");
        entry.Attributes!["status"].Should().BeEquivalentTo(["done"]);
        entry.Attributes["person"].Should().BeEquivalentTo(["Nick", "Maria"]);
    }

    /// <summary>
    /// P6.3 writer cutover: proves <see cref="Scrinia.Core.Migration.AttributeMigration.Forward"/> runs inside
    /// <see cref="MemoryOrchestrator.StoreAsync"/>'s persistent path AT WRITE TIME, not merely
    /// picked up later by <see cref="FileMemoryStore.LoadIndex"/>'s read-time forward-fill.
    ///
    /// Write/read-time isolation: <see cref="FileMemoryStore.LoadIndex"/> always forward-fills
    /// Fields in-memory on every cold load (see its "Read-time forward-fill" comment) regardless
    /// of whether Fields was already populated at write — so reading back through LoadIndex
    /// cannot distinguish "populated at write" from "upgraded on this read". This test instead
    /// reads the raw `*.meta.json` sidecar directly (via the `internal MetaPath` helper, which
    /// Scrinia.Core exposes to Scrinia.Server.Tests via InternalsVisibleTo) with a plain
    /// deserialize that never calls AttributeMigration. Before Task 3's change, this assertion
    /// fails because the freshly-written sidecar's Fields lacks "tag"/"review" (only
    /// keyword/ref/attribute-derived keys are present at write time).
    /// </summary>
    [Fact]
    public async Task StoreAsync_Persistent_PopulatesFieldsAtWriteTime_DualWriteWithLegacy()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["Body about widgets."], "orch-writer-cutover-store",
                Tags: ["widgets"], ReviewAfter: "2026-08-01"));

        string metaPath = _store.MetaPath("orch-writer-cutover-store", "local");
        string json = File.ReadAllText(metaPath);
        var persisted = JsonSerializer.Deserialize<ArtifactEntry>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        persisted.Should().NotBeNull();
        persisted!.Attributes.Should().NotBeNull();
        persisted.Attributes!["tag"].Should().Contain("widgets");
        persisted.Attributes!["review"].Should().NotBeEmpty();

        // Legacy fields STILL written (dual-write) — Forward is additive, not a replacement.
        persisted.Tags.Should().Contain("widgets");
        persisted.ReviewAfter.Should().NotBeNull();
    }

    /// <summary>
    /// Ephemeral counterpart: EphemeralEntry is in-memory-only and never forward-filled at read
    /// time, so asserting directly against the store (no sidecar/LoadIndex involved at all)
    /// proves <see cref="MemoryOrchestrator.StoreAsync"/>'s tag->Fields routing runs at write.
    /// </summary>
    [Fact]
    public async Task StoreAsync_Ephemeral_PopulatesFieldsAtWriteTime_DualWriteWithLegacy()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["Ephemeral scratch note about widgets."], "~orch-writer-cutover-eph",
                Tags: ["widgets"]));

        var entry = _store.GetEphemeral("orch-writer-cutover-eph");
        entry.Should().NotBeNull();

        entry!.Attributes.Should().NotBeNull();
        entry.Attributes!["tag"].Should().Contain("widgets");

        // Legacy field STILL written (dual-write).
        entry.Tags.Should().Contain("widgets");
    }
}
