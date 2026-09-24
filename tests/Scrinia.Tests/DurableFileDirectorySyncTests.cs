using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class DurableFileDirectorySyncTests
{
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
