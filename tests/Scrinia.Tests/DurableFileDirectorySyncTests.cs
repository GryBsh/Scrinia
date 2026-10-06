using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class DurableFileDirectorySyncTests
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, true)]
    public void DeleteAndSyncFaultPreservesObservableDeletion(
        int phaseValue, bool exists, bool syncAttempted)
    {
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", "evidence.json");
        File.WriteAllText(path, "evidence");
        bool synced = false;
        DurableFile.FailureInjector = (phase, _) => { synced |= phase == DurableWritePhase.BeforeDirSync; return null; };
        DurableFile.DeleteFailureInjector = (phase, _) => phase == (DurableDeletePhase)phaseValue ? new IOException("delete fault") : null;
        try
        {
            Action delete = () => DurableFile.DeleteAndSync(path);
            delete.Should().Throw<IOException>();
            File.Exists(path).Should().Be(exists);
            synced.Should().Be(syncAttempted);
            Directory.Exists(Path.GetDirectoryName(path)).Should().BeTrue();
        }
        finally
        {
            DurableFile.FailureInjector = null;
            DurableFile.DeleteFailureInjector = null;
        }
    }

    [Fact]
    public void DeleteAndSyncDeletesOnlyEvidenceAndSyncsParent()
    {
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", "evidence.json");
        string other = Path.Combine(scope.WorkspaceDir, ".scrinia", "other.json");
        File.WriteAllText(path, "evidence");
        File.WriteAllText(other, "keep");
        var phases = new List<DurableDeletePhase>();
        DurableFile.DeleteFailureInjector = (phase, _) => { phases.Add(phase); return null; };
        try { DurableFile.DeleteAndSync(path); }
        finally { DurableFile.DeleteFailureInjector = null; }
        File.Exists(path).Should().BeFalse();
        File.ReadAllText(other).Should().Be("keep");
        phases.Should().Equal(DurableDeletePhase.BeforeDelete, DurableDeletePhase.AfterDelete,
            DurableDeletePhase.BeforeDirSync, DurableDeletePhase.AfterDirSync);
    }

    [Fact]
    public async Task AppendAsync_DirectoryIsFsyncedAfterAtomicRename()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var phases = new List<DurableWritePhase>();

        DurableFile.FailureInjector = (phase, _) =>
        {
            phases.Add(phase);
            return null;
        };

        try
        {
            AppendStoreResult result = await store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/synced"), "local", ["body"]));

            result.Outcome.Should().Be(AppendStoreOutcome.Created);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        phases.Should().Contain(DurableWritePhase.BeforeDirSync);
    }

    [Fact]
    public async Task AppendAsync_FirstDirSyncFailureRollsBackCreateMembers()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        string unrelatedPath = Path.Combine(scope.WorkspaceDir, "unrelated.txt");
        await File.WriteAllTextAsync(unrelatedPath, "preserve me");

        // Fault the directory-sync of the live member's durable move (the append's atomically
        // moved artifact/sidecar land in the scope memory directory, unlike the workspace
        // .gitignore or the versions/ journal writes). That is the durable move whose failure
        // must surface as DurableWriteException and roll the create back.
        string memberDirectory = store.GetStoreDirForScope("local");
        DurableFile.FailureInjector = (phase, destination) =>
            phase == DurableWritePhase.BeforeDirSync && destination == memberDirectory
                ? new IOException("Injected dir-sync failure.")
                : null;

        try
        {
            await FluentActions.Invoking(() => store.AppendAsync(
                    MemoryWrite.Create(MemoryAddress.Parse("/new-target"), "local", ["created"])))
                .Should().ThrowAsync<DurableWriteException>();

            File.Exists(store.FindArtifactPath("new-target", "local")).Should().BeFalse();
            File.Exists(store.MetaPath("new-target")).Should().BeFalse();
            (await File.ReadAllTextAsync(unrelatedPath)).Should().Be("preserve me");

            string journalPath = Path.Combine(
                store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName);
            File.Exists(journalPath).Should().BeFalse();
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }
    }
}
