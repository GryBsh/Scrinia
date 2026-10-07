using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class BundleEntitySnapshotTests
{
    [Theory]
    [InlineData("skill", false, false, false)]
    [InlineData("agent", false, false, false)]
    [InlineData("skill", true, false, false)]
    [InlineData("agent", true, false, false)]
    [InlineData("skill", true, true, false)]
    [InlineData("agent", true, true, false)]
    [InlineData("skill", false, false, true)]
    [InlineData("agent", false, false, true)]
    public async Task ExportRefusesAnActualPublisherBetweenBodyAndMetadata(
        string kind, bool rollback, bool callerArchive, bool genericStore)
    {
        using var workspace = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        var paths = Paths(TestHelpers.Store, kind);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "{\"name\":\"qa\",\"description\":\"old metadata\"}");
        string destination = Path.Combine(workspace.WorkspaceDir, "backup.scrinia-bundle");
        byte[] priorBackup = "existing backup"u8.ToArray();
        File.WriteAllBytes(destination, priorBackup);
        using var betweenMembers = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int paused = 0;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase != DurableWritePhase.BeforeWrite || path != paths.Meta ||
                Interlocked.CompareExchange(ref paused, 1, 0) != 0)
                return null;
            betweenMembers.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(30)))
                return new TimeoutException("The test did not release the paused publisher.");
            return rollback ? new UnauthorizedAccessException("Injected metadata publication failure.") : null;
        };
        Task<FileEntityMutationResult> publisher = Task.Run(() =>
            new FileEntityTransactionCoordinator(workspace.WorkspaceDir).ExecuteAsync(Mutation(kind, paths)));
        Exception? refusal = null;
        try
        {
            betweenMembers.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            File.ReadAllText(paths.Body).Should().Be("new body");
            File.ReadAllText(paths.Meta).Should().Contain("old metadata");
            IMemoryStore source = genericStore ? new StoreView(TestHelpers.Store) : TestHelpers.Store;
            refusal = Record.Exception(() => Export(source, destination, callerArchive));
        }
        finally
        {
            resume.Set();
            DurableFile.FailureInjector = null;
            if (rollback)
                await FluentActions.Awaiting(() => publisher).Should().ThrowAsync<WholeMemoryTransactionException>();
            else
                (await publisher).Outcome.Should().Be(WholeMemoryOutcome.Replaced);
        }

        refusal.Should().BeOfType<FileLockTimeoutException>().Which.LockPath
            .Should().Be(Path.Combine(paths.Directory, ".lock"));
        File.ReadAllBytes(destination).Should().Equal(priorBackup);
        BundleFormatService.ExportTopicsToZip(destination, TestHelpers.Store, ["api"]);
        using var zip = ZipFile.OpenRead(destination);
        string category = kind == "skill" ? "skills" : "agent";
        Read(zip, $"files/{category}/qa.md").Should().Be(rollback ? "old body" : "new body");
        Read(zip, $"files/{category}/qa.meta.json").Should().Be(rollback
            ? "{\"name\":\"qa\",\"description\":\"old metadata\"}"
            : "{\"name\":\"qa\",\"description\":\"new metadata\"}");
    }

    [Theory]
    [InlineData("skill", false)]
    [InlineData("agent", false)]
    [InlineData("skill", true)]
    [InlineData("agent", true)]
    public async Task ExportRefusesRetainedPreparedEvidenceAndPreservesTheBackup(
        string kind, bool genericStore)
    {
        using var workspace = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        var paths = Paths(TestHelpers.Store, kind);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "{\"name\":\"qa\",\"description\":\"old metadata\"}");
        var coordinator = new FileEntityTransactionCoordinator(workspace.WorkspaceDir);
        coordinator.FailOnceAt(FileEntityFaultPoint.MetaWrite);
        coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);
        FileEntityMutationResult failed;
        try { failed = await coordinator.ExecuteAsync(Mutation(kind, paths)); }
        finally { B0FaultSeam.ClearFaults(); }
        failed.Outcome.Should().Be(WholeMemoryOutcome.RecoveryRequired);
        string evidencePath = failed.Recovery!.EvidencePath;
        byte[] evidence = File.ReadAllBytes(evidencePath);
        byte[] body = File.ReadAllBytes(paths.Body);
        byte[] meta = File.ReadAllBytes(paths.Meta);
        string destination = Path.Combine(workspace.WorkspaceDir, "backup.scrinia-bundle");
        byte[] priorBackup = "existing backup"u8.ToArray();
        File.WriteAllBytes(destination, priorBackup);
        IMemoryStore source = genericStore ? new StoreView(TestHelpers.Store) : TestHelpers.Store;

        Action export = () => BundleFormatService.ExportTopicsToZip(destination, source, ["api"]);

        RestoreRecoveryException refusal = export.Should().Throw<RestoreRecoveryException>().Which;
        refusal.DiscoveryBlock.Should().NotBeNull();
        refusal.JournalPath.Should().Be(evidencePath);
        File.ReadAllBytes(destination).Should().Equal(priorBackup);
        File.ReadAllBytes(evidencePath).Should().Equal(evidence);
        File.ReadAllBytes(paths.Body).Should().Equal(body);
        File.ReadAllBytes(paths.Meta).Should().Equal(meta);
    }

    [Fact]
    public void GenericStoreTakesWorkspaceAdmissionBeforeReadingTopics()
    {
        using var workspace = new TestHelpers.StoreScope(createStoreDir: false);
        Directory.CreateDirectory(workspace.WorkspaceDir);
        File.WriteAllText(Path.Combine(workspace.WorkspaceDir, ".scrinia"), "not a directory");
        using var store = new FileMemoryStore(workspace.WorkspaceDir);
        var source = new StoreView(store);
        using var output = new MemoryStream();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);

        Action export = () => BundleFormatService.ExportTopicsToZip(zip, source, ["api"]);

        export.Should().Throw<IOException>();
        source.IndexReads.Should().Be(0);
    }

    private static void Export(IMemoryStore store, string destination, bool callerArchive)
    {
        if (!callerArchive)
        {
            BundleFormatService.ExportTopicsToZip(destination, store, ["api"]);
            return;
        }
        using var output = new MemoryStream();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        BundleFormatService.ExportTopicsToZip(zip, store, ["api"]);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static EntityPaths Paths(IMemoryStore store, string kind)
    {
        string directory = PathRouter.ToFileEntityDir(kind, store.WorkspaceRoot);
        Directory.CreateDirectory(directory);
        return new(directory, Path.Combine(directory, "qa.md"), Path.Combine(directory, "qa.meta.json"),
            Path.Combine(directory, "versions"));
    }

    private static FileEntityMutation Mutation(string kind, EntityPaths paths) =>
        new(kind, "qa", paths.Body, paths.Meta, paths.Versions,
            Encoding.UTF8.GetBytes("new body"),
            Encoding.UTF8.GetBytes("{\"name\":\"qa\",\"description\":\"new metadata\"}"),
            deleteBody: false, deleteMeta: false);

    private sealed record EntityPaths(string Directory, string Body, string Meta, string Versions);

    private sealed class StoreView(IMemoryStore store) : FakeMemoryStore
    {
        internal int IndexReads { get; private set; }
        public override string WorkspaceRoot => store.WorkspaceRoot;
        public override IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local")
        {
            IndexReads++;
            return store.LoadIndex(scope);
        }
        public override List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope) =>
            store.ListTopicArtifacts(topicScope);
    }
}
