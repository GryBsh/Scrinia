using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AppendDurabilityTests
{
    [Fact]
    public async Task AppendAsync_MissingPersistentTargetDelegatesToStoreSemantics()
    {
        using var workspace = new TempWorkspace();

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/created"), "local", ["body"],
                keywords: ["caller-keyword"]));

        result.Outcome.Should().Be(AppendStoreOutcome.Created);
        result.Entry.Should().NotBeNull();
        result.Entry!.Keywords.Should().Contain("caller-keyword");
        result.Entry.UpdatedAt.Should().BeNull();
        result.Entry.Address.Should().Be(MemoryAddress.Parse("/created"));
        Directory.EnumerateFiles(Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions"),
                "*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_CreateJournalDeleteFailureRetainsCommittedPairAndReturnsWarning()
    {
        using var workspace = new TempWorkspace();
        workspace.Store.FailOnceAt(AppendFaultPoint.JournalDelete);

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/committed"), "local", ["body"]));

        result.Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);
        result.Warning!.Code.Should().Be(AppendWarningCode.PostCommitCleanupFailed);
        result.OperationId.Should().NotBeNullOrWhiteSpace();
        result.Entry.Should().NotBeNull();
        File.Exists(workspace.Store.ArtifactPath("committed")).Should().BeTrue();
        File.Exists(workspace.Store.MetaPath("committed")).Should().BeTrue();
        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName);
        File.Exists(journalPath).Should().BeTrue();
        (await File.ReadAllTextAsync(journalPath)).Should().Contain("\"state\":\"committed\"");
    }

    [Fact]
    public async Task AppendAsync_ExistingJournalDeleteIOExceptionRetainsCommittedPairAndReturnsWarning()
    {
        using var workspace = new TempWorkspace();
        string unrelatedPath = Path.Combine(workspace.Root, "unrelated.txt");
        await File.WriteAllTextAsync(unrelatedPath, "preserve me");
        await workspace.Store.StoreAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/existing"), "local", ["before"]));

        FileMemoryStore.AppendFaultInjector = (point, _, _) =>
        {
            if (point == AppendFaultPoint.JournalDelete)
                return new IOException("Injected journal-delete IOException.");
            return null;
        };

        try
        {
            AppendStoreResult result = await workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/existing"), "local", ["after"]));

            result.Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);
            result.Warning!.Code.Should().Be(AppendWarningCode.PostCommitCleanupFailed);
            result.OperationId.Should().NotBeNullOrWhiteSpace();
            result.Entry.Should().NotBeNull();
            (await workspace.Store.ReadAsync(MemoryAddress.Parse("/existing")))!.ChunkCount.Should().Be(2);

            string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName);
            File.Exists(journalPath).Should().BeTrue();
            (await File.ReadAllTextAsync(journalPath)).Should().Contain("\"state\":\"cleanupPending\"");
            (await File.ReadAllTextAsync(unrelatedPath)).Should().Be("preserve me");
        }
        finally
        {
            FileMemoryStore.AppendFaultInjector = null;
        }
    }

    [Fact]
    public async Task AppendAsync_NormalCreateRemovesJournalAfterCommittedPairIsVerified()
    {
        using var workspace = new TempWorkspace();

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/normal"), "local", ["body"]));

        result.Outcome.Should().Be(AppendStoreOutcome.Created);
        result.Warning.Should().BeNull();
        result.Recovery.Should().BeNull();
        File.Exists(Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName))
            .Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_MissingEphemeralTargetCreatesItWithoutPersistentFiles()
    {
        using var workspace = new TempWorkspace();

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("~scratch"), "local", ["body"]));

        result.Outcome.Should().Be(AppendStoreOutcome.Created);
        result.Entry!.Address.Should().Be(MemoryAddress.Parse("~scratch"));
        workspace.Store.GetEphemeral("scratch").Should().NotBeNull();
        Directory.EnumerateFiles(workspace.Root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_ExistingEphemeralTargetAppendsUsingEphemeralStoreSemantics()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("~scratch"), "local", ["old"]));

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("~scratch"), "local", ["new"]));

        result.Outcome.Should().Be(AppendStoreOutcome.Appended);
        result.Entry!.ChunkCount.Should().Be(2);
        workspace.Store.GetEphemeral("scratch")!.Artifact.Should().NotBeNullOrWhiteSpace();
        Directory.EnumerateFiles(workspace.Root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "scrinia-append-durability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new FileMemoryStore(Root);
        }

        public string Root { get; }
        public FileMemoryStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
