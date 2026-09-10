using FluentAssertions;
using System.Text.Json;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.Plugin.Abstractions;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class AppendContractTests
{
    [Fact]
    public void AppendStoreResult_ExposesTheSpecifiedOutcomeShape()
    {
        var result = new AppendStoreResult(AppendStoreOutcome.Appended, null, "op-1");

        result.Outcome.Should().Be(AppendStoreOutcome.Appended);
        result.Entry.Should().BeNull();
        result.OperationId.Should().Be("op-1");
        result.Warning.Should().BeNull();
        result.Recovery.Should().BeNull();
    }

    [Fact]
    public void AppendStoreException_AllowsOnlyRecoveryRequiredAndInternalCodes()
    {
        var exception = new AppendStoreException(
            AppendStoreErrorCode.RecoveryRequired, "op-1", "recovery required");

        typeof(AppendStoreException).IsSealed.Should().BeTrue();
        exception.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        exception.OperationId.Should().Be("op-1");
    }

    [Fact]
    public void AppendStoreException_RejectsUnsupportedCodes()
    {
        FluentActions.Invoking(() => new AppendStoreException(
                (AppendStoreErrorCode)999, "op-invalid", "invalid code"))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AppendRecoveryInfo_ContainsOnlySafeRecoveryDetails()
    {
        var recovery = new AppendRecoveryInfo("journal.json", "prepared", "replay", "replay journal");

        recovery.JournalPath.Should().Be("journal.json");
        recovery.State.Should().Be("prepared");
        recovery.Action.Should().Be("replay");
        recovery.Detail.Should().Be("replay journal");
        typeof(AppendRecoveryInfo).GetProperties().Select(property => property.Name)
            .Should().NotContain("RawBytes");
    }

    [Fact]
    public void AppendStoreResult_UsesTheSourceGeneratedPublicContract()
    {
        var result = new AppendStoreResult(
            AppendStoreOutcome.SuccessWithWarning,
            null,
            "op-2",
            new AppendStoreWarning(AppendWarningCode.EventFailed, "event failed"),
            new AppendRecoveryInfo("journal", "prepared", "replay", "replay it"));

        string json = JsonSerializer.Serialize(result, ScriniaMcpJsonContext.Default.AppendStoreResult);
        using var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("outcome").GetInt32()
            .Should().Be((int)AppendStoreOutcome.SuccessWithWarning);
        document.RootElement.GetProperty("operationId").GetString().Should().Be("op-2");
        document.RootElement.GetProperty("warning").GetProperty("code").GetInt32()
            .Should().Be((int)AppendWarningCode.EventFailed);
        document.RootElement.GetProperty("recovery").GetProperty("action").GetString()
            .Should().Be("replay");
    }

    [Fact]
    public async Task AppendFacade_PreservesWarningAndOperationId()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.SuccessWithWarning,
            new ArtifactEntry(MemoryAddress.Parse("/mapped"), "mapped", 1, 1, DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            "op-warning",
            new AppendStoreWarning(AppendWarningCode.EventFailed, "event sink failed")));

        var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));

        response.OperationId.Should().Be("op-warning");
        response.Info.Should().Contain("warningCode: EventFailed");
        response.Info.Should().Contain("event sink failed");
    }

    [Fact]
    public async Task AppendFacade_MapsRecoveryResultToStableError()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.RecoveryRequired,
            null,
            "op-recovery",
            Recovery: new AppendRecoveryInfo("journal", "prepared", "replay", "recovery required")));

        var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));

        response.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
        response.OperationId.Should().Be("op-recovery");
        response.ActionNeeded.Should().Contain("action: replay");
    }

    [Theory]
    [InlineData(AppendStoreOutcome.Created, "created")]
    [InlineData(AppendStoreOutcome.Appended, "appended")]
    public async Task AppendFacade_MapsStoreOutcomeToTheMatchingAction(
        AppendStoreOutcome outcome, string action)
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            outcome,
            new ArtifactEntry(MemoryAddress.Parse("/mapped"), "mapped", 1, 1, DateTimeOffset.UtcNow,
                UpdatedAt: outcome == AppendStoreOutcome.Appended ? DateTimeOffset.UtcNow : null),
            "op-outcome"));

        var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));

        response.Action.Should().Be(action);
        response.OperationId.Should().Be("op-outcome");
        response.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task AppendFacade_MapsWarningToStructuredSuccessWithoutGenericError()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.SuccessWithWarning,
            new ArtifactEntry(MemoryAddress.Parse("/mapped"), "mapped", 1, 1, DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            "op-warning",
            new AppendStoreWarning(AppendWarningCode.EventFailed, "event sink failed")));

        var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));

        response.Status.Should().Be("success_with_warning");
        response.IsError.Should().BeFalse();
        response.OperationId.Should().Be("op-warning");
        response.Info.Should().Contain("warningCode: EventFailed");
        response.ToYaml().Should().NotContain("An error occurred invoking 'append'");
    }

    [Fact]
    public async Task AppendFacade_MapsAfterEventCancellationToStructuredWarning()
    {
        using var scope = new TestHelpers.StoreScope();
        IMemoryEventSink? prior = MemoryEventSinkContext.Current;
        MemoryEventSinkContext.Current = new CancellingSink();
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.Appended,
            new ArtifactEntry(MemoryAddress.Parse("/mapped"), "mapped", 1, 1, DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            "op-event"));

        try
        {
            var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));

            response.Status.Should().Be("success_with_warning");
            response.Error.Should().BeNull();
            response.Info.Should().Contain("warningCode: EventFailed");
            response.OperationId.Should().Be("op-event");
        }
        finally
        {
            MemoryEventSinkContext.Current = prior;
        }
    }

    [Fact]
    public async Task AppendFacade_PreservesCleanupWarningAndAddsEventFailureDetails()
    {
        using var scope = new TestHelpers.StoreScope();
        FileMemoryStore.AppendFaultInjector = (point, _, _) =>
            point == AppendFaultPoint.AfterEvent ? new IOException("event delivery failed") : null;
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.SuccessWithWarning,
            new ArtifactEntry(MemoryAddress.Parse("/mapped"), "mapped", 1, 1, DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            "op-combined",
            new AppendStoreWarning(AppendWarningCode.PostCommitCleanupFailed, "cleanup evidence retained")));

        try
        {
            var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));

            response.Status.Should().Be("success_with_warning");
            response.Action.Should().Be("appended");
            response.OperationId.Should().Be("op-combined");
            response.Info.Should().Contain("warningCode: PostCommitCleanupFailed");
            response.Info.Should().Contain("cleanup evidence retained");
            response.Info.Should().Contain("eventWarningCode: EventFailed");
            response.Info.Should().Contain(
                "The append committed, but post-commit event delivery failed: event delivery failed");
        }
        finally
        {
            FileMemoryStore.AppendFaultInjector = null;
        }
    }

    [Fact]
    public async Task AppendFacade_MapsCreatedWarningAsCreated()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.SuccessWithWarning,
            new ArtifactEntry(MemoryAddress.Parse("/created"), "created", 1, 1, DateTimeOffset.UtcNow),
            "op-created-warning",
            new AppendStoreWarning(AppendWarningCode.PostCommitCleanupFailed, "cleanup evidence retained")));

        var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/created"));

        response.Status.Should().Be("success_with_warning");
        response.Action.Should().Be("created");
        response.Content.Should().StartWith("Created ");
        response.OperationId.Should().Be("op-created-warning");
        response.Info.Should().Contain("warningCode: PostCommitCleanupFailed");
        response.Info.Should().Contain("cleanup evidence retained");
    }

    [Fact]
    public async Task AppendFacade_PassesOperationIdToOperationAwareSinks()
    {
        using var scope = new TestHelpers.StoreScope();
        var sink = new OperationAwareSink();
        IMemoryEventSink? prior = MemoryEventSinkContext.Current;
        MemoryEventSinkContext.Current = sink;
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.Appended,
            new ArtifactEntry(MemoryAddress.Parse("/mapped"), "mapped", 1, 1, DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            "op-aware"));

        try
        {
            await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));
            sink.OperationId.Should().Be("op-aware");
        }
        finally
        {
            MemoryEventSinkContext.Current = prior;
        }
    }

    [Fact]
    public async Task AppendFacade_BeforeEventFaultBecomesOperationAwareInternalError()
    {
        using var scope = new TestHelpers.StoreScope();
        IMemoryEventSink? prior = MemoryEventSinkContext.Current;
        MemoryEventSinkContext.Current = new BeforeEventSink();
        FileMemoryStore.AppendFaultInjector = (faultPoint, operationId, _) =>
            faultPoint == AppendFaultPoint.BeforeEvent ? new IOException($"{operationId}:BeforeEvent") : null;
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.Appended,
            new ArtifactEntry(MemoryAddress.Parse("/mapped"), "mapped", 1, 1, DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            "op-boundary"));

        try
        {
            var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));
            response.ErrorCode.Should().Be(ErrorCodes.Internal);
            response.OperationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            FileMemoryStore.AppendFaultInjector = null;
            MemoryEventSinkContext.Current = prior;
        }
    }

    [Fact]
    public async Task AppendFacade_AfterEventFaultRemainsOperationAwareWarning()
    {
        using var scope = new TestHelpers.StoreScope();
        FileMemoryStore.AppendFaultInjector = (faultPoint, operationId, _) =>
            faultPoint == AppendFaultPoint.AfterEvent ? new IOException($"{operationId}:AfterEvent") : null;
        MemoryStoreContext.Current = new ResultStore(new AppendStoreResult(
            AppendStoreOutcome.Appended,
            new ArtifactEntry(MemoryAddress.Parse("/mapped"), "mapped", 1, 1, DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            "op-boundary"));

        try
        {
            var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));
            response.Status.Should().Be("success_with_warning");
            response.OperationId.Should().Be("op-boundary");
            response.Info.Should().Contain("warningCode: EventFailed");
        }
        finally
        {
            FileMemoryStore.AppendFaultInjector = null;
        }
    }

    [Fact]
    public async Task AppendFacade_MapsStoreExceptionInsteadOfLeakingGenericInvocationError()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new ThrowingStore();

        var response = await new ScriniaMcpTools().Append("body", MemoryAddress.Parse("/mapped"));

        response.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
        response.OperationId.Should().Be("op-exception");
        response.ToYaml().Should().NotContain("An error occurred invoking 'append'");
    }

    private sealed class ResultStore(AppendStoreResult result) : FakeMemoryStore
    {
        public override Task<AppendStoreResult> AppendAsync(MemoryWrite write, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingStore : FakeMemoryStore
    {
        public override Task<AppendStoreResult> AppendAsync(MemoryWrite write, CancellationToken ct = default) =>
            throw new AppendStoreException(AppendStoreErrorCode.RecoveryRequired, "op-exception", "recovery required");
    }

    private sealed class CancellingSink : IMemoryEventSink
    {
        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.FromCanceled(new CancellationToken(canceled: true));

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class OperationAwareSink : IMemoryEventSink, IOperationAwareMemoryEventSink
    {
        public string? OperationId { get; private set; }

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store,
            string operationId, CancellationToken ct)
        {
            OperationId = operationId;
            return Task.CompletedTask;
        }

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class BeforeEventSink : IMemoryEventSink, IMemoryOperationHook
    {
        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
