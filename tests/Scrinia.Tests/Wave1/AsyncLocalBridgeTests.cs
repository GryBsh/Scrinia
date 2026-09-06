using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests.Wave1;

public sealed class AsyncLocalBridgeTests
{
    [Fact]
    public async Task CliPath_UsesDefaultFallback_WhenCurrentIsNull()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-w1-c6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new FileMemoryStore(root);
        IMemoryStore? prevCurrent = MemoryStoreContext.Current;
        IMemoryStore? prevDefault = MemoryStoreContext.Default;
        MemoryStoreContext.Current = null;
        MemoryStoreContext.Default = store;
        try
        {
            var resolved = MemoryStoreContext.Current ?? MemoryStoreContext.Default;
            Assert.Same(store, resolved);

            var viaResolve = MemoryStoreContext.Resolve();
            Assert.Same(store, viaResolve);

            var tools = new ScriniaMcpTools();
            string yaml = await tools.MemoryRecall("/nonexistent-test-" + Guid.NewGuid().ToString("N"));
            // Recall should reach store and return NotFound error YAML, not throw "No memory store configured"
            Assert.Contains("not found", yaml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("No memory store configured", yaml, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            MemoryStoreContext.Default = prevDefault;
            MemoryStoreContext.Current = prevCurrent;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StoreScope_ClearsBoth_CurrentAndDefault_OnDispose()
    {
        string workspaceDir = Path.Combine(Path.GetTempPath(), $"scrinia_w1_c6_scope_{Guid.NewGuid():N}");
        IMemoryStore? prevCurrent = MemoryStoreContext.Current;
        IMemoryStore? prevDefault = MemoryStoreContext.Default;
        MemoryStoreContext.Current = null;
        MemoryStoreContext.Default = null;

        try
        {
            using (var scope = new TestHelpers.StoreScope())
            {
                Assert.NotNull(MemoryStoreContext.Current);
                // During scope, Current is set; Default is shadowed via Current sentinel, not cleared
                // Also verify Default fallback is preserved via Resolve path
                var resolved = MemoryStoreContext.Resolve();
                Assert.NotNull(resolved);
            }

            Assert.Null(MemoryStoreContext.Current);
            // After dispose, flow's Current sentinel cleared - should be null (falls back to outer Default which was null)
            Assert.Null(MemoryStoreContext.Current ?? MemoryStoreContext.Default);
        }
        finally
        {
            MemoryStoreContext.Current = prevCurrent;
            MemoryStoreContext.Default = prevDefault;
            try { Directory.Delete(workspaceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StoreScope_RestoresPreviousDefault_WhenNested()
    {
        string outerRoot = Path.Combine(Path.GetTempPath(), "scrinia-w1-c6-outer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outerRoot);
        var outerStore = new FileMemoryStore(outerRoot);
        IMemoryStore? prevCurrent = MemoryStoreContext.Current;
        IMemoryStore? prevDefault = MemoryStoreContext.Default;

        MemoryStoreContext.Current = outerStore;
        MemoryStoreContext.Default = outerStore;
        TestHelpers.StoreScope? innerScope = null;
        try
        {
            innerScope = new TestHelpers.StoreScope();
            // Inner scope should have replaced Current with its own isolated store
            Assert.NotSame(outerStore, MemoryStoreContext.Current);
            innerScope.Dispose();
            innerScope = null;

            // After inner dispose, outer store should be restored (both Current and Default)
            Assert.Same(outerStore, MemoryStoreContext.Current);
            Assert.Same(outerStore, MemoryStoreContext.Default);
            var resolved = MemoryStoreContext.Resolve();
            Assert.Same(outerStore, resolved);
        }
        finally
        {
            innerScope?.Dispose();
            MemoryStoreContext.Current = prevCurrent;
            MemoryStoreContext.Default = prevDefault;
            outerStore.Dispose();
            try { Directory.Delete(outerRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorkspaceSetup_Configure_PopulatesDefault()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-w1-c6-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IMemoryStore? prevCurrent = MemoryStoreContext.Current;
        IMemoryStore? prevDefault = MemoryStoreContext.Default;
        try
        {
            Scrinia.Commands.WorkspaceSetup.Configure(root);
            Assert.NotNull(MemoryStoreContext.Default);
            Assert.NotNull(MemoryStoreContext.Current);
            // Simulate generic-host thread boundary: clear Current, Default should still resolve
            MemoryStoreContext.Current = null;
            var resolved = MemoryStoreContext.Resolve();
            Assert.NotNull(resolved);
            Assert.Same(MemoryStoreContext.Default, resolved);
        }
        finally
        {
            MemoryStoreContext.Current = prevCurrent;
            MemoryStoreContext.Default = prevDefault;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
