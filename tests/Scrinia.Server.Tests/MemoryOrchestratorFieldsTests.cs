using FluentAssertions;
using Scrinia.Core;
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
        entry.Fields!["status"].Should().BeEquivalentTo(["active"]);
        entry.Fields["person"].Should().BeEquivalentTo(["Nick"]);
        entry.Keywords.Should().NotContain(k => k.StartsWith("person:"));

        await MemoryOrchestrator.AppendAsync(_store, "server-fields", "status: done\nperson: Maria\nMore.");
        entry = _store.LoadIndex("local").Single(e => e.Name == "server-fields");
        entry.Fields!["status"].Should().BeEquivalentTo(["done"]);
        entry.Fields["person"].Should().BeEquivalentTo(["Nick", "Maria"]);
    }
}
