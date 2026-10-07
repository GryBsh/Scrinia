using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Formats;
using System.Security.Cryptography;

namespace Scrinia.Tests;

public sealed class BundleRecoveryOwnershipTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope = new();
    private string Root => _scope.WorkspaceDir;
    private string Journal => Path.Combine(Root, ".scrinia", ".bundle-restore-journal.json");
    private string Body => Path.Combine(Root, ".scrinia", "skills", "planner.md");
    private static BundleRestorePlan Plan(byte[]? metadata = null) => new([], [new("skill", "planner.md", "after"u8.ToArray(), metadata)], true);

    [Fact]
    public async Task BundleScopeRecoveryReusesRestoreLeaseAndRefusesUnprovableCoordinator()
    {
        using var store = new FileMemoryStore(Root);
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiMemoryTransactionRecordCodec.Write(directory, new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "retained", "local:notes",
            MultiMemoryTransactionState.Prepared,
            [new MultiMemoryParticipantManifest("note", "op-note", "memory")], now, now));
        string record = MultiMemoryTransactionRecordCodec.GetPath(directory);
        byte[] prior = File.ReadAllBytes(record);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Func<Task> restore = () => store.RestoreBundleAsync(new(
            [new("local:notes", "notes", [new("note", "", 5, 1, now)],
                new Dictionary<string, byte[]> { ["note"] = "after"u8.ToArray() })], [], true), timeout.Token);
        await restore.Should().ThrowAsync<RestoreRecoveryException>();
        timeout.IsCancellationRequested.Should().BeFalse();
        File.ReadAllBytes(record).Should().Equal(prior);
        File.Exists(Journal).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureMemorySidecarIsNeverOverwritten(bool incoming)
    {
        using var store = new FileMemoryStore(Root);
        string directory = PathRouter.ToStoreDir("local:notes", Root);
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "note.nmp2");
        string metadata = Path.Combine(directory, "note.meta.json");
        File.WriteAllText(body, "before");
        File.WriteAllText(metadata, incoming ? "{}" : """{"schemaVersion":999}""");
        byte[] prior = File.ReadAllBytes(metadata);
        var entry = new Scrinia.Core.Models.ArtifactEntry("note", "", 5, 1, DateTimeOffset.UtcNow)
            { SchemaVersion = incoming ? 999 : null };
        Func<Task> restore = () => store.RestoreBundleAsync(new(
            [new("local:notes", "notes", [entry], new Dictionary<string, byte[]> { ["note"] = "after"u8.ToArray() })], [], true));
        var refusal = (await restore.Should().ThrowAsync<BundleImportException>()).Which;
        if (incoming) refusal.InnerException.Should().BeOfType<BundleValidationException>();
        else refusal.InnerException.Should().BeOfType<ScopeReadOnlyException>();
        File.ReadAllText(body).Should().Be("before");
        File.ReadAllBytes(metadata).Should().Equal(prior);
        File.Exists(Journal).Should().BeFalse();
    }

    [Fact]
    public async Task CancellationAfterPublicationCompensatesWithIndependentDeadline()
    {
        Seed("before");
        using var cancelled = new CancellationTokenSource();
        BundleRestoreCoordinator.FailureInjector.Value = (at, _) =>
        {
            if (at == BundleFaultPoint.AfterMember) cancelled.Cancel();
            return null;
        };
        using var store = new FileMemoryStore(Root);
        Func<Task> restore = () => store.RestoreBundleAsync(Plan(), cancelled.Token);
        await restore.Should().ThrowAsync<BundleImportException>().Where(e => e.Failure == BundleImportFailure.RolledBack);
        File.ReadAllText(Body).Should().Be("before");
        File.Exists(Journal).Should().BeFalse();
    }

    [Theory]
    [InlineData((int)BundleFaultPoint.BeforeRollbackDecision)]
    [InlineData((int)BundleFaultPoint.AfterRollbackDecision)]
    public async Task UncertainRollbackDecisionRetainsRecoverableEvidence(int point)
    {
        Seed("before");
        BundleRestoreCoordinator.FailureInjector.Value = (at, _) =>
            at == BundleFaultPoint.AfterMember || at == (BundleFaultPoint)point ? new IOException("injected") : null;
        using var store = new FileMemoryStore(Root);
        Func<Task> restore = () => store.RestoreBundleAsync(Plan());
        await restore.Should().ThrowAsync<BundleImportException>().Where(e => e.Failure == BundleImportFailure.RecoveryRequired);
        File.ReadAllText(Body).Should().Be("before");
        File.Exists(Journal).Should().BeTrue();
        BundleRestoreCoordinator.FailureInjector.Value = null;
        store.ListScoped();
        File.Exists(Journal).Should().BeFalse();
    }

    [Fact]
    public async Task PersistentScopeCleanupFailureAllowsOneVerifiedAsyncRead()
    {
        using var store = new FileMemoryStore(Root);
        int attempts = 0;
        FileMemoryStore.RestoreJournalFailureInjector.Value = (at, _) =>
        {
            if (at != RestoreJournalTransition.BeforeCleanupDelete) return null;
            attempts++;
            return new IOException("scope cleanup remains unavailable");
        };
        store.ImportTopicEntries("local:scope", [new("note", "", 4, 1, DateTimeOffset.UtcNow)],
            new Dictionary<string, byte[]> { ["note"] = "body"u8.ToArray() }, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        (await store.ReadArtifactAsync(Scrinia.Core.Models.MemoryAddress.Parse("/scope/note"), timeout.Token)).Should().Be("body");
        attempts.Should().Be(2);
        File.Exists(Path.Combine(Root, ".scrinia", "memories", "scope", ".restore-journal.json")).Should().BeTrue();
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task ForwardVersionedDestinationIsPreserved(string kind)
    {
        string directory = PathRouter.ToFileEntityDir(kind, Root);
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "planner.md");
        string metadata = Path.ChangeExtension(body, ".meta.json");
        File.WriteAllText(body, "before");
        File.WriteAllText(metadata, """{"schemaVersion":999,"future":"keep"}""");
        byte[] prior = File.ReadAllBytes(metadata);
        using var store = new FileMemoryStore(Root);
        Func<Task> restore = () => store.RestoreBundleAsync(new([], [new(kind, "planner.md", "after"u8.ToArray(), null)], true));
        await restore.Should().ThrowAsync<BundleImportException>();
        File.ReadAllText(body).Should().Be("before");
        File.ReadAllBytes(metadata).Should().Equal(prior);
        File.Exists(Journal).Should().BeFalse();
    }

    public void Dispose()
    {
        BundleRestoreCoordinator.FailureInjector.Value = null;
        FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        _scope.Dispose();
    }

    [Theory]
    [InlineData("prepared", "after", "before")]
    [InlineData("prepared", "before", "before")]
    [InlineData("committed", "after", "after")]
    [InlineData("rolled-back", "before", "before")]
    public void RecoveryUsesOnlyOwnedMembers(string state, string current, string expected)
    {
        Seed(current);
        WriteJournal(state);
        string[] unrelated = ["config.json", "export.zip", "vectors.bin", "sentinel.txt"];
        foreach (string name in unrelated) File.WriteAllText(Path.Combine(Root, ".scrinia", name), "keep");
        using var store = new FileMemoryStore(Root);
        store.ListScoped();
        File.ReadAllText(Body).Should().Be(expected);
        File.Exists(Journal).Should().BeFalse();
        foreach (string name in unrelated) File.ReadAllText(Path.Combine(Root, ".scrinia", name)).Should().Be("keep");
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("committed")]
    [InlineData("rolled-back")]
    public void ThirdStatePreflightRefusesWithoutReplay(string state)
    {
        Seed("external");
        WriteJournal(state);
        byte[] evidence = File.ReadAllBytes(Journal);
        using var store = new FileMemoryStore(Root);
        Action read = () => store.ListScoped();
        read.Should().Throw<BundleImportException>();
        File.ReadAllText(Body).Should().Be("external");
        File.ReadAllBytes(Journal).Should().Equal(evidence);
    }

    [Theory]
    [InlineData((int)BundleFaultPoint.BeforeMember)]
    [InlineData((int)BundleFaultPoint.AfterMember)]
    public async Task ApplyFailureRestoresOwnedPair(int pointValue)
    {
        Seed("before");
        File.WriteAllText(Path.ChangeExtension(Body, ".meta.json"), "old metadata");
        bool fired = false;
        BundleRestoreCoordinator.FailureInjector.Value = (at, _) =>
            at == (BundleFaultPoint)pointValue && !fired && (fired = true) ? new IOException("apply failed") : null;
        using var store = new FileMemoryStore(Root);
        Func<Task> restore = () => store.RestoreBundleAsync(Plan());
        await restore.Should().ThrowAsync<BundleImportException>().Where(e => e.Failure == BundleImportFailure.RolledBack);
        File.ReadAllText(Body).Should().Be("before");
        File.ReadAllText(Path.ChangeExtension(Body, ".meta.json")).Should().Be("old metadata");
        File.Exists(Journal).Should().BeFalse();
    }

    [Theory]
    [InlineData((int)BundleFaultPoint.BeforeCommitDecision, false)]
    [InlineData((int)BundleFaultPoint.AfterCommitDecision, true)]
    [InlineData((int)BundleFaultPoint.Cleanup, true)]
    public async Task DecisionAndCleanupFailuresNeverCompensate(int pointValue, bool committed)
    {
        Seed("before");
        BundleRestoreCoordinator.FailureInjector.Value = (at, _) => at == (BundleFaultPoint)pointValue ? new IOException("decision fault") : null;
        using var store = new FileMemoryStore(Root);
        if (committed)
            (await store.RestoreBundleAsync(Plan())).FileEntityCount.Should().Be(1);
        else
        {
            Func<Task> restore = () => store.RestoreBundleAsync(Plan());
            await restore.Should().ThrowAsync<BundleImportException>().Where(e => e.Failure == BundleImportFailure.RecoveryRequired);
        }
        File.ReadAllText(Body).Should().Be("after");
        File.Exists(Journal).Should().BeTrue();
    }

    [Fact]
    public async Task BodyOnlyOverwriteDeletesOldMetadataAndAcceptsZeroBytes()
    {
        Seed("before");
        string meta = Path.ChangeExtension(Body, ".meta.json");
        File.WriteAllText(meta, "metadata");
        using var store = new FileMemoryStore(Root);
        var result = await store.RestoreBundleAsync(new([], [new("skill", "planner.md", [], null)], true));
        result.FileEntityCount.Should().Be(1);
        File.ReadAllBytes(Body).Should().BeEmpty();
        File.Exists(meta).Should().BeFalse();
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(5, false)]
    public async Task PriorBytesHonorExactCapacity(long capacity, bool succeeds)
    {
        Seed("before");
        using var store = new FileMemoryStore(Root);
        var limits = BundleReadLimits.Default with { PriorBytes = capacity };
        var admission = new WorkspaceAdmission(Root, (lease, ct) => BundleRestoreCoordinator.RecoverUnderLease(lease, limits, ct));
        var coordinator = new BundleRestoreCoordinator(store, admission, limits);
        if (succeeds) (await coordinator.ExecuteAsync(Plan(), default)).FileEntityCount.Should().Be(1);
        else
        {
            Func<Task> restore = () => coordinator.ExecuteAsync(Plan(), default);
            await restore.Should().ThrowAsync<BundleImportException>().Where(e => e.Failure == BundleImportFailure.RolledBack);
            File.ReadAllText(Body).Should().Be("before");
        }
        File.Exists(Journal).Should().BeFalse();
    }

    private void Seed(string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Body)!);
        File.WriteAllText(Body, body);
    }

    private void WriteJournal(string state)
    {
        byte[] before = "before"u8.ToArray();
        var member = new BundleJournalMember(".scrinia/skills/planner.md", "skill-body", true, before,
            Hash(before), true, Hash("after"u8.ToArray()));
        var journal = BundleRestoreJournalCodec.CreatePrepared("operation", Root, [member], BundleReadLimits.Default) with { State = state };
        File.WriteAllBytes(Journal, BundleRestoreJournalCodec.SerializeValidated(journal, Root, BundleReadLimits.Default));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
