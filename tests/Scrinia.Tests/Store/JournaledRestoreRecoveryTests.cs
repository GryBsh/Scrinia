using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Store;

public class JournaledRestoreRecoveryTests
{
    private const string Scope = "local:notes";
    private const string JournalPath = "/workspace/.scrinia/memories/notes/.restore-journal.json";

    [Fact]
    public void ImportTopicEntries_OnSuccess_RemovesJournalAndWritesExactFinalArtifacts()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        byte[] body = [0, 255, 10, 13];
        var incoming = new ArtifactEntry("daily-log", "file://exported", 1, 1, DateTimeOffset.UtcNow);

        store.ImportTopicEntries(Scope, [incoming], new Dictionary<string, byte[]>
        {
            [incoming.Name] = body
        }, overwrite: true);

        string storeDirectory = store.GetStoreDirForScope(Scope);
        File.Exists(Path.Combine(storeDirectory, ".restore-journal.json")).Should().BeFalse(
            "a normally completed restore leaves no recovery residue");
        File.ReadAllBytes(Path.Combine(storeDirectory, "daily-log.nmp2")).Should().Equal(body);
        File.ReadAllBytes(Path.Combine(storeDirectory, "daily-log.meta.json")).Should().Equal(
            JsonSerializer.SerializeToUtf8Bytes(incoming with
            {
                Address = MemoryAddress.Parse("/notes/daily-log"),
                ArtifactKey = $"artifacts/notes/{incoming.Name}.nmp2"
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }));
    }

    [Fact]
    public void ImportTopicEntries_CanonicalizesTheRestoredEntryToItsTargetScope()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var incoming = new ArtifactEntry("daily-log", "file://exported", 4, 1, DateTimeOffset.UtcNow);

        store.ImportTopicEntries(Scope, [incoming], new Dictionary<string, byte[]>
        {
            [incoming.Name] = [1, 2, 3, 4]
        }, overwrite: true);

        var restored = store.LoadIndex(Scope).Should().ContainSingle().Subject;
        restored.Name.Should().Be("daily-log");
        restored.Address.Should().Be(MemoryAddress.Parse("/notes/daily-log"));
        restored.ArtifactKey.Should().Be("artifacts/notes/daily-log.nmp2");
        File.Exists(Path.Combine(store.GetStoreDirForScope(Scope), "daily-log.nmp2")).Should().BeTrue();
    }

    [Fact]
    public async Task LoadIndex_WhenAWarmScopeGainsAPreparedJournal_RecoversBeforeReturningTheCachedIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("note".StartsWith("/") || "note".StartsWith("~") ? "note" : "/" + "note"), Scope, ["old"]));
        store.LoadIndex(Scope).Should().ContainSingle();

        var recovered = CreateEntry("note") with { ContentPreview = "recovered" };
        WritePreparedJournal(store, Scope, recovered, Encoding.UTF8.GetBytes("NMP/2 recovered"));

        store.LoadIndex(Scope).Should().ContainSingle().Which.ContentPreview.Should().Be("recovered");
    }

    [Fact]
    public async Task ReadAndResolveArtifact_WhenAWarmScopeGainsAPreparedJournal_ReturnRecoveredBytes()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.WriteArtifactAsync("note", Scope, "old");
        (await store.ReadArtifactAsync(MemoryAddress.Parse("/notes/note"))).Should().Be("old");

        WritePreparedJournal(store, Scope, CreateEntry("note"), Encoding.UTF8.GetBytes("recovered"));

        (await store.ReadArtifactAsync(MemoryAddress.Parse("/notes/note"))).Should().Be("recovered");
        (await store.ReadArtifactAsync(MemoryAddress.Parse("/notes/note"))).Should().Be("recovered");
    }

    [Fact]
    public async Task ArchiveVersion_WhenAPreparedJournalExists_RecoversBeforeCopyingTheBodyAndSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.WriteArtifactAsync("note", Scope, "old");

        var recovered = CreateEntry("note") with { ContentPreview = "recovered" };
        WritePreparedJournal(store, Scope, recovered, Encoding.UTF8.GetBytes("recovered"));

        store.ArchiveVersion("note", Scope);

        string storeDirectory = store.GetStoreDirForScope(Scope);
        string archive = Directory.GetFiles(Path.Combine(storeDirectory, "versions"), "note_*.nmp2")
            .Should().ContainSingle().Subject;
        File.ReadAllText(archive).Should().Be("recovered");
        File.ReadAllText(Path.ChangeExtension(archive, null) + ".meta.json").Should().Contain("recovered");
        File.Exists(Path.Combine(storeDirectory, ".restore-journal.json")).Should().BeFalse();
    }

    [Fact]
    public async Task MigrateStore_WhenAPreparedJournalExists_RecoversBeforeReadingRawSidecars()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.WriteArtifactAsync("note", Scope, "old");

        var recovered = CreateEntry("note") with
        {
            FileHashes = new Dictionary<string, string> { ["src/recovered.cs"] = "hash" }
        };
        WritePreparedJournal(store, Scope, recovered, Encoding.UTF8.GetBytes("recovered"));

        AttributeMigration.MigrateSummary summary = AttributeMigration.MigrateStore(store);

        summary.Total.Should().Be(1);
        summary.Migrated.Should().Be(1);
        store.LoadIndex(Scope).Should().ContainSingle().Which.Attributes!["file"].Should().Contain("src/recovered.cs");
    }

    [Fact]
    public async Task GetEntityFactStore_WhenAWarmCacheGainsAPreparedJournal_RebuildsOnlyAfterRecovery()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("note".StartsWith("/") || "note".StartsWith("~") ? "note" : "/" + "note"), Scope, ["old"], attributes: new()
        {
            ["person"] = ["old"]
        }));

        store.GetEntityFactStore().MentionCount(new EntityRef("person", "old")).Should().Be(1);

        var recovered = CreateEntry("note") with
        {
            Attributes = new Dictionary<string, string[]> { ["person"] = ["new"] }
        };
        WritePreparedJournal(store, Scope, recovered, Encoding.UTF8.GetBytes("NMP/2 recovered"));

        EntityFactStore rebuilt = store.GetEntityFactStore();

        rebuilt.MentionCount(new EntityRef("person", "old")).Should().Be(0,
            "the pre-recovery facts cache must be invalidated before it can be returned");
        rebuilt.MentionCount(new EntityRef("person", "new")).Should().Be(1,
            "facts must be rebuilt from the recovered sidecar after recovery completes");
    }

    [Fact]
    public async Task GetEntityFactStore_HoldsRecoveryBarrierAcrossFactsSidecarRead()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("note".StartsWith("/") || "note".StartsWith("~") ? "note" : "/" + "note"), Scope, ["old"], attributes: new()
        {
            ["person"] = ["old"]
        }));
        store.GetEntityFactStore().MentionCount(new EntityRef("person", "old")).Should().Be(1);
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        using var sidecarReadStarted = new ManualResetEventSlim();
        using var allowSidecarRead = new ManualResetEventSlim();
        FileMemoryStore.ReadObserver = (path, _) =>
        {
            if (!path.EndsWith("facts.json", StringComparison.Ordinal))
                return;
            sidecarReadStarted.Set();
            allowSidecarRead.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
                "the test must release the sidecar read");
        };

        using var otherProcess = new FileMemoryStore(scope.WorkspaceDir);
        var incoming = CreateEntry("later");
        try
        {
            Task<EntityFactStore> read = Task.Run(() => store.GetEntityFactStore());
            sidecarReadStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
                "the facts sidecar read must be observable");

            Task write = Task.Run(() => otherProcess.ImportTopicEntries(
                Scope, [incoming], new Dictionary<string, byte[]>
                {
                    [incoming.Name] = Encoding.UTF8.GetBytes("later")
                }, overwrite: true));

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            write.IsCompleted.Should().BeFalse(
                "a writer must not publish recovery while a consistent facts read is in progress");

            allowSidecarRead.Set();
            await read;
            await write;
        }
        finally
        {
            allowSidecarRead.Set();
            FileMemoryStore.ReadObserver = null;
        }
    }

    [Fact]
    public async Task PreparedScope_ConcurrentReadersAndWritersWaitForRecovery_WhileOtherScopesRemainAvailable()
    {
        using var scope = new TestHelpers.StoreScope();
        var recoveringStore = (FileMemoryStore)TestHelpers.Store;
        await recoveringStore.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("note".StartsWith("/") || "note".StartsWith("~") ? "note" : "/" + "note"), Scope, ["old"]));
        WritePreparedJournal(recoveringStore, Scope, CreateEntry("note"), Encoding.UTF8.GetBytes("recovered"));

        using var otherProcess = new FileMemoryStore(scope.WorkspaceDir);
        using var unrelatedProcess = new FileMemoryStore(scope.WorkspaceDir);
        using var recoveryStarted = new ManualResetEventSlim();
        using var allowRecovery = new ManualResetEventSlim();
        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
        {
            if (transition != RestoreJournalTransition.BeforeReplaySidecar)
                return null;

            recoveryStarted.Set();
            allowRecovery.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the test must release recovery");
            return null;
        };

        try
        {
            Task<IReadOnlyList<ArtifactEntry>> recovery = Task.Run(() => recoveringStore.LoadIndex(Scope));
            recoveryStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("prepared recovery reached its write barrier");

            Task<IReadOnlyList<ArtifactEntry>> blockedRead = Task.Run(() => otherProcess.LoadIndex(Scope));
            Task<ArtifactEntry> blockedWrite = Task.Run(() => otherProcess.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("later".StartsWith("/") || "later".StartsWith("~") ? "later" : "/" + "later"), Scope, ["must wait"])));

            Task.Delay(TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult();
            blockedRead.IsCompleted.Should().BeFalse("a second process cannot read a body-new/sidecar-old pair");
            blockedWrite.IsCompleted.Should().BeFalse("a second process cannot write around recovery");

            await unrelatedProcess.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("other".StartsWith("/") || "other".StartsWith("~") ? "other" : "/" + "other"), "local:other", ["available"]));
            unrelatedProcess.LoadIndex("local:other").Should().ContainSingle().Which.Name.Should().Be("other");
            await unrelatedProcess.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("~scratch".StartsWith("/") || "~scratch".StartsWith("~") ? "~scratch" : "/" + "~scratch"), Scope, ["available"]));
            unrelatedProcess.GetEphemeral("scratch")!.Artifact.Should().NotBeNullOrEmpty();

            allowRecovery.Set();
            (await recovery).Should().ContainSingle().Which.ContentPreview.Should().BeNull();
            (await blockedRead).Should().Contain(entry => entry.Name == "note");
            (await blockedWrite).Name.Should().Be("later");
        }
        finally
        {
            allowRecovery.Set();
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }
    }

    [Fact]
    public async Task ReaderAlreadyInsideTheRecoveryBarrier_FinishesBeforeARecoveryWriterCanBegin()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("note".StartsWith("/") || "note".StartsWith("~") ? "note" : "/" + "note"), Scope, ["old"]));
        var incoming = CreateEntry("note") with { ContentPreview = "new" };
        using var readerEntered = new ManualResetEventSlim();
        using var allowReader = new ManualResetEventSlim();

        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
        {
            if (transition != RestoreJournalTransition.BeforeRecoveryBarrierRead)
                return null;
            readerEntered.Set();
            allowReader.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the test must release the reader");
            return null;
        };

        try
        {
            Task<IReadOnlyList<ArtifactEntry>> reader = Task.Run(() => store.LoadIndex(Scope));
            readerEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the reader acquired the shared barrier");

            Task writer = Task.Run(() => store.ImportTopicEntries(Scope, [incoming], new Dictionary<string, byte[]>
            {
                [incoming.Name] = Encoding.UTF8.GetBytes("new")
            }, overwrite: true));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            writer.IsCompleted.Should().BeFalse("the prepared-journal writer cannot begin recovery while a reader holds the shared barrier");

            allowReader.Set();
            (await reader).Should().ContainSingle().Which.ContentPreview.Should().NotBe("new");
            await writer;
            store.LoadIndex(Scope).Should().ContainSingle().Which.ContentPreview.Should().Be("new");
        }
        finally
        {
            allowReader.Set();
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }
    }

    [Fact]
    public async Task MalformedJournal_BlocksOnlyItsScopeWithoutDeadlockingOtherScopes()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string blockedDirectory = store.GetStoreDirForScope(Scope);
        Directory.CreateDirectory(blockedDirectory);
        File.WriteAllText(Path.Combine(blockedDirectory, ".restore-journal.json"), "{]");

        using var otherProcess = new FileMemoryStore(scope.WorkspaceDir);
        Task blockedRead = Task.Run(() =>
            Assert.Throws<RestoreRecoveryException>(() => otherProcess.LoadIndex(Scope)));
        Task blockedWrite = Task.Run(() =>
            Assert.Throws<RestoreRecoveryException>(() => otherProcess.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("note".StartsWith("/") || "note".StartsWith("~") ? "note" : "/" + "note"), Scope, ["blocked"])).GetAwaiter().GetResult()));
        Task accessibleScope = Task.Run(async () =>
            await otherProcess.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("other".StartsWith("/") || "other".StartsWith("~") ? "other" : "/" + "other"), "local:other", ["available"])));

        Task.WaitAll([blockedRead, blockedWrite, accessibleScope], TimeSpan.FromSeconds(10)).Should().BeTrue(
            "a retained invalid journal must fail its own scope rather than deadlocking the store");
        otherProcess.LoadIndex("local:other").Should().ContainSingle().Which.Name.Should().Be("other");
    }

    [Fact]
    public async Task CompletedJournalCleanupFailure_AllowsTheCurrentReadAndWriteToContinueWithoutReenteringTheBarrier()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string storeDirectory = store.GetStoreDirForScope(Scope);
        string journalPath = Path.Combine(storeDirectory, ".restore-journal.json");
        var first = CreateEntry("first");
        var next = CreateEntry("next");
        var firstBodies = new Dictionary<string, byte[]> { [first.Name] = [1, 2, 3] };
        var nextBodies = new Dictionary<string, byte[]> { [next.Name] = [4, 5, 6] };

        int cleanupAttempts = 0;
        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
        {
            if (transition != RestoreJournalTransition.BeforeCleanupDelete)
                return null;

            cleanupAttempts++;
            return cleanupAttempts <= 3
                ? new IOException("Injected completed-journal cleanup failure.")
                : null;
        };
        try
        {
            store.ImportTopicEntries(Scope, [first], firstBodies, overwrite: true);

            byte[] retainedJournal = File.ReadAllBytes(journalPath);
            File.ReadAllBytes(journalPath).Should().Equal(retainedJournal,
                "a failed recovery cleanup must retain its durable completion evidence");

            store.LoadIndex(Scope).Should().ContainSingle().Which.Name.Should().Be(first.Name,
                "a completed journal whose cleanup retry failed has already verified its destination data");
            cleanupAttempts.Should().Be(2,
                "the read barrier retries cleanup once, then permits the verified read instead of recursively retrying");
            File.Exists(journalPath).Should().BeTrue(
                "cleanup failure retains completed recovery evidence for a later access");
            File.ReadAllBytes(Path.Combine(storeDirectory, "first.nmp2")).Should().Equal(firstBodies[first.Name]);

            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
            await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("later".StartsWith("/") || "later".StartsWith("~") ? "later" : "/" + "later"), Scope, ["must continue"]));
            File.Exists(Path.Combine(storeDirectory, "later.nmp2")).Should().BeTrue();
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }

        store.ImportTopicEntries(Scope, [next], nextBodies, overwrite: true);

        File.Exists(journalPath).Should().BeFalse("the later retry cleaned the retained completion evidence");
        File.ReadAllBytes(Path.Combine(storeDirectory, "first.nmp2")).Should().Equal(firstBodies[first.Name]);
        File.ReadAllBytes(Path.Combine(storeDirectory, "next.nmp2")).Should().Equal(nextBodies[next.Name]);
    }

    [Fact]
    public void CompletedJournalDestinationIntegrityFailure_IdentifiesTheRetainedJournal()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var entry = CreateEntry("note");
        byte[] body = [1, 2, 3];
        WritePreparedJournal(store, Scope, entry, body);

        string storeDirectory = store.GetStoreDirForScope(Scope);
        string journalPath = Path.Combine(storeDirectory, ".restore-journal.json");
        RestoreJournal prepared = ReadRestoreEvidence(journalPath);
        File.WriteAllBytes(journalPath, RestoreJournalCodec.Serialize(RestoreJournalCodec.WithCompleted(prepared)));
        File.WriteAllBytes(Path.Combine(storeDirectory, "note.nmp2"), [9, 9, 9]);

        Action read = () => store.LoadIndex(Scope);

        read.Should().Throw<RestoreRecoveryException>().Which.JournalPath.Should().Be(journalPath);
    }

    [Fact]
    public async Task EmptyEntriesCompletedJournal_FailsClosedAsMalformedBlockWithoutCrashing()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string storeDirectory = store.GetStoreDirForScope(Scope);
        Directory.CreateDirectory(storeDirectory);
        string journalPath = Path.Combine(storeDirectory, ".restore-journal.json");

        byte[] completedEmptyJournal = SerializeHandcraftedJournal(state: "completed", entries: 0);
        File.WriteAllBytes(journalPath, completedEmptyJournal);

        Action load = () => store.LoadIndex(Scope);

        RestoreRecoveryException failure = Record.Exception(load)
            .Should().BeOfType<RestoreRecoveryException>().Subject;
        failure.DiscoveryBlock.Should().NotBeNull(
            "an empty-entries completed restore journal must become a clean recovery block");
        failure.DiscoveryBlock!.Reason.Should().Be(RecoveryBlockReason.Malformed);
        File.Exists(journalPath).Should().BeTrue(
            "the malformed journal must be retained, never half-replayed");
    }

    [Fact]
    public async Task ImportTopicEntries_WhenTheFirstLiveBodyWriteFailsAfterPreparedJournalCreation_LeavesEveryLivePairUntouched()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("replace".StartsWith("/") || "replace".StartsWith("~") ? "replace" : "/" + "replace"), Scope, ["ORIGINAL REPLACE"]));
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("skip".StartsWith("/") || "skip".StartsWith("~") ? "skip" : "/" + "skip"), Scope, ["ORIGINAL SKIP"]));

        string storeDirectory = store.GetStoreDirForScope(Scope);
        string journalPath = Path.Combine(storeDirectory, ".restore-journal.json");
        byte[] replaceBodyBefore = File.ReadAllBytes(Path.Combine(storeDirectory, "replace.nmp2"));
        byte[] replaceSidecarBefore = File.ReadAllBytes(Path.Combine(storeDirectory, "replace.meta.json"));
        byte[] skipBodyBefore = File.ReadAllBytes(Path.Combine(storeDirectory, "skip.nmp2"));
        byte[] skipSidecarBefore = File.ReadAllBytes(Path.Combine(storeDirectory, "skip.meta.json"));
        byte[] replacementBody = [0, 255, 10, 13];
        var replacement = CreateEntry("replace");
        var skipped = CreateEntry("skip");
        var missingBody = CreateEntry("missing-body");
        bool observedPreparedJournalBeforeFirstLiveWrite = false;

        DurableFile.FailureInjector = (phase, destination) =>
        {
            if (phase != DurableWritePhase.BeforeWrite ||
                !destination.EndsWith("replace.nmp2", StringComparison.Ordinal))
                return null;

            observedPreparedJournalBeforeFirstLiveWrite = File.Exists(journalPath);
            return new IOException("Injected failure after prepared journal creation.");
        };

        try
        {
            Action import = () => store.ImportTopicEntries(
                Scope,
                [replacement, skipped, missingBody],
                new Dictionary<string, byte[]> { [replacement.Name] = replacementBody },
                overwrite: true);

            import.Should().Throw<DurableWriteException>();
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        observedPreparedJournalBeforeFirstLiveWrite.Should().BeTrue();
        byte[] journalBytes = File.ReadAllBytes(journalPath);
        RestoreJournal journal = ReadRestoreEvidence(journalPath);
        RestoreJournalEntry journalEntry = journal.Entries.Should().ContainSingle().Subject;
        journalEntry.Name.Should().Be("replace");
        Convert.FromBase64String(journalEntry.BodyBase64).Should().Equal(replacementBody);

        File.ReadAllBytes(Path.Combine(storeDirectory, "replace.nmp2")).Should().Equal(replaceBodyBefore);
        File.ReadAllBytes(Path.Combine(storeDirectory, "replace.meta.json")).Should().Equal(replaceSidecarBefore);
        File.ReadAllBytes(Path.Combine(storeDirectory, "skip.nmp2")).Should().Equal(skipBodyBefore);
        File.ReadAllBytes(Path.Combine(storeDirectory, "skip.meta.json")).Should().Equal(skipSidecarBefore);
        File.Exists(Path.Combine(storeDirectory, "missing-body.nmp2")).Should().BeFalse();
        File.Exists(Path.Combine(storeDirectory, "missing-body.meta.json")).Should().BeFalse();
    }

    [Fact]
    public async Task ImportTopicEntries_ExcludesOverwriteFalseAndMissingBodyEntriesBeforeJournalCreation()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("skip".StartsWith("/") || "skip".StartsWith("~") ? "skip" : "/" + "skip"), Scope, ["ORIGINAL SKIP"]));

        string storeDirectory = store.GetStoreDirForScope(Scope);
        byte[] bodyBefore = File.ReadAllBytes(Path.Combine(storeDirectory, "skip.nmp2"));
        byte[] sidecarBefore = File.ReadAllBytes(Path.Combine(storeDirectory, "skip.meta.json"));
        var skipped = CreateEntry("skip");
        var missingBody = CreateEntry("missing-body");

        store.ImportTopicEntries(
            Scope,
            [skipped, missingBody],
            new Dictionary<string, byte[]> { [skipped.Name] = [1, 2, 3] },
            overwrite: false);

        File.Exists(Path.Combine(storeDirectory, ".restore-journal.json")).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(storeDirectory, "skip.nmp2")).Should().Equal(bodyBefore);
        File.ReadAllBytes(Path.Combine(storeDirectory, "skip.meta.json")).Should().Equal(sidecarBefore);
        File.Exists(Path.Combine(storeDirectory, "missing-body.nmp2")).Should().BeFalse();
        File.Exists(Path.Combine(storeDirectory, "missing-body.meta.json")).Should().BeFalse();
    }

    [Fact]
    public void ImportTopicsFromZip_RefusesAnOversizedDeclaredRawMemberBeforeAnyBodyAllocationOrLiveResidue()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        const string topic = "notes";
        const string artifactPath = "topics/notes/oversized.nmp2";
        uint declaredLength = checked((uint)(RestoreJournalCodec.MaxRawNmp2BodyBytes + 1));
        byte[] zipBytes = CreateBundleWithPatchedArtifactLength(topic, "oversized", artifactPath, declaredLength);

        using var archiveStream = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        zip.GetEntry(artifactPath)!.Length.Should().Be(declaredLength);

        Action import = () => BundleFormatService.ImportTopicsFromZip(zip, store, filterTopics: null, overwrite: true);

        import.Should().Throw<InvalidOperationException>()
            .WithMessage("*exceeds the maximum raw NMP/2 body size*");
        string storeDirectory = store.GetStoreDirForScope(Scope);
        Directory.Exists(storeDirectory).Should().BeFalse("the declared entry length is refused before the import reaches its live-write path");
    }

    [Fact]
    public void CreateSerializeAndParsePreparedJournal_PreservesExactPayloadBytes()
    {
        byte[] body = [0, 255, 10, 13];
        byte[] sidecar = Encoding.UTF8.GetBytes("{\"name\":\"daily-log\"}");
        var payload = new RestoreJournalPayload(CreateEntry("daily-log"), body, sidecar);

        RestoreJournal journal = RestoreJournalCodec.CreatePrepared(Scope, [payload]);
        byte[] json = RestoreJournalCodec.Serialize(journal);
        RestoreJournal parsed = RestoreJournalCodec.ParseLegacyJournal(json, Scope, JournalPath);
        RestoreJournalEntry entry = parsed.Entries.Should().ContainSingle().Subject;

        Convert.FromBase64String(entry.BodyBase64).Should().Equal(body);
        Convert.FromBase64String(entry.SidecarBase64).Should().Equal(sidecar);
        entry.BodySha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(body)));
        entry.SidecarSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(sidecar)));
        entry.BodySha256.Should().MatchRegex("^[0-9a-f]{64}$");
        entry.SidecarSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        parsed.State.Should().Be(RestoreJournalState.Prepared);
        Encoding.UTF8.GetString(json).Should().Contain("\"state\":\"prepared\"");
    }

    [Fact]
    public async Task ImportTopicEntries_PublishesTheCommonEnvelopeAtTheScopeRoot()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var entry = CreateEntry("enveloped");

        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
            transition == RestoreJournalTransition.AfterPreparedWrite
                ? new IOException("Stop after prepared evidence.")
                : null;
        try
        {
            Action import = () => store.ImportTopicEntries(Scope, [entry], new Dictionary<string, byte[]>
            {
                [entry.Name] = [1, 2, 3]
            }, overwrite: true);
            import.Should().Throw<IOException>();
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }

        string journalPath = Path.Combine(store.GetStoreDirForScope(Scope), ".restore-journal.json");
        RecoveryEnvelopeCodec.IsCommonEnvelope(File.ReadAllBytes(journalPath)).Should().BeTrue();
    }

    [Fact]
    public void RestoreAdapter_RoundTripsCommonAndLegacyDocumentsWithoutRewritingLegacyBytes()
    {
        var entry = CreateEntry("adapter");
        RestoreJournal journal = RestoreJournalCodec.CreatePrepared(Scope,
            [new RestoreJournalPayload(entry, [7, 8], [9, 10])]);
        string evidencePath = JournalPath;
        var adapter = new RestoreRecoveryAdapter();
        byte[] legacyBytes = RestoreJournalCodec.Serialize(journal);

        RecoveryEnvelopeDocument legacy = ((IRecoveryAdapter)adapter).ConsumeParsed(
            legacyBytes, Scope, evidencePath, Path.GetDirectoryName(evidencePath)!);
        RecoveryEnvelopeDocument common = RestoreJournalCodec.ToEnvelope(journal);
        byte[] commonBytes = RecoveryEnvelopeCodec.Serialize(common);
        RecoveryEnvelopeDocument parsedCommon = ((IRecoveryAdapter)adapter).ConsumeParsed(
            commonBytes, Scope, evidencePath, Path.GetDirectoryName(evidencePath)!);

        legacy.IsLegacy.Should().BeTrue();
        parsedCommon.IsLegacy.Should().BeFalse();
        RestoreJournal restored = adapter.FromEnvelope(parsedCommon, evidencePath);
        restored.TransactionId.Should().Be(journal.TransactionId);
        restored.Entries.Should().Equal(journal.Entries);
        RestoreJournalCodec.Serialize(adapter.FromEnvelope(legacy, evidencePath)).Should().Equal(legacyBytes);
    }

    [Theory]
    [InlineData("committed", "prepared")]
    [InlineData("prepared", "completed")]
    public async Task RestoreLifecycleMismatch_BlocksOnlyTheSelectedScopeWithoutMutatingLivePair(
        string envelopeStateName, string bodyStateName)
    {
        RecoveryLifecycleState envelopeState = envelopeStateName == "committed"
            ? RecoveryLifecycleState.Committed
            : RecoveryLifecycleState.Prepared;
        RestoreJournalState bodyState = bodyStateName == "completed"
            ? RestoreJournalState.Completed
            : RestoreJournalState.Prepared;
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/notes/mismatch"), Scope, ["original"]));
        string storeDirectory = store.GetStoreDirForScope(Scope);
        string bodyPath = Path.Combine(storeDirectory, "mismatch.nmp2");
        string sidecarPath = Path.Combine(storeDirectory, "mismatch.meta.json");
        byte[] originalBody = File.ReadAllBytes(bodyPath);
        byte[] originalSidecar = File.ReadAllBytes(sidecarPath);
        var entry = CreateEntry("mismatch");
        RestoreJournal journal = RestoreJournalCodec.CreatePrepared(Scope,
            [new RestoreJournalPayload(entry, Encoding.UTF8.GetBytes("replacement"), [1, 2, 3])]);
        if (bodyState == RestoreJournalState.Completed)
            journal = RestoreJournalCodec.WithCompleted(journal);

        string journalPath = Path.Combine(storeDirectory, ".restore-journal.json");
        RecoveryEnvelopeDocument envelope = RestoreJournalCodec.ToEnvelope(journal) with
        {
            State = envelopeState
        };
        File.WriteAllBytes(journalPath, RecoveryEnvelopeCodec.Serialize(envelope));

        RestoreRecoveryException failure = Record.Exception(() => store.LoadIndex(Scope))
            .Should().BeOfType<RestoreRecoveryException>().Subject;
        failure.DiscoveryBlock.Should().NotBeNull();
        failure.DiscoveryBlock!.Reason.Should().Be(RecoveryBlockReason.Malformed);
        failure.DiscoveryBlock.OperationId.Should().Be(journal.TransactionId);
        File.ReadAllBytes(bodyPath).Should().Equal(originalBody);
        File.ReadAllBytes(sidecarPath).Should().Equal(originalSidecar);
        File.Exists(journalPath).Should().BeTrue();
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("Completed")]
    [InlineData("PREPARED")]
    [InlineData("1")]
    public void ParseAndValidate_RejectsNonCanonicalJournalStateWireValues(string state)
    {
        byte[] json = SerializeHandcraftedJournal(state: state, stateIsJsonNumber: state == "1");

        Action parse = () => RestoreJournalCodec.ParseLegacyJournal(json, Scope, JournalPath);

        parse.Should().Throw<RestoreRecoveryException>().Which.Failure
             .Should().Be(RestoreRecoveryFailure.MalformedOrIntegrityFailed);
    }

    [Fact]
    public void ParseAndValidate_RejectsAnAbsentStateBeforeDecodingOrMutatingJournalBytes()
    {
        byte[] journal = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(SerializeHandcraftedJournal())
            .Replace("\"state\":\"prepared\",", "", StringComparison.Ordinal));

        AssertRejectedWithoutDecodeOrMutation(journal);
    }

    [Fact]
    public void WithCompleted_PreservesTheJournalAndChangesOnlyState()
    {
        RestoreJournal prepared = RestoreJournalCodec.CreatePrepared(Scope,
            [new RestoreJournalPayload(CreateEntry("daily-log"), [1], [2])]);

        RestoreJournalCodec.WithCompleted(prepared).Should().Be(prepared with
        {
            State = RestoreJournalState.Completed
        });
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("{\"format\":\"other\"}", false)]
    [InlineData("{\"format\":\"scrinia.restore-journal\",\"version\":2}", false)]
    public void ParseAndValidate_RejectsInvalidJournalDocuments(string json, bool isUnsupportedVersion)
    {
        Action parse = () => RestoreJournalCodec.ParseLegacyJournal(Encoding.UTF8.GetBytes(json), Scope, JournalPath);

        parse.Should().Throw<RestoreRecoveryException>().Which.Failure.Should().Be(
            isUnsupportedVersion
                ? RestoreRecoveryFailure.UnsupportedVersion
                : RestoreRecoveryFailure.MalformedOrIntegrityFailed);
    }

    [Theory]
    [InlineData("../x.nmp2")]
    [InlineData("x/y.nmp2")]
    [InlineData("daily-log.NMP2")]
    public void ParseAndValidate_RejectsNoncanonicalDestinationFileNames(string bodyFile)
    {
        byte[] json = SerializeHandcraftedJournal(bodyFile: bodyFile);

        Action parse = () => RestoreJournalCodec.ParseLegacyJournal(json, Scope, JournalPath);

        parse.Should().Throw<RestoreRecoveryException>().Which.Failure
             .Should().Be(RestoreRecoveryFailure.MalformedOrIntegrityFailed);
    }

    [Fact]
    public void ParseAndValidate_RejectsRootedDestinationFileNamesBeforeDecodingOrMutatingJournalBytes()
    {
        byte[] journal = SerializeHandcraftedJournal(bodyFile: "/daily-log.nmp2");

        AssertRejectedWithoutDecodeOrMutation(journal);
    }

    [Fact]
    public void ParseAndValidate_RejectsCaseInsensitiveDuplicateEntryNames()
    {
        byte[] journal = SerializeHandcraftedJournal(journalEntries:
        [
            ("daily-log", "daily-log.nmp2", "daily-log.meta.json"),
            ("DAILY-LOG", "DAILY-LOG.nmp2", "DAILY-LOG.meta.json")
        ]);

        AssertRejectedWithoutDecodeOrMutation(journal);
    }

    [Fact]
    public void ParseAndValidate_RejectsCaseInsensitiveDuplicateDestinationFiles()
    {
        byte[] journal = SerializeHandcraftedJournal(journalEntries:
        [
            ("daily/log", "daily_log.nmp2", "daily_log.meta.json"),
            ("DAILY\\LOG", "DAILY_LOG.nmp2", "DAILY_LOG.meta.json")
        ]);

        AssertRejectedWithoutDecodeOrMutation(journal);
    }

    [Fact]
    public void ParseAndValidate_RejectsAValidLowercaseButMismatchedSha256WithoutMutatingJournalBytes()
    {
        byte[] journal = SerializeHandcraftedJournal(bodyDigest: new string('0', 64));
        byte[] original = journal.ToArray();
        int decodeAttempts = 0;
        RestoreJournalCodec.DecodeObserver.Value = () => decodeAttempts++;
        try
        {
            Action parse = () => RestoreJournalCodec.ParseLegacyJournal(journal, Scope, JournalPath);

            parse.Should().Throw<RestoreRecoveryException>().Which.Failure
                .Should().Be(RestoreRecoveryFailure.MalformedOrIntegrityFailed);
            journal.Should().Equal(original);
            decodeAttempts.Should().Be(1, "integrity verification starts only after complete admission");
        }
        finally
        {
            RestoreJournalCodec.DecodeObserver.Value = null;
        }
    }

    [Fact]
    public void ParseAndValidate_RejectsEmptyEntriesBeforeDecodingOrMutatingJournalBytes()
    {
        byte[] journal = Encoding.UTF8.GetBytes("""
            {"format":"scrinia.restore-journal","version":1,"state":"prepared","transactionId":"0123456789abcdef0123456789abcdef","scope":"local:notes","entries":[]}
            """);

        AssertRejectedWithoutDecodeOrMutation(journal);
    }

    [Fact]
    public void Bounds_AreFiniteAndPinTheApprovedMaximumJournalDocumentSize()
    {
        RestoreJournalCodec.MaxRawNmp2BodyBytes.Should().Be(96L * 1024 * 1024,
            "journal admission has a fixed raw-body limit independent of NMP semantic limits");
        RestoreJournalCodec.MaxSidecarBytes.Should().Be(16L * 1024 * 1024);
        RestoreJournalCodec.MaxAggregatePayloadBytes.Should().Be(128L * 1024 * 1024);
        RestoreJournalCodec.MaxJournalEntryCount.Should().Be(1_000);
        RestoreJournalCodec.MaxJournalJsonBytes.Should().Be(181_009_068);
    }

    [Fact]
    public void ParseAndValidate_AdmitsAllEntriesBeforeDecodingAnyPayload()
    {
        byte[] json = SerializeHandcraftedJournal(entries: 2, secondBodyDigest: new string('A', 64));
        int decodeAttempts = 0;
        RestoreJournalCodec.DecodeObserver.Value = () => decodeAttempts++;
        try
        {
        Action parse = () => RestoreJournalCodec.ParseLegacyJournal(json, Scope, JournalPath);

            parse.Should().Throw<RestoreRecoveryException>().Which.Failure
                .Should().Be(RestoreRecoveryFailure.MalformedOrIntegrityFailed);
            decodeAttempts.Should().Be(0,
                "a malformed later entry must reject the complete journal before any Base64 decode");
        }
        finally
        {
            RestoreJournalCodec.DecodeObserver.Value = null;
        }
    }

    [Theory]
    [InlineData(1_000, false)]
    [InlineData(1_001, true)]
    public void ParseAndValidate_EnforcesTheEntryCountBeforeAnyBase64Decode(int entries, bool shouldReject)
    {
        byte[] json = SerializeHandcraftedJournal(entries: entries);
        int decodeAttempts = 0;
        RestoreJournalCodec.DecodeObserver.Value = () => decodeAttempts++;
        try
        {
        Action parse = () => RestoreJournalCodec.ParseLegacyJournal(json, Scope, JournalPath);

            if (shouldReject)
            {
                parse.Should().Throw<RestoreRecoveryException>();
                decodeAttempts.Should().Be(0);
            }
            else
            {
                parse.Should().NotThrow();
                decodeAttempts.Should().Be(entries * 2);
            }
        }
        finally
        {
            RestoreJournalCodec.DecodeObserver.Value = null;
        }
    }

    [Fact]
    public void JournalDocumentLength_AdmitsTheExactLimitAndRejectsTheNextByteBeforeParsing()
    {
        RestoreJournalCodec.IsJournalDocumentLengthAdmitted(181_009_068).Should().BeTrue();
        RestoreJournalCodec.IsJournalDocumentLengthAdmitted(181_009_069).Should().BeFalse();
    }

    [Fact]
    public void BoundedJournalReader_RejectsBytesThatArriveAfterAnInitiallyAdmittedPrefix()
    {
        using var growingStream = new MemoryStream([1, 2, 3, 4, 5, 6]);

        Action materialize = () => FileMemoryStore.ReadBoundedJournalStream(
            growingStream, maximumBytes: 5, Scope, JournalPath);

        materialize.Should().Throw<RestoreRecoveryException>().Which.Failure
            .Should().Be(RestoreRecoveryFailure.MalformedOrIntegrityFailed);
    }

    [Fact]
    public void ParseAndValidate_RejectsAnOversizedJournalBeforeDecodingOrMutatingJournalBytes()
    {
        byte[] journal = new byte[checked((int)RestoreJournalCodec.MaxJournalJsonBytes + 1)];

        AssertRejectedWithoutDecodeOrMutation(journal);
    }

    [Fact]
    public void AggregateAdmission_Accepts128MiBAndRejectsOverflowAndTheNextByte()
    {
        long maximum = RestoreJournalCodec.MaxAggregatePayloadBytes;

        RestoreJournalCodec.AddDecodedPayloadLength(0, maximum, Scope, JournalPath).Should().Be(maximum);
        Action overLimit = () => RestoreJournalCodec.AddDecodedPayloadLength(maximum, 1, Scope, JournalPath);
        Action overflow = () => RestoreJournalCodec.AddDecodedPayloadLength(long.MaxValue, 1, Scope, JournalPath);

        overLimit.Should().Throw<RestoreRecoveryException>();
        overflow.Should().Throw<RestoreRecoveryException>();
    }

    [Theory]
    [InlineData(96L * 1024 * 1024)]
    [InlineData(16L * 1024 * 1024)]
    public void EncodedPayloadAdmission_AcceptsEachExactDecodedBoundaryAndRejectsTheNextByte(long decodedLimit)
    {
        long exactEncodedLength = RestoreJournalCodec.GetPaddedBase64Length(decodedLimit);

        RestoreJournalCodec.IsEncodedPayloadLengthAdmitted(exactEncodedLength, decodedLimit).Should().BeTrue();
        RestoreJournalCodec.IsDecodedPayloadLengthAdmitted(decodedLimit, decodedLimit).Should().BeTrue();
        RestoreJournalCodec.IsDecodedPayloadLengthAdmitted(decodedLimit + 1, decodedLimit).Should().BeFalse();
    }

    [Fact]
    public void ParseAndValidate_RejectsMalformedIntegrityAndScopeResidueWithoutDecodingOrMutatingIt()
    {
        byte[] valid = SerializeHandcraftedJournal(entries: 2);
        var malformedJournals = new[]
        {
            Encoding.UTF8.GetBytes("{]"),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"scope\":\"local:notes\"", "\"scope\":\"local:other\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"name\":\"daily-log-1\"", "\"name\":\"daily-log\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("daily-log-1.nmp2", "daily-log.nmp2", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"bodyBase64\":\"AQ==\"", "\"bodyBase64\":\"!!!!\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"bodySha256\":\"", "\"bodySha256\":\"A", StringComparison.Ordinal))
        };

        foreach (byte[] journal in malformedJournals)
        {
            byte[] original = journal.ToArray();
            int decodeAttempts = 0;
            RestoreJournalCodec.DecodeObserver.Value = () => decodeAttempts++;
            try
            {
                Action parse = () => RestoreJournalCodec.ParseLegacyJournal(journal, Scope, JournalPath);

                parse.Should().Throw<RestoreRecoveryException>().Which.Failure
                    .Should().Be(RestoreRecoveryFailure.MalformedOrIntegrityFailed);
                journal.Should().Equal(original, "codec validation must never alter blocked residue");
                decodeAttempts.Should().Be(0, "malformed journals are rejected during admission");
            }
            finally
            {
                RestoreJournalCodec.DecodeObserver.Value = null;
            }
        }
    }

    [Fact]
    public void ParseAndValidate_LeavesNewerJournalBytesUntouched()
    {
        byte[] journal = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(SerializeHandcraftedJournal())
            .Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));
        byte[] original = journal.ToArray();

        Action parse = () => RestoreJournalCodec.ParseLegacyJournal(journal, Scope, JournalPath);

        parse.Should().Throw<RestoreRecoveryException>().Which.Failure
            .Should().Be(RestoreRecoveryFailure.UnsupportedVersion);
        journal.Should().Equal(original);
    }

    [Theory]
    [InlineData("BeforePreparedWrite", 0)]
    [InlineData("AfterPreparedWrite", 0)]
    [InlineData("BeforeReplayBody", 0)]
    [InlineData("BeforeReplaySidecar", 0)]
    [InlineData("BeforeReplayBody", 1)]
    [InlineData("AfterReplayDestinations", 0)]
    [InlineData("BeforeCompletedWrite", 0)]
    [InlineData("AfterCompletedWrite", 0)]
    [InlineData("BeforeCleanupDelete", 0)]
    public void ImportTopicEntries_CrashAtEveryJournalTransition_RollsForwardExactPairsFromTheJournal(
        string transitionName, int targetEntry)
    {
        RestoreJournalTransition transition = Enum.Parse<RestoreJournalTransition>(transitionName);
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string storeDirectory = store.GetStoreDirForScope(Scope);
        byte[] firstBody = Encoding.UTF8.GetBytes(Nmp2ChunkedEncoder.Encode("first body"));
        byte[] secondBody = Encoding.UTF8.GetBytes(Nmp2ChunkedEncoder.Encode("second body"));
        var first = CreateEntry("first") with { OriginalBytes = firstBody.Length };
        var second = CreateEntry("second") with { OriginalBytes = secondBody.Length };
        var bodies = new Dictionary<string, byte[]>
        {
            [first.Name] = firstBody,
            [second.Name] = secondBody
        };
        string sourceZipPath = Path.Combine(scope.WorkspaceDir, "restore-source.zip");
        WriteBinarySourceZip(sourceZipPath, [first, second], bodies);
        string journalPath = Path.Combine(storeDirectory, ".restore-journal.json");
        int replayWritesDuringRecovery = 0;
        bool injected = false;

        FileMemoryStore.RestoreJournalFailureInjector.Value = (phase, destination) =>
        {
            if (phase is RestoreJournalTransition.BeforeReplayBody or RestoreJournalTransition.BeforeReplaySidecar)
                replayWritesDuringRecovery++;

            bool matchesTarget = phase == transition && (phase switch
            {
                RestoreJournalTransition.BeforeReplayBody => destination.EndsWith(
                    targetEntry == 0 ? "first.nmp2" : "second.nmp2", StringComparison.Ordinal),
                RestoreJournalTransition.BeforeReplaySidecar => destination.EndsWith("first.meta.json", StringComparison.Ordinal),
                _ => true
            });
            if (!matchesTarget || injected)
                return null;

            injected = true;
            return new IOException("Injected journal transition failure.");
        };

        try
        {
            using (var zipStream = File.OpenRead(sourceZipPath))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                Action import = () => BundleFormatService.ImportTopicsFromZip(zip, store, filterTopics: null, overwrite: true);
                if (transition == RestoreJournalTransition.BeforeCleanupDelete)
                    import.Should().NotThrow("cleanup follows durable completion and is retried later");
                else
                    import.Should().Throw<BundleImportException>()
                        .Which.InnerException.Should().BeOfType<IOException>();
            }
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }

        injected.Should().BeTrue();
        if (transition == RestoreJournalTransition.BeforePreparedWrite)
        {
            File.Exists(journalPath).Should().BeFalse();
            File.Exists(Path.Combine(storeDirectory, "first.nmp2")).Should().BeFalse();
            return;
        }

        File.Exists(journalPath).Should().Be(transition == RestoreJournalTransition.BeforeCleanupDelete);
        File.Exists(Path.Combine(storeDirectory, "first.nmp2")).Should().Be(
            transition == RestoreJournalTransition.BeforeCleanupDelete);
        File.Exists(Path.Combine(storeDirectory, "second.nmp2")).Should().Be(
            transition == RestoreJournalTransition.BeforeCleanupDelete);
    }

    private static ArtifactEntry CreateEntry(string name) =>
        new(name, $"local:notes:{name}", 0, 1, DateTimeOffset.UtcNow);

    private static void WritePreparedJournal(FileMemoryStore store, string scope, ArtifactEntry entry, byte[] body)
    {
        string storeDirectory = store.GetStoreDirForScope(scope);
        Directory.CreateDirectory(storeDirectory);
        byte[] sidecar = JsonSerializer.SerializeToUtf8Bytes(entry, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        RestoreJournal journal = RestoreJournalCodec.CreatePrepared(scope,
            [new RestoreJournalPayload(entry, body, sidecar)]);
        File.WriteAllBytes(Path.Combine(storeDirectory, ".restore-journal.json"),
            RestoreJournalCodec.Serialize(journal));
    }

    private static RestoreJournal ReadRestoreEvidence(string journalPath)
    {
        var adapter = new RestoreRecoveryAdapter();
        RecoveryEnvelopeDocument envelope = ((IRecoveryAdapter)adapter).ConsumeParsed(
            File.ReadAllBytes(journalPath), Scope, journalPath, Path.GetDirectoryName(journalPath)!);
        return adapter.FromEnvelope(envelope, journalPath);
    }

    private static void WriteBinarySourceZip(string path, IReadOnlyList<ArtifactEntry> entries,
        IReadOnlyDictionary<string, byte[]> bodies)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteZipBytes(zip, "topics/notes/index.json", JsonSerializer.SerializeToUtf8Bytes(
            new BundleIndex(entries.ToList()), BundleJsonContext.Default.BundleIndex));
        foreach (ArtifactEntry entry in entries)
            WriteZipBytes(zip, $"topics/notes/{entry.Name}.nmp2", bodies[entry.Name]);
        BundleFormatService.WriteManifest(zip, ["notes"], entries.Count, fileEntities: null);
    }

    private static void WriteZipBytes(ZipArchive zip, string path, byte[] bytes)
    {
        using Stream destination = zip.CreateEntry(path).Open();
        destination.Write(bytes);
    }

    private static byte[] CreateBundleWithPatchedArtifactLength(
        string topic,
        string name,
        string artifactPath,
        uint declaredLength)
    {
        var entry = CreateEntry(name);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            BundleFormatService.WriteTopic(zip, TestHelpers.Store, topic, [entry], [(name, "x")]);
            BundleFormatService.WriteManifest(zip, [topic], totalEntries: 1, fileEntities: null);
        }

        byte[] bytes = stream.ToArray();
        int centralDirectoryOffset = FindCentralDirectoryEntry(bytes, artifactPath);
        BitConverter.GetBytes(declaredLength).CopyTo(bytes, centralDirectoryOffset + 24);
        return bytes;
    }

    private static int FindCentralDirectoryEntry(byte[] zipBytes, string entryPath)
    {
        const uint centralDirectorySignature = 0x02014b50;
        for (int offset = 0; offset <= zipBytes.Length - 46; offset++)
        {
            if (BitConverter.ToUInt32(zipBytes, offset) != centralDirectorySignature)
                continue;

            ushort nameLength = BitConverter.ToUInt16(zipBytes, offset + 28);
            ushort extraLength = BitConverter.ToUInt16(zipBytes, offset + 30);
            ushort commentLength = BitConverter.ToUInt16(zipBytes, offset + 32);
            string candidatePath = Encoding.UTF8.GetString(zipBytes, offset + 46, nameLength);
            if (candidatePath.Equals(entryPath, StringComparison.Ordinal))
                return offset;

            offset += 46 + nameLength + extraLength + commentLength - 1;
        }

        throw new InvalidOperationException($"Test bundle does not contain '{entryPath}'.");
    }

    private static void AssertRejectedWithoutDecodeOrMutation(byte[] journal)
    {
        byte[] original = journal.ToArray();
        int decodeAttempts = 0;
        RestoreJournalCodec.DecodeObserver.Value = () => decodeAttempts++;
        try
        {
            Action parse = () => RestoreJournalCodec.ParseLegacyJournal(journal, Scope, JournalPath);

            parse.Should().Throw<RestoreRecoveryException>().Which.Failure
                .Should().Be(RestoreRecoveryFailure.MalformedOrIntegrityFailed);
            journal.Should().Equal(original, "codec validation must never alter blocked residue");
            decodeAttempts.Should().Be(0, "malformed journals are rejected during admission");
        }
        finally
        {
            RestoreJournalCodec.DecodeObserver.Value = null;
        }
    }

    private static byte[] SerializeHandcraftedJournal(
        string bodyFile = "daily-log.nmp2",
        string state = "prepared",
        bool stateIsJsonNumber = false,
        int entries = 1,
        string? secondBodyDigest = null,
        string? bodyDigest = null,
        (string Name, string BodyFile, string SidecarFile)[]? journalEntries = null)
    {
        byte[] body = [1];
        byte[] sidecar = [2];
        string body64 = Convert.ToBase64String(body);
        string sidecar64 = Convert.ToBase64String(sidecar);
        string bodyHash = bodyDigest ?? Convert.ToHexStringLower(SHA256.HashData(body));
        string sidecarHash = Convert.ToHexStringLower(SHA256.HashData(sidecar));
        string stateJson = stateIsJsonNumber ? state : $"\"{state}\"";
        var serializedEntries = new StringBuilder();
        int entryCount = journalEntries?.Length ?? entries;
        for (int index = 0; index < entryCount; index++)
        {
            if (index > 0) serializedEntries.Append(',');
            string name = journalEntries is { } explicitEntries
                ? explicitEntries[index].Name
                : index == 0 ? "daily-log" : $"daily-log-{index}";
            string entryBodyFile = journalEntries is { } targets
                ? targets[index].BodyFile
                : index == 0 ? bodyFile : name + ".nmp2";
            string entrySidecarFile = journalEntries is { } sidecarTargets
                ? sidecarTargets[index].SidecarFile
                : index == 0 ? "daily-log.meta.json" : name + ".meta.json";
            string entryBodyHash = index == 1 && secondBodyDigest is not null ? secondBodyDigest : bodyHash;
            serializedEntries.Append($$"""{"name":"{{name}}","bodyFile":"{{entryBodyFile}}","bodyBase64":"{{body64}}","bodySha256":"{{entryBodyHash}}","sidecarFile":"{{entrySidecarFile}}","sidecarBase64":"{{sidecar64}}","sidecarSha256":"{{sidecarHash}}"}""");
        }
        string json = $$"""{"format":"scrinia.restore-journal","version":1,"state":{{stateJson}},"transactionId":"0123456789abcdef0123456789abcdef","scope":"local:notes","entries":[{{serializedEntries}}]}""";
        return Encoding.UTF8.GetBytes(json);
    }
}
