using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.Plugin.Abstractions;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class AppendFacadeMappingTests
{
    [Fact]
    public async Task AppendFacade_MapsAStoreCreatedResultAsCreated()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new FakeMemoryStore
        {
            AppendResult = new AppendStoreResult(
                AppendStoreOutcome.Created,
                new ArtifactEntry(MemoryAddress.Parse("/new"), "new", 1, 1, DateTimeOffset.UtcNow),
                "op-created")
        };
        MemoryStoreContext.Current = store;

        McpResponse response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/new"));

        response.IsError.Should().BeFalse();
        response.Action.Should().Be("created");
        response.OperationId.Should().Be("op-created");
    }

    [Fact]
    public async Task AppendFacade_MapsAStoreInternalExceptionWithoutGenericInvocationError()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new FakeMemoryStore
        {
            AppendFailure = new AppendStoreException(
                AppendStoreErrorCode.Internal, "op-internal", "append invariant failed")
        };

        McpResponse response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/new"));

        response.ErrorCode.Should().Be(ErrorCodes.Internal);
        response.OperationId.Should().Be("op-internal");
        response.ToYaml().Should().NotContain("An error occurred invoking 'append'");
    }

    [Fact]
    public async Task AppendFacade_BeforeEventFailureOccursBeforeJournalOrLiveMutation()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = Path.Combine(Path.GetTempPath(), "scrinia-before-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new FileMemoryStore(root);
        MemoryStoreContext.Current = store;
        var sink = new BeforeAppendProbe();
        IMemoryEventSink? prior = MemoryEventSinkContext.Current;
        MemoryEventSinkContext.Current = sink;
        FileMemoryStore.AppendFaultInjector = (point, _, _) =>
            point == AppendFaultPoint.BeforeEvent ? new IOException("before event failed") : null;

        try
        {
            McpResponse response = await new ScriniaMcpTools().Append(
                "body", MemoryAddress.Parse("/before-event"));

            response.ErrorCode.Should().Be(ErrorCodes.Internal);
            response.OperationId.Should().NotBeNullOrWhiteSpace();
            sink.BeforeCalled.Should().BeFalse();
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            FileMemoryStore.AppendFaultInjector = null;
            MemoryEventSinkContext.Current = prior;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AppendFacade_BeforeAppendCancellationOccursBeforeJournalOrLiveMutation()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = Path.Combine(Path.GetTempPath(), "scrinia-before-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new FileMemoryStore(root);
        MemoryStoreContext.Current = store;
        var sink = new BeforeAppendProbe { Cancel = true };
        IMemoryEventSink? prior = MemoryEventSinkContext.Current;
        MemoryEventSinkContext.Current = sink;

        try
        {
            McpResponse response = await new ScriniaMcpTools().Append(
                "body", MemoryAddress.Parse("/before-hook"));

            response.ErrorCode.Should().Be(ErrorCodes.AppendCancelled);
            sink.BeforeCalled.Should().BeTrue();
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            MemoryEventSinkContext.Current = prior;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AppendFacade_MapsNonRecoveryNullEntryToInternalWithoutGenericInvocationError()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new FakeMemoryStore
        {
            AppendResult = new AppendStoreResult(AppendStoreOutcome.Appended, null, "op-null-entry")
        };

        McpResponse response = await new ScriniaMcpTools().Append(
            "body", MemoryAddress.Parse("/null-entry"));

        response.ErrorCode.Should().Be(ErrorCodes.Internal);
        response.OperationId.Should().Be("op-null-entry");
        response.ToYaml().Should().NotContain("An error occurred invoking 'append'");
    }

    private sealed class BeforeAppendProbe : IMemoryEventSink, IMemoryOperationHook
    {
        public bool Cancel { get; init; }
        public bool BeforeCalled { get; private set; }

        public Task OnBeforeAppendAsync(BeforeAppendContext context, CancellationToken ct = default)
        {
            BeforeCalled = true;
            context.Cancel = Cancel;
            context.CancelReason = Cancel ? "cancelled by before hook" : null;
            return Task.CompletedTask;
        }

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
