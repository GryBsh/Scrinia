using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class McpWholeMemoryTransactionTests
{
    [Fact]
    public async Task Remember_RollbackSafeFailureReturnsDurableWriteError()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/transaction/target", ["old"]);
        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryRemember("/transaction/target", ["new"]));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        response.OperationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Remember_RetainedEvidenceReturnsRecoveryDetailsWithoutContent()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/transaction/recovery", ["old secret"]);
        var sink = new RecordingSink();
        IMemoryEventSink? priorSink = MemoryEventSinkContext.Current;
        MemoryEventSinkContext.Current = sink;
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) => point switch
        {
            WholeMemoryFaultPoint.LiveArtifactWrite or WholeMemoryFaultPoint.RollbackWrite =>
                new IOException("injected transaction failure"),
            _ => null
        };

        try
        {
            McpResponseAssert response = ResponseParser.Parse(
                await new ScriniaMcpTools().MemoryRemember("/transaction/recovery", ["new secret"]));

            response.Status.Should().Be("error");
            response.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
            response.OperationId.Should().NotBeNullOrWhiteSpace();
            response.Scope.Should().Be("local:transaction");
            response.JournalPath.Should().NotContain(scope.WorkspaceDir);
            response.Content.Should().BeNull();
            response.Error.Should().NotContain("old secret");
            await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
            sink.Stored.Should().BeEmpty();
        }
        finally
        {
            FileMemoryStore.WholeMemoryFaultInjector = null;
            MemoryEventSinkContext.Current = priorSink;
        }
    }

    [Fact]
    public async Task Remember_PostCommitCleanupWarningPreservesCommittedOutcome()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/transaction/warning", ["old"]);
        store.FailOnceAt(WholeMemoryFaultPoint.ArchiveCleanup);

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryRemember("/transaction/warning", ["new"]));

        response.Status.Should().Be("success_with_warning");
        response.OperationId.Should().NotBeNullOrWhiteSpace();
        response.Path.Should().Be("/transaction/warning");
    }

    [Fact]
    public async Task Compact_ScopeLockTimeoutReturnsDurableWriteError()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/transaction/locked", ["one", "two"]);
        using FileLock held = FileLock.AcquireExclusive(
            Path.Combine(store.GetStoreDirForScope("local:transaction"), ".lock"));

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryCompact("/transaction/locked"));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        response.Error.Should().Contain("Failed to acquire file lock");
    }

    [Fact]
    public async Task SkillRecoveryResponseOmitsAbsoluteEvidencePath()
    {
        using var scope = new TestHelpers.StoreScope();
        string skills = PathRouter.ToFileEntityDir("skill", scope.WorkspaceDir);
        string body = Path.Combine(skills, "privacy-probe.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        Directory.CreateDirectory(skills);
        File.WriteAllText(body, "old");
        var coordinator = new FileEntityTransactionCoordinator(scope.WorkspaceDir);
        coordinator.FailOnceAt(FileEntityFaultPoint.BodyWrite);
        coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);
        await coordinator.ExecuteAsync(new FileEntityMutation(
            "skill", "privacy-probe", body, meta, Path.Combine(skills, "versions"),
            "new"u8.ToArray(), null, false, false));
        string evidence = Directory.EnumerateFiles(skills, ".file-entity-*.recovery").Single();
        File.WriteAllBytes(evidence, [0]);

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryRemember("/skill/privacy-probe", ["replacement"]));

        response.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
        response.JournalPath.Should().NotContain(scope.WorkspaceDir);
    }

    [Fact]
    public async Task AgentRecoveryResponseOmitsAbsoluteEvidencePath()
    {
        using var scope = new TestHelpers.StoreScope();
        string agents = PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir);
        string body = Path.Combine(agents, "privacy-probe.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        Directory.CreateDirectory(agents);
        File.WriteAllText(body, "old");
        var coordinator = new FileEntityTransactionCoordinator(scope.WorkspaceDir);
        coordinator.FailOnceAt(FileEntityFaultPoint.BodyWrite);
        coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);
        await coordinator.ExecuteAsync(new FileEntityMutation(
            "agent", "privacy-probe", body, meta, Path.Combine(agents, "versions"),
            "new"u8.ToArray(), null, false, false));
        string evidence = Directory.EnumerateFiles(agents, ".file-entity-*.recovery").Single();
        File.WriteAllBytes(evidence, [0]);

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryRemember("/agent/privacy-probe", ["replacement"]));

        response.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
        response.JournalPath.Should().NotContain(scope.WorkspaceDir);
    }

    [Fact]
    public async Task Remember_RollbackSafeFailureDoesNotPublishAnEvent()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var sink = new RecordingSink();
        IMemoryEventSink? prior = MemoryEventSinkContext.Current;
        MemoryEventSinkContext.Current = sink;
        try
        {
            var tools = new ScriniaMcpTools();
            await tools.MemoryRemember("/transaction/event", ["old"]);
            await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
            sink.Stored.Clear();
            store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

            McpResponseAssert response = ResponseParser.Parse(
                await tools.MemoryRemember("/transaction/event", ["new"]));

            response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
            await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
            sink.Stored.Should().BeEmpty();
        }
        finally
        {
            MemoryEventSinkContext.Current = prior;
        }
    }

    private sealed class RecordingSink : IMemoryEventSink
    {
        public List<(string Name, string[] Content)> Stored { get; } = [];

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        {
            Stored.Add((address.ToString(), content));
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
