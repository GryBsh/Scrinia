using System.Text.Json;
using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;

namespace Scrinia.Tests;

public sealed class BundleAtomicRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scrinia-bundle-atomic-" + Guid.NewGuid().ToString("N"));

    public BundleAtomicRestoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task InvalidPlanLeavesOriginalMembersAndNoJournal()
    {
        using var store = new FileMemoryStore(_root);
        Seed(store, "original");
        var plan = Plan("replacement", new BundleFileEntityRestore("unknown", "planner.md", "new"u8.ToArray(), null));

        Func<Task> act = () => store.RestoreBundleAsync(plan);

        await act.Should().ThrowAsync<BundleImportException>()
            .Where(exception => exception.Failure == BundleImportFailure.InvalidBundle);
        ReadArtifact(store, "note").Should().Be("original");
        File.Exists(Path.Combine(_root, ".scrinia", ".bundle-restore-journal.json")).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupFailureRetainsCommittedDecisionAndReturnsRealCounts()
    {
        using var store = new FileMemoryStore(_root);
        Seed(store, "original");
        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
            transition == RestoreJournalTransition.BeforeCleanupDelete ? new IOException("cleanup") : null;
        BundleRestoreResult result = await store.RestoreBundleAsync(Plan("replacement"));
        result.Outcome.Should().Be(BundleRestoreOutcome.Committed);
        result.EntryCount.Should().Be(1);
        string path = Path.Combine(_root, ".scrinia", ".bundle-restore-journal.json");
        File.ReadAllText(path).Should().Contain("\"state\":\"committed\"");
        Func<Task> blocked = () => store.ReadArtifactAsync(MemoryAddress.Parse("/incoming/note"));
        await blocked.Should().ThrowAsync<BundleImportException>();
        FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        ReadArtifact(store, "note").Should().Be("replacement");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task RollbackFailureBlocksTheNextImport()
    {
        using var store = new FileMemoryStore(_root);
        Seed(store, "original");
        string sentinel = Path.Combine(_root, ".scrinia", "memories", "incoming", "note.nmp2");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "sentinel");
        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
        {
            if (transition != RestoreJournalTransition.AfterPreparedWrite)
                return null;
            File.Delete(sentinel);
            Directory.CreateDirectory(sentinel);
            return new IOException("injected mutation failure");
        };

        Func<Task> first = () => store.RestoreBundleAsync(Plan("replacement"));
        await first.Should().ThrowAsync<BundleImportException>()
            .Where(exception => exception.Failure == BundleImportFailure.RecoveryRequired);

        Func<Task> second = () => store.RestoreBundleAsync(Plan("second"));
        await second.Should().ThrowAsync<BundleImportException>()
            .Where(exception => exception.Failure == BundleImportFailure.RecoveryRequired);
    }

    [Fact]
    public async Task ConcurrentWriteWaitsForRestoreAndSurvivesRollback()
    {
        using var store = new FileMemoryStore(_root);
        Seed(store, "original");
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
        {
            if (transition != RestoreJournalTransition.AfterPreparedWrite)
                return null;

            prepared.SetResult();
            release.Task.GetAwaiter().GetResult();
            return new IOException("injected restore failure");
        };

        Task<BundleRestoreResult> restore = Task.Run(() => store.RestoreBundleAsync(Plan("replacement")));
        await prepared.Task;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task write = Task.Run(() =>
        {
            entered.SetResult();
            return store.WriteArtifactAsync("concurrent",
                MemoryNaming.BuildScopedTopicScope("incoming"), Nmp2ChunkedEncoder.Encode("must survive"));
        });

        await entered.Task;
        write.IsCompleted.Should().BeFalse();
        release.SetResult();

        Func<Task> restoreAction = async () => await restore;
        await restoreAction.Should().ThrowAsync<BundleImportException>()
            .Where(exception => exception.Failure == BundleImportFailure.RolledBack);
        await write;
        ReadArtifact(store, "concurrent").Should().Be("must survive");
    }

    [Fact]
    public async Task ColdStartRefusesLegacyPreparedJournal()
    {
        SeedJournalWithOriginalSentinel();
        using var store = new FileMemoryStore(_root);
        File.ReadAllText(Path.Combine(_root, ".scrinia", "skills", "sentinel.md")).Should().Be("changed");
        Func<Task> read = () => store.ReadArtifactAsync(MemoryAddress.Parse("/note"));
        await read.Should().ThrowAsync<BundleImportException>()
            .Where(e => e.Failure == BundleImportFailure.RecoveryRequired);
        File.Exists(Path.Combine(_root, ".scrinia", ".bundle-restore-journal.json")).Should().BeTrue();
    }

    [Fact]
    public void ColdStartRejectsUnsafeJournalBeforeDeletingAnyWorkspaceFile()
    {
        string scrinia = Path.Combine(_root, ".scrinia");
        string sentinel = Path.Combine(scrinia, "skills", "sentinel.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "sentinel");

        string journal = JsonSerializer.Serialize(new
        {
            format = "scrinia.bundle-restore",
            state = "prepared",
            files = new[]
            {
                new
                {
                    relativePath = "../../outside.md",
                    existed = true,
                    contentBase64 = Convert.ToBase64String("outside"u8.ToArray())
                }
            }
        });
        File.WriteAllText(Path.Combine(scrinia, ".bundle-restore-journal.json"), journal);

        using var store = new FileMemoryStore(_root);

        File.ReadAllText(sentinel).Should().Be("sentinel");
        File.Exists(Path.Combine(scrinia, ".bundle-restore-journal.json")).Should().BeTrue();
    }

    private BundleRestorePlan Plan(string content, BundleFileEntityRestore? file = null) =>
        new([new BundleTopicRestore(
            MemoryNaming.BuildScopedTopicScope("incoming"), "incoming",
            [new("note", "", content.Length, 1, DateTimeOffset.UtcNow)],
            new Dictionary<string, byte[]> { ["note"] = Encoding.UTF8.GetBytes(Nmp2ChunkedEncoder.Encode(content)) })],
            file is null ? [] : [file], Overwrite: true);

    private void Seed(FileMemoryStore store, string content)
    {
        string scope = MemoryNaming.BuildScopedTopicScope("incoming");
        store.Upsert(new("note", "", content.Length, 1, DateTimeOffset.UtcNow), scope);
        store.WriteArtifactAsync("note", scope, Nmp2ChunkedEncoder.Encode(content)).GetAwaiter().GetResult();
    }

    private static string ReadArtifact(FileMemoryStore store, string name)
    {
        string encoded = store.ReadArtifactAsync(MemoryAddress.Parse("/incoming/" + name)).GetAwaiter().GetResult();
        return Nmp2ChunkedEncoder.DecodeAllChunksVerified(encoded).Chunks.Single();
    }

    private void SeedJournalWithOriginalSentinel()
    {
        string directory = Path.Combine(_root, ".scrinia", "skills");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "sentinel.md");
        File.WriteAllText(path, "changed");
        string relative = Path.GetRelativePath(Path.Combine(_root, ".scrinia"), path);
        string journal = JsonSerializer.Serialize(new
        {
            format = "scrinia.bundle-restore",
            state = "prepared",
            files = new[] { new { relativePath = relative, existed = true, contentBase64 = Convert.ToBase64String("original"u8.ToArray()) } }
        });
        File.WriteAllText(Path.Combine(_root, ".scrinia", ".bundle-restore-journal.json"), journal);
    }
}
