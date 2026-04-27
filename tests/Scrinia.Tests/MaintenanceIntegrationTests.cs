using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Integration tests verifying that <see cref="MaintenanceEventSink"/> auto-linking
/// fires correctly through the full MCP tool pipeline via <see cref="CompositeEventSink"/>.
/// </summary>
public sealed class MaintenanceIntegrationTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaMcpTools _tools;

    public MaintenanceIntegrationTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaMcpTools();

        // Wire up CompositeEventSink wrapping only MaintenanceEventSink (no embeddings needed)
        MemoryEventSinkContext.Default = new CompositeEventSink([new MaintenanceEventSink()]);
    }

    public void Dispose()
    {
        MemoryEventSinkContext.Default = null;
        _scope.Dispose();
    }

    [Fact]
    public async Task Store_WithReference_AutoLinksTargetViaCompositeEventSink()
    {
        var store = MemoryStoreContext.Current!;

        // 1. Store a "target" memory
        await _tools.Store(["Target content for the auto-link test."], "topic:target");

        // 2. Store a "source" memory whose content references topic:target
        await _tools.Store(
            ["This references topic:target in the analysis."],
            "topic:source");

        // 3. Load the target entry's metadata and verify it has a ref:topic:source keyword
        var (targetScope, targetSubject) = store.ParseQualifiedName("topic:target");
        var targetEntries = store.LoadIndex(targetScope);
        var targetEntry = targetEntries.First(e =>
            e.Name.Equals(targetSubject, StringComparison.OrdinalIgnoreCase));

        targetEntry.Keywords.Should().Contain("ref:/topic/source",
            "storing a memory that references topic:target should auto-link via " +
            "CompositeEventSink → MaintenanceEventSink, adding ref:/topic/source on the target");
    }

    [Fact]
    public async Task Append_WithReference_AutoLinksTargetViaCompositeEventSink()
    {
        var store = MemoryStoreContext.Current!;

        // 1. Store target and source memories (no cross-references yet)
        await _tools.Store(["Target for append test."], "topic:appendtarget");
        await _tools.Store(["Source content initially."], "topic:appendsource");

        // 2. Append content to source that references the target
        await _tools.Append("See topic:appendtarget for details.", "topic:appendsource");

        // 3. Verify auto-link was created on the target
        var (targetScope, targetSubject) = store.ParseQualifiedName("topic:appendtarget");
        var targetEntries = store.LoadIndex(targetScope);
        var targetEntry = targetEntries.First(e =>
            e.Name.Equals(targetSubject, StringComparison.OrdinalIgnoreCase));

        targetEntry.Keywords.Should().Contain("ref:/topic/appendsource",
            "appending content that references topic:appendtarget should auto-link via " +
            "CompositeEventSink → MaintenanceEventSink");
    }
}
