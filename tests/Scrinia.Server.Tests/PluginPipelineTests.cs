using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Plugin.Abstractions;
using Scrinia.Server.Models;
using Scrinia.Server.Services;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class PluginPipelineTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public PluginPipelineTests(ScriniaServerFactory factory) => _factory = factory;

    private IMemoryStore GetStore()
    {
        var sm = _factory.Services.GetRequiredService(typeof(StoreManager)) as StoreManager;
        return sm!.GetStore(_factory.PrimaryStore);
    }

    // ── No hooks (pass-through) ─────────────────────────────────────────────

    [Fact]
    public async Task Store_without_hooks_passes_through()
    {
        var pipeline = new PluginPipeline([]);
        var store = GetStore();

        var result = await pipeline.StoreAsync(store, new StoreRequest(["hello"], "pipeline-test-1"));
        result.Should().NotBeNull();
        result.Name.Should().Be("pipeline-test-1");
        result.ChunkCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Append_without_hooks_passes_through()
    {
        var pipeline = new PluginPipeline([]);
        var store = GetStore();

        // Store first so append can find it
        await pipeline.StoreAsync(store, new StoreRequest(["seed"], "pipeline-append-1"));
        var result = await pipeline.AppendAsync(store, "pipeline-append-1", "more data");
        result.Should().NotBeNull();
        result.ChunkCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task Forget_without_hooks_passes_through()
    {
        var pipeline = new PluginPipeline([]);
        var store = GetStore();

        await pipeline.StoreAsync(store, new StoreRequest(["to-forget"], "pipeline-forget-1"));
        bool deleted = await pipeline.ForgetAsync(store, "pipeline-forget-1");
        deleted.Should().BeTrue();
    }

    // ── Before hooks ────────────────────────────────────────────────────────

    [Fact]
    public async Task Before_store_hook_fires()
    {
        string? capturedName = null;
        var hook = new TestHook
        {
            OnBeforeStore = (ctx, _) => { capturedName = ctx.Name; return Task.CompletedTask; },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        await pipeline.StoreAsync(store, new StoreRequest(["data"], "hook-before-store-1"));
        capturedName.Should().Be("hook-before-store-1");
    }

    [Fact]
    public async Task Before_append_hook_fires()
    {
        string? capturedContent = null;
        var hook = new TestHook
        {
            OnBeforeAppend = (ctx, _) => { capturedContent = ctx.Content; return Task.CompletedTask; },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        await pipeline.StoreAsync(store, new StoreRequest(["seed"], "hook-before-append-1"));
        await pipeline.AppendAsync(store, "hook-before-append-1", "appended-content");
        capturedContent.Should().Be("appended-content");
    }

    [Fact]
    public async Task Before_forget_hook_fires()
    {
        string? capturedName = null;
        var hook = new TestHook
        {
            OnBeforeForget = (ctx, _) => { capturedName = ctx.Name; return Task.CompletedTask; },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        await pipeline.StoreAsync(store, new StoreRequest(["data"], "hook-before-forget-1"));
        await pipeline.ForgetAsync(store, "hook-before-forget-1");
        capturedName.Should().Be("hook-before-forget-1");
    }

    // ── After hooks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task After_store_hook_fires_with_result()
    {
        string? qualifiedName = null;
        var hook = new TestHook
        {
            OnAfterStore = (ctx, _) => { qualifiedName = ctx.QualifiedName; return Task.CompletedTask; },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        await pipeline.StoreAsync(store, new StoreRequest(["data"], "hook-after-store-1"));
        qualifiedName.Should().Be("hook-after-store-1");
    }

    [Fact]
    public async Task After_append_hook_fires_with_result()
    {
        int capturedChunks = 0;
        var hook = new TestHook
        {
            OnAfterAppend = (ctx, _) => { capturedChunks = ctx.ChunkCount; return Task.CompletedTask; },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        await pipeline.StoreAsync(store, new StoreRequest(["seed"], "hook-after-append-1"));
        await pipeline.AppendAsync(store, "hook-after-append-1", "more");
        capturedChunks.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task After_forget_hook_fires_with_result()
    {
        bool? wasDeleted = null;
        var hook = new TestHook
        {
            OnAfterForget = (ctx, _) => { wasDeleted = ctx.WasDeleted; return Task.CompletedTask; },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        await pipeline.StoreAsync(store, new StoreRequest(["data"], "hook-after-forget-1"));
        await pipeline.ForgetAsync(store, "hook-after-forget-1");
        wasDeleted.Should().BeTrue();
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Before_store_cancel_throws()
    {
        var hook = new TestHook
        {
            OnBeforeStore = (ctx, _) =>
            {
                ctx.Cancel = true;
                ctx.CancelReason = "blocked by test";
                return Task.CompletedTask;
            },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        var act = () => pipeline.StoreAsync(store, new StoreRequest(["data"], "cancel-store-1"));
        await act.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("blocked by test");
    }

    [Fact]
    public async Task Before_append_cancel_throws()
    {
        var hook = new TestHook
        {
            OnBeforeAppend = (ctx, _) =>
            {
                ctx.Cancel = true;
                ctx.CancelReason = "append blocked";
                return Task.CompletedTask;
            },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        await MemoryOrchestrator.StoreAsync(store, new StoreRequest(["seed"], "cancel-append-1"));
        var act = () => pipeline.AppendAsync(store, "cancel-append-1", "more");
        await act.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("append blocked");
    }

    [Fact]
    public async Task Before_forget_cancel_throws()
    {
        var hook = new TestHook
        {
            OnBeforeForget = (ctx, _) =>
            {
                ctx.Cancel = true;
                ctx.CancelReason = "forget blocked";
                return Task.CompletedTask;
            },
        };
        var pipeline = new PluginPipeline([hook]);
        var store = GetStore();

        await MemoryOrchestrator.StoreAsync(store, new StoreRequest(["data"], "cancel-forget-1"));
        var act = () => pipeline.ForgetAsync(store, "cancel-forget-1");
        await act.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("forget blocked");
    }

    // ── Ordering ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hooks_run_in_order()
    {
        var order = new List<string>();
        var hookA = new TestHook(order: 10)
        {
            OnBeforeStore = (_, _) => { order.Add("A"); return Task.CompletedTask; },
        };
        var hookB = new TestHook(order: 1)
        {
            OnBeforeStore = (_, _) => { order.Add("B"); return Task.CompletedTask; },
        };

        // Pipeline sorts by Order, so B (1) runs before A (10)
        var pipeline = new PluginPipeline([hookA, hookB]);
        var store = GetStore();

        await pipeline.StoreAsync(store, new StoreRequest(["data"], "order-test-1"));
        order.Should().Equal("B", "A");
    }

    // ── Test helpers ────────────────────────────────────────────────────────

    private sealed class TestHook : IMemoryOperationHook
    {
        private readonly int _order;

        public TestHook(int order = 0) => _order = order;

        int IMemoryOperationHook.Order => _order;

        public Func<BeforeStoreContext, CancellationToken, Task>? OnBeforeStore { get; init; }
        public Func<AfterStoreContext, CancellationToken, Task>? OnAfterStore { get; init; }
        public Func<BeforeAppendContext, CancellationToken, Task>? OnBeforeAppend { get; init; }
        public Func<AfterAppendContext, CancellationToken, Task>? OnAfterAppend { get; init; }
        public Func<BeforeForgetContext, CancellationToken, Task>? OnBeforeForget { get; init; }
        public Func<AfterForgetContext, CancellationToken, Task>? OnAfterForget { get; init; }

        Task IMemoryOperationHook.OnBeforeStoreAsync(BeforeStoreContext ctx, CancellationToken ct) =>
            OnBeforeStore?.Invoke(ctx, ct) ?? Task.CompletedTask;
        Task IMemoryOperationHook.OnAfterStoreAsync(AfterStoreContext ctx, CancellationToken ct) =>
            OnAfterStore?.Invoke(ctx, ct) ?? Task.CompletedTask;
        Task IMemoryOperationHook.OnBeforeAppendAsync(BeforeAppendContext ctx, CancellationToken ct) =>
            OnBeforeAppend?.Invoke(ctx, ct) ?? Task.CompletedTask;
        Task IMemoryOperationHook.OnAfterAppendAsync(AfterAppendContext ctx, CancellationToken ct) =>
            OnAfterAppend?.Invoke(ctx, ct) ?? Task.CompletedTask;
        Task IMemoryOperationHook.OnBeforeForgetAsync(BeforeForgetContext ctx, CancellationToken ct) =>
            OnBeforeForget?.Invoke(ctx, ct) ?? Task.CompletedTask;
        Task IMemoryOperationHook.OnAfterForgetAsync(AfterForgetContext ctx, CancellationToken ct) =>
            OnAfterForget?.Invoke(ctx, ct) ?? Task.CompletedTask;
    }
}
