using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Text;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using System.Text.Json;
using Scrinia.Mcp;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class ReservedAppendIntegrityTests
{
    private static readonly bool[] PresenceStates = [false, true];
    [Theory]
    [InlineData("skill", "\n\n")]
    [InlineData("agent", "\n")]
    public async Task ReservedAppendPreservesCompleteBody(string kind, string separator)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string address = $"/{kind}/long-body";
        await tools.MemoryRemember(address, [new string('x', 9000) + "TAIL"]);
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "long-body.md");
        string before = File.ReadAllText(path);
        var result = ResponseParser.Parse(await tools.MemoryAppend(address, "APPENDED"));
        result.Status.Should().Be("success");
        File.ReadAllText(path).Should().Be(before + separator + "APPENDED");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.md")
            .Select(File.ReadAllText).Should().Contain(before);
    }
    [Fact]
    public async Task BuiltInAppendCreatesOneExactOverrideWithoutAddingScaffold()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string original = EmbeddedPrompts.LoadAllSkills()["qa"];
        var result = ResponseParser.Parse(await tools.MemoryAppend("/skill/qa", "FIRST"));
        result.Status.Should().Be("success");
        string path = Path.Combine(PathRouter.ToFileEntityDir("skill", scope.WorkspaceDir), "qa.md");
        File.ReadAllText(path).Should().Be(original + "\n\nFIRST");
        File.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitignore")).Should().BeTrue();
        await tools.MemoryAppend("/skill/qa", "SECOND");
        File.ReadAllText(path).Should().Be(original + "\n\nFIRST\n\nSECOND");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.md")
            .Select(File.ReadAllText).Should().ContainSingle().Which.Should().Be(original + "\n\nFIRST");
    }

    [Fact]
    public async Task MissingSkillAppendRefusesWithoutCreatingAnOverride()
    {
        using var scope = new TestHelpers.StoreScope();
        var result = ResponseParser.Parse(await new ScriniaMcpTools().MemoryAppend("/skill/missing", "suffix"));
        result.Status.Should().Be("error");
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        File.Exists(Path.Combine(PathRouter.ToFileEntityDir("skill", scope.WorkspaceDir), "missing.md"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task MissingAgentAppendRefusesWithoutCreatingAFile()
    {
        using var scope = new TestHelpers.StoreScope();
        var result = ResponseParser.Parse(await new ScriniaMcpTools().MemoryAppend("/agent/missing", "suffix"));
        result.Status.Should().Be("error");
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        File.Exists(Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "missing.md"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task AppendPreservesCuratedAndUnknownMetadata(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember($"/{kind}/metadata", ["body"]);
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "metadata.md");
        string metaPath = Path.ChangeExtension(path, ".meta.json");
        string prior = """{"createdAt":"2026-01-01","updatedAt":"old","v":1,"basedOn":"curated-hash","role":"curated-role","capabilities":["curated-tool"],"scaffold":"reviewer","future":{"nested":42}}""";
        File.WriteAllText(metaPath, prior);
        var result = ResponseParser.Parse(await tools.MemoryAppend($"/{kind}/metadata", "suffix"));
        result.Status.Should().Be("success");
        using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        using var original = JsonDocument.Parse(prior);
        foreach (var property in original.RootElement.EnumerateObject().Where(p => p.Name != "updatedAt"))
            JsonElement.DeepEquals(meta.RootElement.GetProperty(property.Name), property.Value).Should().BeTrue(property.Name);
        meta.RootElement.GetProperty("updatedAt").GetString().Should().NotBe("old");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.meta.json")
            .Select(File.ReadAllText).Should().Contain(prior);
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task AppendRefusesForwardMetadataBeforePublishingOrArchiving(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember($"/{kind}/future", ["body"]);
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "future.md");
        string before = File.ReadAllText(path);
        string metaPath = Path.ChangeExtension(path, ".meta.json");
        File.WriteAllText(metaPath, """{"v":999}""");
        Func<Task> append = () => tools.MemoryAppend($"/{kind}/future", "suffix");
        await append.Should().ThrowAsync<ScopeReadOnlyException>();
        File.ReadAllText(path).Should().Be(before);
        File.ReadAllText(metaPath).Should().Be("""{"v":999}""");
        Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, "versions")).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendPromotesCompleteLegacyAgentBody(bool withBom)
    {
        using var scope = new TestHelpers.StoreScope();
        string original = new string('x', 9000) + "LEGACY-TAIL";
        string legacyText = (withBom ? "\uFEFF" : string.Empty) + original;
        byte[] originalBytes = Encoding.UTF8.GetBytes(legacyText);
        await TestHelpers.Store.WriteArtifactAsync("legacy", "local:agent", Nmp2ChunkedEncoder.Encode(legacyText));
        var result = ResponseParser.Parse(await new ScriniaMcpTools().MemoryAppend("/agent/legacy", "suffix"));
        result.Status.Should().Be("success");
        File.ReadAllText(Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "legacy.md"))
            .Should().Be(original + "\nsuffix");
        File.Exists(TestHelpers.Store.FindArtifactPath("legacy", "local:agent")).Should().BeFalse();
        string versions = Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "versions");
        Directory.Exists(versions).Should().BeTrue("the promoted predecessor must be archived");
        string archivedBody = Directory.GetFiles(versions, "*.md").Should().ContainSingle().Subject;
        File.ReadAllText(archivedBody).Should().Be(original);
        File.ReadAllBytes(archivedBody).Should().Equal(originalBytes);
    }

    public static IEnumerable<object[]> PromotedAgentFailureCases =>
        new[] { FileEntityFaultPoint.ArchiveBodyWrite, FileEntityFaultPoint.BodyWrite,
            FileEntityFaultPoint.MetaWrite, FileEntityFaultPoint.VerificationRead }
            .SelectMany(point => PresenceStates.SelectMany(bodyExists =>
                PresenceStates.Select(metaExists => new object[] { (int)point, bodyExists, metaExists })));

    [Theory]
    [MemberData(nameof(PromotedAgentFailureCases))]
    public async Task PromotedAgentAppendFailureRestoresPhysicalStateAndRetainsUsableSource(
        int pointValue, bool bodyExists, bool metaExists)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string original = new string('x', 9000) + "LEGACY-FAILURE-TAIL";
        await TestHelpers.Store.WriteArtifactAsync("legacy-failure", "local:agent", Nmp2ChunkedEncoder.Encode(original));
        string legacyPath = TestHelpers.Store.FindArtifactPath("legacy-failure", "local:agent");
        byte[] legacyBytes = File.ReadAllBytes(legacyPath);
        string directory = PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir);
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "legacy-failure.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        byte[] oldBody = Encoding.UTF8.GetBytes(original);
        byte[] oldMeta = Encoding.UTF8.GetBytes("""{"createdAt":"original-created","future":{"nested":42}}""");
        if (bodyExists) File.WriteAllBytes(body, oldBody);
        if (metaExists) File.WriteAllBytes(meta, oldMeta);
        new FileEntityTransactionCoordinator(scope.WorkspaceDir).FailOnceAt((FileEntityFaultPoint)pointValue);
        try
        {
            var failed = ResponseParser.Parse(await tools.MemoryAppend("/agent/legacy-failure", "FAILED-SUFFIX"));
            failed.Status.Should().Be("error");
            File.Exists(body).Should().Be(bodyExists);
            File.Exists(meta).Should().Be(metaExists);
            if (bodyExists) File.ReadAllBytes(body).Should().Equal(oldBody);
            if (metaExists) File.ReadAllBytes(meta).Should().Equal(oldMeta);
            File.ReadAllBytes(legacyPath).Should().Equal(legacyBytes);
            string usableSource = await TestHelpers.Store.ReadArtifactAsync(
                Scrinia.Core.Models.MemoryAddress.Parse("/agent/legacy-failure"));
            Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(usableSource)).Should().Be(original);
            string versions = Path.Combine(directory, "versions");
            (Directory.Exists(versions) ? Directory.GetFiles(versions) : []).Should().BeEmpty();

            var retry = ResponseParser.Parse(await tools.MemoryAppend("/agent/legacy-failure", "RETRY-SUFFIX"));
            retry.Status.Should().Be("success");
            File.ReadAllText(body).Should().Be(original + "\nRETRY-SUFFIX");
            Directory.Exists(versions).Should().BeTrue("the successful retry must archive its predecessor");
            Directory.GetFiles(versions, "*.md").Select(File.ReadAllText)
                .Should().ContainSingle().Which.Should().Be(original);
            File.Exists(legacyPath).Should().Be(bodyExists);
        }
        finally { B0FaultSeam.ClearFaults(); }
    }

    [Theory]
    [InlineData(false, "touch")]
    [InlineData(false, "maintenance")]
    [InlineData(true, "touch")]
    [InlineData(true, "maintenance")]
    public async Task CommittedAgentRecoveryRetainsPredecessorArchives(bool diskSource, string route)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string original = new string('x', 9000) + "COMMITTED-TAIL";
        string directory = PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir);
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "committed-source.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        string oldMeta = """{"createdAt":"original-created","future":42}""";
        File.WriteAllText(meta, oldMeta);
        if (diskSource) File.WriteAllText(body, original);
        else await TestHelpers.Store.WriteArtifactAsync("committed-source", "local:agent", Nmp2ChunkedEncoder.Encode(original));
        new FileEntityTransactionCoordinator(scope.WorkspaceDir).FailOnceAt(FileEntityFaultPoint.EvidenceCleanup);
        try
        {
            var committed = ResponseParser.Parse(await tools.MemoryAppend("/agent/committed-source", "COMMITTED"));
            committed.Status.Should().Be("success_with_warning");
            string evidence = Directory.GetFiles(directory, ".file-entity-*.recovery").Should().ContainSingle().Subject;
            var journal = RecoveryLegacyTestParser.ParseFileEntity(File.ReadAllBytes(evidence), ".scrinia", evidence, directory);
            var snapshots = (FileEntityRecoveryBody)journal.TypedBody!;
            snapshots.OldBody.Existed.Should().Be(diskSource);
            snapshots.OldMeta.Existed.Should().BeTrue();
            string versions = Path.Combine(directory, "versions");
            Directory.GetFiles(versions, "*.md").Select(File.ReadAllText)
                .Should().ContainSingle().Which.Should().Be(original);
            Directory.GetFiles(versions, "*.meta.json").Select(File.ReadAllText)
                .Should().ContainSingle().Which.Should().Be(oldMeta);
            byte[] committedMeta = File.ReadAllBytes(meta);

            if (route == "touch")
            {
                new FileEntityTransactionCoordinator(scope.WorkspaceDir).FailOnceAt(FileEntityFaultPoint.RawBodyRead);
                Func<Task> touch = () => tools.MemoryAppend("/agent/committed-source", "UNCOMMITTED");
                await touch.Should().ThrowAsync<IOException>();
            }
            else
            {
                using WorkspaceLease lease = new WorkspaceAdmission(scope.WorkspaceDir, null)
                    .EnterMutation(TimeSpan.FromSeconds(5), CancellationToken.None);
                using FileLock held = FileLock.AcquireExclusive(Path.Combine(directory, ".lock"));
                string root = Path.Combine(scope.WorkspaceDir, ".scrinia");
                var recovery = new RecoveryDiscoveryCoordinator();
                RecoveryDiscoveryResult discovery = recovery.Discover(".scrinia", root);
                discovery.BlockingEvidence.Should().BeNull();
                discovery.Candidates.Should().ContainSingle();
                RecoveryOutcome outcome = recovery.AdmitNext(".scrinia", root, discovery, CancellationToken.None);
                outcome.IsBlocked.Should().BeFalse();
                outcome.IsCommitted.Should().BeTrue();
                outcome.CleanupPending.Should().BeFalse();
            }
            File.Exists(evidence).Should().BeFalse();
            File.ReadAllText(body).Should().Be(original + "\nCOMMITTED");
            File.ReadAllBytes(meta).Should().Equal(committedMeta);
            Directory.GetFiles(versions, "*.md").Select(File.ReadAllText)
                .Should().ContainSingle().Which.Should().Be(original);
            Directory.GetFiles(versions, "*.meta.json").Select(File.ReadAllText)
                .Should().ContainSingle().Which.Should().Be(oldMeta);
        }
        finally { B0FaultSeam.ClearFaults(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparedAgentRecoveryRestoresPhysicalStateAndRemovesAttemptArchives(bool bodyExists, bool metaExists)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string original = new string('x', 9000) + "PREPARED-TAIL";
        await TestHelpers.Store.WriteArtifactAsync("prepared-source", "local:agent", Nmp2ChunkedEncoder.Encode(original));
        string legacy = TestHelpers.Store.FindArtifactPath("prepared-source", "local:agent");
        byte[] legacyBytes = File.ReadAllBytes(legacy);
        string directory = PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir);
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "prepared-source.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        byte[] bodyBytes = Encoding.UTF8.GetBytes(original);
        byte[] metaBytes = Encoding.UTF8.GetBytes("""{"createdAt":"original-created"}""");
        if (bodyExists) File.WriteAllBytes(body, bodyBytes);
        if (metaExists) File.WriteAllBytes(meta, metaBytes);
        var coordinator = new FileEntityTransactionCoordinator(scope.WorkspaceDir);
        coordinator.FailOnceAt(FileEntityFaultPoint.VerificationRead);
        coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);
        try
        {
            var failed = ResponseParser.Parse(await tools.MemoryAppend("/agent/prepared-source", "FAILED"));
            failed.Status.Should().Be("error");
            failed.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
            string evidence = Directory.GetFiles(directory, ".file-entity-*.recovery").Should().ContainSingle().Subject;
            var journal = RecoveryLegacyTestParser.ParseFileEntity(File.ReadAllBytes(evidence), ".scrinia", evidence, directory);
            var snapshots = (FileEntityRecoveryBody)journal.TypedBody!;
            snapshots.OldBody.Existed.Should().Be(bodyExists);
            snapshots.OldMeta.Existed.Should().Be(metaExists);
            snapshots.OldBody.Payload.Should().Be(bodyExists ? Convert.ToBase64String(bodyBytes) : null);
            snapshots.OldMeta.Payload.Should().Be(metaExists ? Convert.ToBase64String(metaBytes) : null);
            string versions = Path.Combine(directory, "versions");
            Directory.Exists(versions).Should().BeTrue("the failed attempt archived its logical or physical predecessor");
            Directory.GetFiles(versions, "*.md").Select(File.ReadAllText)
                .Should().ContainSingle().Which.Should().Be(original);
            coordinator.FailOnceAt(FileEntityFaultPoint.RawBodyRead);
            Func<Task> recover = () => tools.MemoryAppend("/agent/prepared-source", "UNCOMMITTED");
            await recover.Should().ThrowAsync<IOException>();
            File.Exists(evidence).Should().BeFalse();
            File.Exists(body).Should().Be(bodyExists);
            File.Exists(meta).Should().Be(metaExists);
            if (bodyExists) File.ReadAllBytes(body).Should().Equal(bodyBytes);
            if (metaExists) File.ReadAllBytes(meta).Should().Equal(metaBytes);
            File.ReadAllBytes(legacy).Should().Equal(legacyBytes);
            Directory.GetFiles(versions).Should().BeEmpty();
        }
        finally { B0FaultSeam.ClearFaults(); }
    }


    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task EmptyPhysicalSnapshotsRoundTripAndRecover(bool committed, bool deleteBody, bool deleteMeta)
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir);
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "empty-recovery.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        File.WriteAllBytes(body, []);
        File.WriteAllBytes(meta, []);
        var mutation = new FileEntityMutation("agent", "empty-recovery", body, meta,
            Path.Combine(directory, "versions"), [], [], deleteBody, deleteMeta);
        var coordinator = new FileEntityTransactionCoordinator(scope.WorkspaceDir);
        if (committed) coordinator.FailOnceAt(FileEntityFaultPoint.EvidenceCleanup);
        else
        {
            coordinator.FailOnceAt(FileEntityFaultPoint.VerificationRead);
            coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);
        }
        try
        {
            FileEntityMutationResult result = await coordinator.ExecuteAsync(mutation);
            result.Outcome.Should().Be(committed ? WholeMemoryOutcome.SuccessWithWarning : WholeMemoryOutcome.RecoveryRequired);
            string evidence = Directory.GetFiles(directory, ".file-entity-*.recovery").Should().ContainSingle().Subject;
            var journal = RecoveryLegacyTestParser.ParseFileEntity(File.ReadAllBytes(evidence), ".scrinia", evidence, directory);
            var snapshots = (FileEntityRecoveryBody)journal.TypedBody!;
            snapshots.OldBody.Existed.Should().BeTrue();
            snapshots.OldBody.Payload.Should().BeEmpty();
            snapshots.OldMeta.Existed.Should().BeTrue();
            snapshots.OldMeta.Payload.Should().BeEmpty();
            if (committed)
            {
                snapshots.CommittedBody!.Existed.Should().Be(!deleteBody);
                snapshots.CommittedBody.Payload.Should().Be(deleteBody ? null : string.Empty);
                snapshots.CommittedMeta!.Existed.Should().Be(!deleteMeta);
                snapshots.CommittedMeta.Payload.Should().Be(deleteMeta ? null : string.Empty);
            }
            using WorkspaceLease lease = new WorkspaceAdmission(scope.WorkspaceDir, null)
                .EnterMutation(TimeSpan.FromSeconds(5), CancellationToken.None);
            using FileLock held = FileLock.AcquireExclusive(Path.Combine(directory, ".lock"));
            RecoveryOutcome recovered = coordinator.RecoverFileEntity(journal, evidence, ".scrinia", directory, CancellationToken.None);
            recovered.IsBlocked.Should().BeFalse();
            recovered.IsCommitted.Should().Be(committed);
            File.Exists(evidence).Should().BeFalse();
            File.Exists(body).Should().Be(!committed || !deleteBody);
            File.Exists(meta).Should().Be(!committed || !deleteMeta);
            if (File.Exists(body)) File.ReadAllBytes(body).Should().BeEmpty();
            if (File.Exists(meta)) File.ReadAllBytes(meta).Should().BeEmpty();
            string[] archives = Directory.GetFiles(Path.Combine(directory, "versions"));
            archives.Should().HaveCount(committed ? 2 : 0);
            foreach (string archive in archives) File.ReadAllBytes(archive).Should().BeEmpty();
        }
        finally { B0FaultSeam.ClearFaults(); }
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "YQ==")]
    [InlineData(true, null)]
    [InlineData(true, " ")]
    [InlineData(true, "\\t")]
    public void RecoveryCodecRejectsInconsistentOrWhitespaceMemberPayload(bool existed, string? payload)
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir);
        string body = Path.Combine(directory, "invalid-payload.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        string evidence = Path.Combine(directory, ".file-entity-invalid.recovery");
        var invalid = new RecoveryMemberSnapshot(body, existed, payload!, RecoveryPayload.Sha256HexLower([]));
        var valid = new RecoveryMemberSnapshot(meta, false, null!, RecoveryPayload.Sha256HexLower([]));
        var snapshots = new FileEntityRecoveryBody("agent", "invalid-payload", invalid, valid,
            null, null, null, null, "prepared");
        RecoveryEnvelopeDocument envelope = RecoveryEnvelopeCodec.FromFileEntity(snapshots, ".scrinia", null,
            "invalid", RecoveryLifecycleState.Prepared, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        FluentActions.Invoking(() => RecoveryLegacyTestParser.ParseFileEntity(
                RecoveryEnvelopeCodec.Serialize(envelope), ".scrinia", evidence, directory))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void RecoveryWriterDistinguishesAbsentFromExistingEmptyBeforeAndAfterMembers()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir);
        string body = Path.Combine(directory, "empty.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        var mutation = new FileEntityMutation("agent", "empty", body, meta,
            Path.Combine(directory, "versions"), [], null, false, true);
        var coordinator = new FileEntityTransactionCoordinator(scope.WorkspaceDir);
        RecoveryEnvelopeDocument envelope = coordinator.CreateEnvelope(mutation,
            RawMemberSnapshot.Capture(body, false, []), RawMemberSnapshot.Capture(meta, true, []),
            "writer", Path.Combine(directory, ".file-entity-writer.recovery"), null, null,
            RecoveryLifecycleState.Committed,
            RawMemberSnapshot.Capture(body, true, []), RawMemberSnapshot.Capture(meta, false, []));
        var snapshots = (FileEntityRecoveryBody)envelope.TypedBody!;
        snapshots.OldBody.Existed.Should().BeFalse();
        snapshots.OldBody.Payload.Should().BeNull();
        snapshots.OldMeta.Existed.Should().BeTrue();
        snapshots.OldMeta.Payload.Should().BeEmpty();
        snapshots.CommittedBody.Should().NotBeNull();
        snapshots.CommittedBody!.Existed.Should().BeTrue();
        snapshots.CommittedBody.Payload.Should().BeEmpty();
        snapshots.CommittedMeta.Should().NotBeNull();
        snapshots.CommittedMeta!.Existed.Should().BeFalse();
        snapshots.CommittedMeta.Payload.Should().BeNull();
    }

    [Theory]
    [InlineData("skill", "\n\n")]
    [InlineData("agent", "\n")]
    public async Task ConcurrentPublicAppendsKeepFullSerialPredecessors(string kind, string separator)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string address = $"/{kind}/concurrent";
        await tools.MemoryRemember(address, [new string('x', 9000) + "ORIGINAL-TAIL"]);
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "concurrent.md");
        string original = File.ReadAllText(path);
        var firstPublishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondContended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int publicationCount = 0;
        ILogger previousLogger = FileLock.DefaultLogger;
        Func<DurableWritePhase, string, Exception?>? previousInjector = DurableFile.FailureInjector;
        FileLock.DefaultLogger = new ContentionLogger(Path.Combine(Path.GetDirectoryName(path)!, ".lock"), secondContended);
        DurableFile.FailureInjector = (phase, destination) =>
        {
            if (phase == DurableWritePhase.BeforeMove && destination == path &&
                Interlocked.Increment(ref publicationCount) == 1)
            {
                firstPublishing.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                    return new TimeoutException("The test did not release the first publication.");
            }
            return null;
        };
        Task<string>? first = null;
        Task<string>? second = null;
        try
        {
            first = Task.Run(() => tools.MemoryAppend(address, "FIRST"));
            await firstPublishing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            second = Task.Run(() => tools.MemoryAppend(address, "SECOND"));
            // Contention proves the second append reached the entity lock before publication
            // resumes. An implementation that read the body first has already captured stale data.
            await secondContended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
            release.Set();
            ResponseParser.Parse(await first).Status.Should().Be("success");
            ResponseParser.Parse(await second).Status.Should().Be("success");
            string predecessor = original + separator + "FIRST";
            File.ReadAllText(path).Should().Be(predecessor + separator + "SECOND");
            Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.md")
                .Select(File.ReadAllText).Should().BeEquivalentTo([original, predecessor]);
        }
        finally
        {
            release.Set();
            try
            {
                if (first is not null) await first;
                if (second is not null) await second;
            }
            finally
            {
                DurableFile.FailureInjector = previousInjector;
                FileLock.DefaultLogger = previousLogger;
            }
        }
    }

    [Fact]
    public async Task TransformPreparationRetainsEntityLockAndCapturesTheCommittedPredecessor()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/agent/preparation", ["ORIGINAL-TAIL"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "preparation.md");
        var target = new FileEntityTarget("agent", "preparation", body,
            Path.ChangeExtension(body, ".meta.json"), Path.Combine(Path.GetDirectoryName(body)!, "versions"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ILogger previousLogger = FileLock.DefaultLogger;
        FileLock.DefaultLogger = new ContentionLogger(Path.Combine(Path.GetDirectoryName(body)!, ".lock"), contended);
        int secondPrepared = 0;
        string? secondBody = null;
        Task<FileEntityMutationResult>? first = null;
        Task<FileEntityMutationResult>? second = null;
        try
        {
            first = Task.Run(() => new FileEntityTransactionCoordinator(scope.WorkspaceDir).TransformAsync(target,
                async (current, ct) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return AppendedMutation(target, current, "FIRST");
                }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            second = Task.Run(() => new FileEntityTransactionCoordinator(scope.WorkspaceDir).TransformAsync(target,
                (current, _) =>
                {
                    Interlocked.Increment(ref secondPrepared);
                    secondBody = Encoding.UTF8.GetString(current.Body.Bytes);
                    return ValueTask.FromResult(AppendedMutation(target, current, "SECOND"));
                }));
            await contended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Volatile.Read(ref secondPrepared).Should().Be(0);
            release.TrySetResult();
            (await first).Outcome.Should().Be(WholeMemoryOutcome.Replaced);
            (await second).Outcome.Should().Be(WholeMemoryOutcome.Replaced);
            secondBody.Should().Be("ORIGINAL-TAIL\nFIRST");
            File.ReadAllText(body).Should().Be("ORIGINAL-TAIL\nFIRST\nSECOND");
        }
        finally
        {
            release.TrySetResult();
            try
            {
                if (first is not null) await first;
                if (second is not null) await second;
            }
            finally { FileLock.DefaultLogger = previousLogger; }
        }
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("name")]
    [InlineData("body")]
    [InlineData("meta")]
    [InlineData("versions")]
    public async Task TransformRefusesRetargetingBeforePublication(string member)
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().MemoryRemember("/agent/target", ["original"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "target.md");
        var target = new FileEntityTarget("agent", "target", body,
            Path.ChangeExtension(body, ".meta.json"), Path.Combine(Path.GetDirectoryName(body)!, "versions"));
        var changed = member switch
        {
            "kind" => target with { EntityKind = "skill" },
            "name" => target with { Name = "other" },
            "body" => target with { BodyPath = body + ".other" },
            "meta" => target with { MetaPath = target.MetaPath + ".other" },
            "versions" => target with { VersionsDirectory = target.VersionsDirectory + ".other" },
            _ => throw new InvalidOperationException()
        };
        Func<Task> transform = () => new FileEntityTransactionCoordinator(scope.WorkspaceDir)
            .TransformAsync(target, (current, _) => ValueTask.FromResult(AppendedMutation(changed, current, "suffix")));
        await transform.Should().ThrowAsync<ArgumentException>();
        File.ReadAllText(body).Should().Be("original");
        Directory.Exists(target.VersionsDirectory).Should().BeFalse();
    }

    [Theory]
    [InlineData("warning")]
    [InlineData("success_with_warning")]
    [InlineData("error")]
    public void PersistedIdentityProjectionRetainsStatusWarningsAndRecovery(string status)
    {
        var original = new McpResponse
        {
            Status = status, Info = ["warning"], OperationId = "operation",
            Scope = "skill/qa", JournalPath = "evidence", Reason = "recovery", ErrorCode = "CODE"
        };
        McpResponse projected = original.WithPersistedIdentity("/skill/qa");
        projected.Should().Be(original with { Path = "/skill/qa", PersistedPath = "/skill/qa" });
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task AppendRetainsCommittedWarningAndRecoveryIdentity(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string address = $"/{kind}/warning";
        await tools.MemoryRemember(address, ["original"]);
        B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.FileEntityEvidenceCleanup, FileEntityFaultPoint.EvidenceCleanup.ToString());
        try
        {
            McpResponse response = kind == "skill"
                ? await ScriniaMcpTools.AppendSkillAsync("warning", ["suffix"], CancellationToken.None)
                : await tools.AppendAgentAsync("warning", ["suffix"], CancellationToken.None);
            response.Status.Should().Be("success_with_warning");
            response.Path.Should().Be(address);
            response.PersistedPath.Should().Be(address);
            response.OperationId.Should().NotBeNullOrEmpty();
            response.Info.Should().ContainSingle().Which.Should().Contain("cleanup");
            response.JournalPath.Should().NotBeNullOrEmpty();
        }
        finally { B0FaultSeam.ClearFaults(); }
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task FirstReservedPublicationRetainsCleanupWarningAndRecoveryIdentity(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.FileEntityEvidenceCleanup, FileEntityFaultPoint.EvidenceCleanup.ToString());
        try
        {
            McpResponse response = kind == "skill"
                ? await ScriniaMcpTools.AppendSkillAsync("qa", ["suffix"], CancellationToken.None)
                : await new ScriniaMcpTools().WriteAgentAsync("qa", ["body"], CancellationToken.None);
            response.Status.Should().Be("success_with_warning");
            response.Path.Should().Be($"/{kind}/qa");
            response.PersistedPath.Should().Be($"/{kind}/qa");
            response.OperationId.Should().NotBeNullOrEmpty();
            response.Info.Should().ContainSingle().Which.Should().Contain("cleanup");
            response.JournalPath.Should().NotBeNullOrEmpty();
        }
        finally { B0FaultSeam.ClearFaults(); }
    }

    [Theory]
    [InlineData("skill", "\n\n")]
    [InlineData("agent", "\n")]
    public async Task AppendArchivesExactHandAuthoredUtf8Bytes(string kind, string separator)
    {
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "unicode.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string original = "\u00e9" + new string('x', 9000) + "TAIL";
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(original)];
        File.WriteAllBytes(path, bytes);
        var result = ResponseParser.Parse(await new ScriniaMcpTools().MemoryAppend($"/{kind}/unicode", "\u8ffd\u52a0"));
        result.Status.Should().Be("success");
        File.ReadAllText(path).Should().Be(original + separator + "\u8ffd\u52a0");
        string archive = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.md")
            .Should().ContainSingle().Subject;
        File.ReadAllBytes(archive).Should().Equal(bytes);
    }

    private static FileEntityMutation AppendedMutation(
        FileEntityTarget target, FileEntityCurrentState current, string suffix) =>
        new(target.EntityKind, target.Name, target.BodyPath, target.MetaPath, target.VersionsDirectory,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(current.Body.Bytes) + "\n" + suffix),
            current.Meta.Bytes, false, false);

    private sealed class ContentionLogger(string lockPath, TaskCompletionSource contended) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "LockContended" &&
                state is IEnumerable<KeyValuePair<string, object?>> values &&
                values.Any(value => value.Key == "LockPath" && value.Value?.ToString() == lockPath))
                contended.TrySetResult();
        }
    }
}
