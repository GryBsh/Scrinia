using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public sealed class WholeMemoryStoreTransactionTests
{
    [Fact]
    public async Task Compact_LiveSidecarFailureMapsStructuredDurableFailureWithoutEvent()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var tools = new ScriniaMcpTools();
        var sink = new RecordingSink();
        MemoryEventSinkContext.Current = sink;
        await tools.Remember(["old one", "old two"], MemoryAddress.Parse("/target"));
        sink.Stored.Clear();
        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        McpResponse response = await tools.Compact(MemoryAddress.Parse("/target"));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        response.OperationId.Should().NotBeNullOrWhiteSpace();
        response.Action.Should().BeNull();
        sink.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Compact_ArchiveCleanupWarningMapsToSuccessWithWarning()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var tools = new ScriniaMcpTools();
        await tools.Remember(["old one", "old two"], MemoryAddress.Parse("/target"));
        store.FailOnceAt(WholeMemoryFaultPoint.ArchiveCleanup);

        McpResponse response = await tools.Compact(MemoryAddress.Parse("/target"));

        response.Status.Should().Be("success_with_warning");
        response.Action.Should().Be("compacted");
        response.OperationId.Should().NotBeNullOrWhiteSpace();
        response.Content.Should().Contain("Original archived.");
    }

    [Fact]
    public async Task Compact_RollbackFailureMapsRecoveryEvidenceAndSuppressesEvent()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var tools = new ScriniaMcpTools();
        var sink = new RecordingSink();
        MemoryEventSinkContext.Current = sink;
        await tools.Remember(["old one", "old two"], MemoryAddress.Parse("/target"));
        sink.Stored.Clear();
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) => point switch
        {
            WholeMemoryFaultPoint.LiveArtifactWrite or WholeMemoryFaultPoint.RollbackWrite =>
                new IOException($"Injected whole-memory fault at {point}."),
            _ => null
        };

        McpResponse response = await tools.Compact(MemoryAddress.Parse("/target"));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
        response.OperationId.Should().NotBeNullOrWhiteSpace();
        response.Scope.Should().Be("local");
        response.JournalPath.Should().NotBeNullOrWhiteSpace();
        response.JournalPath.Should().EndWith(AppendJournal.FileName);
        sink.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Compact_KeepRecentPreservesMetadataAndPublishesTheCommittedEvent()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var tools = new ScriniaMcpTools();
        var sink = new RecordingSink();
        MemoryEventSinkContext.Current = sink;
        await tools.Remember(["first", "second", "third"], MemoryAddress.Parse("/target"),
            attributes: new Dictionary<string, string[]> { ["tag"] = ["kept"] });
        sink.Stored.Clear();

        McpResponse response = await tools.Compact(MemoryAddress.Parse("/target"), keepRecent: 2);

        response.Status.Should().Be("success");
        store.LoadIndex("local").Single(entry => entry.Name == "target")
            .Attributes!["tag"].Should().Contain("kept");
        Nmp2ChunkedEncoder.GetChunkCount(await store.ReadArtifactAsync(MemoryAddress.Parse("/target")))
            .Should().Be(2);
        sink.Stored.Should().ContainSingle();
    }

    [Fact]
    public async Task CompactWrite_LiveSidecarFailurePreservesPair()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old one", "old two"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));
        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["compacted content"])))
            .Should().ThrowAsync<WholeMemoryTransactionException>();
        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(oldBody);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task StoreAsync_VerificationFailureRestoresSameInstanceReadSearchAndListViews()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old marker"]));

        // Warm every read surface before the failed publication so each assertion exercises the
        // same instance's cache invalidation rather than a cold read from disk.
        store.LoadIndex("local").Should().ContainSingle(entry => entry.Name == "target");
        store.ListScoped(["local"]).Should().ContainSingle(item => item.Entry.Name == "target");
        store.SearchAll(new SearchQuery("old marker")).OfType<EntryResult>()
            .Should().ContainSingle(result => result.Item.Entry.Name == "target");

        store.FailOnceAt(WholeMemoryFaultPoint.VerificationRead);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new marker"])))
            .Should().ThrowAsync<WholeMemoryTransactionException>();

        store.LoadIndex("local").Should().ContainSingle(entry => entry.Name == "target");
        store.ListScoped(["local"]).Should().ContainSingle(item => item.Entry.Name == "target");
        store.SearchAll(new SearchQuery("old marker")).OfType<EntryResult>()
            .Should().ContainSingle(result => result.Item.Entry.Name == "target");
        store.SearchAll(new SearchQuery("newercontent")).Should().BeEmpty();
    }

    [Fact]
    public async Task Compact_SuccessArchivesThePreviousPairExactlyOnce()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var tools = new ScriniaMcpTools();
        await tools.Remember(["old one", "old two"], MemoryAddress.Parse("/target"));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));

        McpResponse response = await tools.Compact(MemoryAddress.Parse("/target"));

        response.Status.Should().Be("success");
        response.Content.Should().Contain("Original archived.");
        string versions = Path.Combine(store.GetStoreDirForScope("local"), "versions");
        string[] archivedBodies = Directory.GetFiles(versions, "target_*.nmp2");
        string[] archivedSidecars = Directory.GetFiles(versions, "target_*.meta.json");
        archivedBodies.Should().ContainSingle();
        archivedSidecars.Should().ContainSingle();
        File.ReadAllBytes(archivedBodies[0]).Should().Equal(oldBody);
        File.ReadAllBytes(archivedSidecars[0]).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task Compact_EphemeralDoesNotCreatePersistentTransactionEvidence()
    {
        using var workspace = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.Remember(["old one", "old two"], MemoryAddress.Parse("~target"));

        McpResponse response = await tools.Compact(MemoryAddress.Parse("~target"));

        response.Status.Should().Be("success");
        Directory.EnumerateFiles(workspace.WorkspaceDir, AppendJournal.FileName, SearchOption.AllDirectories)
            .Should().BeEmpty();
        Directory.EnumerateFiles(workspace.WorkspaceDir, "*.nmp2", SearchOption.AllDirectories)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData((int)WholeMemoryFaultPoint.RawArtifactRead)]
    [InlineData((int)WholeMemoryFaultPoint.RawSidecarRead)]
    [InlineData((int)WholeMemoryFaultPoint.EvidenceWrite)]
    [InlineData((int)WholeMemoryFaultPoint.ArchiveArtifactWrite)]
    [InlineData((int)WholeMemoryFaultPoint.ArchiveSidecarWrite)]
    [InlineData((int)WholeMemoryFaultPoint.LiveArtifactWrite)]
    [InlineData((int)WholeMemoryFaultPoint.LiveSidecarWrite)]
    [InlineData((int)WholeMemoryFaultPoint.VerificationRead)]
    public async Task StoreAsync_PreCommitFailureRestoresTheOriginalPair(int faultPointValue)
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));
        store.FailOnceAt((WholeMemoryFaultPoint)faultPointValue);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<IOException>();

        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(oldBody);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task StoreAsync_LiveSidecarFailureRestoresBothMembersByteForByte()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));

        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<IOException>();

        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(oldBody);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task StoreAsync_PersistentWriteCommitsArtifactAndSidecarTogether()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;

        ArtifactEntry entry = await store.StoreAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        entry.Name.Should().Be("target");
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
        File.Exists(store.MetaPath("target")).Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_CleanupStateFailureKeepsCommittedPairAndEvidence()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        int cleanupFaults = 0;
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) =>
            (point is WholeMemoryFaultPoint.EvidenceCleanup or WholeMemoryFaultPoint.CleanupStateWrite)
                && Interlocked.Increment(ref cleanupFaults) <= 2
                ? new IOException($"Injected cleanup fault at {point}.")
                : null;

        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        File.ReadAllBytes(store.ArtifactPath("target")).Should().NotEqual(oldBody);
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName))
            .Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_CommittedEvidenceIsRecoveredByAFreshStore()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        store.FailOnceAt(WholeMemoryFaultPoint.EvidenceCleanup);

        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        string journalPath = Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName);
        File.Exists(journalPath).Should().BeTrue();
        using var freshStore = new FileMemoryStore(workspace.WorkspaceDir);
        await freshStore.RecoverAppendAsync("local");

        File.Exists(journalPath).Should().BeFalse();
        ArtifactEntry recovered = freshStore.LoadIndex("local")
            .Should().ContainSingle(entry => entry.Name == "target").Subject;
        recovered.Name.Should().Be("target");
    }

    [Fact]
    public async Task StoreAsync_ArchiveCleanupFailureRetainsCommittedEvidenceWithoutFailingWrite()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) =>
            point == WholeMemoryFaultPoint.ArchiveCleanup
                ? new IOException("Injected archive cleanup failure.")
                : null;

        ArtifactEntry entry = await store.StoreAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        entry.Name.Should().Be("target");
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName))
            .Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_WhenCleanupStatePersistenceFailsFreshRecoveryRetriesCommittedCleanup()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) => point switch
        {
            WholeMemoryFaultPoint.ArchiveCleanup or WholeMemoryFaultPoint.CleanupStateWrite =>
                new IOException($"Injected whole-memory fault at {point}."),
            _ => null
        };

        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));
        string journalPath = Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName);
        File.Exists(journalPath).Should().BeTrue();
        FileMemoryStore.WholeMemoryFaultInjector = null;

        using var freshStore = new FileMemoryStore(workspace.WorkspaceDir);
        freshStore.FailOnceAt(AppendFaultPoint.ArchiveCleanup);
        await FluentActions.Invoking(() => freshStore.RecoverAppendAsync("local"))
            .Should().ThrowAsync<AppendStoreException>();
        File.Exists(journalPath).Should().BeTrue();

        freshStore.ClearFaults();
        await freshStore.RecoverAppendAsync("local");
        File.Exists(journalPath).Should().BeFalse();
        File.Exists(freshStore.ArtifactPath("target")).Should().BeTrue();
        File.Exists(freshStore.MetaPath("target")).Should().BeTrue();
    }

    [Fact]
    public void PruneWholeArchives_UsesOnlyTheFrozenCandidateList()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string versions = Path.Combine(store.GetStoreDirForScope("local"), "versions");
        Directory.CreateDirectory(versions);
        string frozenArtifact = Path.Combine(versions, "target_frozen.nmp2");
        string frozenSidecar = Path.Combine(versions, "target_frozen.meta.json");
        string laterArtifact = Path.Combine(versions, "target_later.nmp2");
        File.WriteAllBytes(frozenArtifact, [1]);
        File.WriteAllBytes(frozenSidecar, [2]);
        File.WriteAllBytes(laterArtifact, [3]);

        string[] failures = store.PruneWholeArchives([frozenArtifact, frozenSidecar], "test-operation");

        failures.Should().BeEmpty();
        File.Exists(frozenArtifact).Should().BeFalse();
        File.Exists(frozenSidecar).Should().BeFalse();
        File.Exists(laterArtifact).Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_RetainsOnlyTheConfiguredArchiveWindow()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        for (int version = 0; version < 12; version++)
            await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", [$"old-{version}"]));

        Directory.EnumerateFiles(Path.Combine(store.GetStoreDirForScope("local"), "versions"), "target_*.nmp2")
            .Should().HaveCount(10);
    }

    [Fact]
    public async Task StoreAsync_ArchiveSidecarFailureRemovesUnpairedArchive()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        store.FailOnceAt(WholeMemoryFaultPoint.ArchiveSidecarWrite);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<WholeMemoryTransactionException>();

        string versions = Path.Combine(store.GetStoreDirForScope("local"), "versions");
        if (Directory.Exists(versions))
            Directory.GetFiles(versions, "*.nmp2").Should().BeEmpty();
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_RollbackFailureRetainsRecoveryEvidence()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) => point switch
        {
            WholeMemoryFaultPoint.LiveArtifactWrite or WholeMemoryFaultPoint.RollbackWrite =>
                new IOException($"Injected whole-memory fault at {point}."),
            _ => null
        };

        WholeMemoryTransactionException exception = (await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<WholeMemoryTransactionException>()).Which;

        exception.Code.Should().Be(WholeMemoryErrorCode.RecoveryRequired);
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName))
            .Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_CancellationBeforeCommitRestoresTheOriginalPair()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));
        using var cancellation = new CancellationTokenSource();
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) =>
            point == WholeMemoryFaultPoint.LiveArtifactWrite
                ? CancelAndReturn(cancellation)
                : null;

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(oldBody);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task StoreAsync_CancellationAfterCommitStillReturnsCommittedPair()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        using var cancellation = new CancellationTokenSource();
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) =>
            point == WholeMemoryFaultPoint.VerificationRead
                ? CancelAndReturn(cancellation)
                : null;

        ArtifactEntry entry = await store.StoreAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]), cancellation.Token);

        entry.Name.Should().Be("target");
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
        File.Exists(store.MetaPath("target")).Should().BeTrue();
    }

    private static Exception? CancelAndReturn(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        return null;
    }

    private sealed class RecordingSink : IMemoryEventSink
    {
        public List<MemoryAddress> Stored { get; } = [];

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        {
            Stored.Add(address);
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
