using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;
using System.Text.Json;
using Scrinia.Core.Formats;
using Xunit;

namespace Scrinia.Tests;

public sealed class FileEntityTransactionTests
{
    [Fact]
    public async Task SkillRewrite_PublishesBodyAndSidecarAsAnArchivedPair()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/skill/task6", content: ["old instructions"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("skill", TestHelpers.Store.WorkspaceRoot), "task6.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        string oldMeta = File.ReadAllText(meta);

        await tools.MemoryRemember(path: "/skill/task6", content: ["new instructions"]);

        File.ReadAllText(body).Should().Contain("new instructions");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(body)!, "versions"))
            .Should().HaveCount(2);
        string archivedBody = Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(body)!, "versions"), "*.md").Should().ContainSingle().Subject;
        File.ReadAllText(archivedBody).Should().Contain("old instructions");
        string archivedMeta = Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(body)!, "versions"), "*.meta.json").Should().ContainSingle().Subject;
        File.ReadAllText(archivedMeta).Should().Contain(oldMeta.Trim());
    }

    [Fact]
    public async Task AgentRewrite_PreservesUnknownSidecarFields()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/agent/profile", content: ["old profile"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("agent", TestHelpers.Store.WorkspaceRoot), "profile.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        File.WriteAllText(meta, "{\"createdAt\":\"2026-01-01T00:00:00Z\",\"futureField\":42}");

        await tools.MemoryRemember(path: "/agent/profile", content: ["new profile"]);

        string rewritten = File.ReadAllText(meta);
        rewritten.Should().Contain("futureField");
        rewritten.Should().Contain("2026-01-01");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(body)!, "versions"))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task SkillRewrite_WithNewerSidecarDoesNotCreateArchiveOrReplaceBody()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/skill/task6-newer", content: ["old instructions"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("skill", TestHelpers.Store.WorkspaceRoot), "task6-newer.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        File.WriteAllText(meta, "{\"v\":999}");

        string response = await tools.MemoryRemember(path: "/skill/task6-newer", content: ["new instructions"]);

        ResponseParser.Parse(response).ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        File.ReadAllText(body).Should().Contain("old instructions");
        Directory.Exists(Path.Combine(Path.GetDirectoryName(body)!, "versions")).Should().BeFalse();
    }

    [Fact]
    public async Task SkillRewrite_WithMalformedSidecarRefusesBeforeChangingAnyMember()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/skill/task6-malformed", content: ["old instructions"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("skill", TestHelpers.Store.WorkspaceRoot), "task6-malformed.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        File.WriteAllText(meta, "not-json");
        string versions = Path.Combine(Path.GetDirectoryName(body)!, "versions");

        var response = ResponseParser.Parse(
            await tools.MemoryRemember(path: "/skill/task6-malformed", content: ["new instructions"]));

        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        File.ReadAllText(body).Should().Contain("old instructions");
        File.ReadAllText(meta).Should().Be("not-json");
        (Directory.Exists(versions) ? Directory.GetFiles(versions).Length : 0).Should().Be(0);
    }

    [Fact]
    public async Task AgentRewrite_WithMalformedSidecarRefusesBeforeChangingAnyMember()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/agent/task6-malformed", content: ["old profile"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("agent", TestHelpers.Store.WorkspaceRoot), "task6-malformed.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        File.WriteAllText(meta, "not-json");
        string versions = Path.Combine(Path.GetDirectoryName(body)!, "versions");

        var response = ResponseParser.Parse(
            await tools.MemoryRemember(path: "/agent/task6-malformed", content: ["new profile"]));

        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        File.ReadAllText(body).Should().Be("old profile");
        File.ReadAllText(meta).Should().Be("not-json");
        (Directory.Exists(versions) ? Directory.GetFiles(versions).Length : 0).Should().Be(0);
    }

    [Fact]
    public async Task AgentRewrite_WithNewerSidecarRefusesBeforeChangingAnyMember()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/agent/task6-newer", content: ["old profile"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("agent", TestHelpers.Store.WorkspaceRoot), "task6-newer.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        File.WriteAllText(meta, "{\"v\":999}");
        string versions = Path.Combine(Path.GetDirectoryName(body)!, "versions");

        var response = ResponseParser.Parse(
            await tools.MemoryRemember(path: "/agent/task6-newer", content: ["new profile"]));

        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        File.ReadAllText(body).Should().Be("old profile");
        File.ReadAllText(meta).Should().Contain("999");
        (Directory.Exists(versions) ? Directory.GetFiles(versions).Length : 0).Should().Be(0);
    }

    [Fact]
    public async Task SkillDelete_ArchivesBothMembersAndRevealsBuiltInOverride()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/skill/qa", content: ["project override"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("skill", TestHelpers.Store.WorkspaceRoot), "qa.md");
        string meta = Path.ChangeExtension(body, ".meta.json");

        ResponseParser.Parse(await tools.MemoryForget(path: "/skill/qa")).Status.Should().Be("success");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(body)!, "versions"))
            .Should().HaveCount(2);
        File.Exists(body).Should().BeFalse();
        File.Exists(meta).Should().BeFalse();
        ResponseParser.Parse(await tools.MemoryRecall(path: "/skill/qa")).Content
            .Should().NotContain("project override");
    }

    [Fact]
    public async Task SkillDelete_BuiltInOnlyRemainsNonDeletable()
    {
        using var scope = new TestHelpers.StoreScope();

        var response = ResponseParser.Parse(await new ScriniaMcpTools().MemoryForget(path: "/skill/qa"));

        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task SkillDeleteFailureRestoresBodyAndSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/skill/task6-delete-failure", content: ["preserve me"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("skill", TestHelpers.Store.WorkspaceRoot), "task6-delete-failure.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        string versions = Path.Combine(Path.GetDirectoryName(body)!, "versions");
        int versionsBefore = Directory.Exists(versions) ? Directory.GetFiles(versions).Length : 0;

        DurableFile.FailureInjector = static (phase, destination) =>
            phase == DurableWritePhase.BeforeMove && destination.EndsWith(".meta.json", StringComparison.Ordinal)
                ? new IOException("injected delete failure") : null;
        try
        {
            var response = ResponseParser.Parse(
                await tools.MemoryForget(path: "/skill/task6-delete-failure"));

            response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
            File.ReadAllText(body).Should().Contain("preserve me");
            File.Exists(meta).Should().BeTrue();
            (Directory.Exists(versions) ? Directory.GetFiles(versions).Length : 0)
                .Should().Be(versionsBefore);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }
    }

    [Fact]
    public async Task AgentRewriteFailureReturnsDurableErrorWithoutClaimingSuccess()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "/agent/failure", content: ["old profile"]);
        DurableFile.FailureInjector = static (phase, _) =>
            phase == DurableWritePhase.BeforeMove ? new IOException("injected") : null;

        try
        {
            var response = ResponseParser.Parse(
                await tools.MemoryRemember(path: "/agent/failure", content: ["new profile"]));

            response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
            response.Status.Should().NotBe("success");
            response.OperationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }
    }

    [Fact]
    public async Task MetadataOnly_CommitEvidenceRecoveryVerifiesTheMetadataMember()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "body remains unchanged");
        File.WriteAllText(paths.Meta, "old metadata");
        var coordinator = Coordinator(scope);
        coordinator.FailOnceAt(FileEntityFaultPoint.EvidenceCleanup);

        FileEntityMutationResult committed = await coordinator.ExecuteAsync(
            SidecarMutation(paths, "committed metadata"));

        committed.Outcome.Should().Be(WholeMemoryOutcome.SuccessWithWarning);
        File.ReadAllText(paths.Meta).Should().Be("committed metadata");

        FileEntityMutationResult recovered = await Coordinator(scope).ExecuteAsync(
            SidecarMutation(paths, "next metadata"));

        recovered.Outcome.Should().Be(WholeMemoryOutcome.Replaced);
        File.ReadAllText(paths.Body).Should().Be("body remains unchanged");
        File.ReadAllText(paths.Meta).Should().Be("next metadata");
    }

    [Theory]
    [InlineData((int)FileEntityFaultPoint.MetaWrite)]
    [InlineData((int)FileEntityFaultPoint.VerificationRead)]
    public async Task MetadataOnly_PublicationFailuresRestoreTheRawSidecar(int pointValue)
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "body remains unchanged");
        byte[] oldMetadata = [0, 1, 2, 3, 4, 5];
        File.WriteAllBytes(paths.Meta, oldMetadata);
        var coordinator = Coordinator(scope);
        coordinator.FailOnceAt((FileEntityFaultPoint)pointValue);

        await FluentActions.Invoking(() => coordinator.ExecuteAsync(
                SidecarMutation(paths, "new metadata")))
            .Should().ThrowAsync<WholeMemoryTransactionException>();

        File.ReadAllText(paths.Body).Should().Be("body remains unchanged");
        File.ReadAllBytes(paths.Meta).Should().Equal(oldMetadata);
    }

    [Fact]
    public async Task MetadataOnly_RollbackDurableFailureRetainsRecoveryEvidence()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "body remains unchanged");
        File.WriteAllText(paths.Meta, "old metadata");
        var coordinator = Coordinator(scope);
        coordinator.FailOnceAt(FileEntityFaultPoint.VerificationRead);
        coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);

        FileEntityMutationResult result = await coordinator.ExecuteAsync(
            SidecarMutation(paths, "new metadata"));

        result.Outcome.Should().Be(WholeMemoryOutcome.RecoveryRequired);
        result.Recovery.Should().NotBeNull();
        File.Exists(result.Recovery!.EvidencePath).Should().BeTrue();
        File.ReadAllBytes(paths.Meta).Should().NotBeEmpty();
    }

    [Fact]
    public void WriteSidecarMeta_UsesTheCoordinatorAndPreservesUnknownFields()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "body remains unchanged");
        const string oldMeta = "{\"role\":\"reviewer\",\"futureThing\":42}";
        File.WriteAllText(paths.Meta, oldMeta);
        var metadata = JsonSerializer.Deserialize(oldMeta, ScriniaMcpJsonContext.Default.SkillFileMeta)!;
        metadata = metadata with { Role = "researcher" };

        ScriniaMcpTools.WriteSidecarMeta(
            paths.Body, metadata, ScriniaMcpJsonContext.Default.SkillFileMeta);

        File.ReadAllText(paths.Body).Should().Be("body remains unchanged");
        File.ReadAllText(paths.Meta).Should().Contain("futureThing");
        Directory.GetFiles(paths.Versions).Should().ContainSingle();
        File.ReadAllText(Directory.GetFiles(paths.Versions)[0]).Should().Be(oldMeta);
    }

    [Fact]
    public void ReadSidecarMeta_ReturnsNullForMalformedJson()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Meta, "not-json");

        var metadata = ScriniaMcpTools.ReadSidecarMeta(
            paths.Body, ScriniaMcpJsonContext.Default.SkillFileMeta);

        metadata.Should().BeNull();
    }

    [Fact]
    public async Task Publication_ReplacesBodyAndMetadataAsAPair()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "{\"role\":\"old\"}");

        FileEntityMutationResult result = await Coordinator(scope).ExecuteAsync(
            Mutation(paths, "new body", "{\"role\":\"new\"}"));

        result.Outcome.Should().Be(WholeMemoryOutcome.Replaced);
        File.ReadAllText(paths.Body).Should().Be("new body");
        File.ReadAllText(paths.Meta).Should().Be("{\"role\":\"new\"}");
        Directory.GetFiles(paths.Versions).Should().HaveCount(2);
    }

    [Fact]
    public async Task OmittedMetadataPreservesAnExistingMalformedOrphanSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "not-json");

        await Coordinator(scope).ExecuteAsync(Mutation(paths, "new body", newMeta: null));

        File.ReadAllText(paths.Body).Should().Be("new body");
        File.ReadAllText(paths.Meta).Should().Be("not-json");
    }

    [Fact]
    public async Task ExplicitMetadataDeleteRemovesMetadataWithoutSynthesizingReplacement()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "{\"role\":\"old\"}");

        await Coordinator(scope).ExecuteAsync(Mutation(paths, "new body", newMeta: null, deleteMeta: true));

        File.Exists(paths.Body).Should().BeTrue();
        File.Exists(paths.Meta).Should().BeFalse();
    }

    [Fact]
    public async Task FailureAfterBodyPublicationRollsBackBothMembers()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "old meta");
        var coordinator = Coordinator(scope);
        coordinator.FailOnceAt(FileEntityFaultPoint.MetaWrite);

        await FluentActions.Invoking(() => coordinator.ExecuteAsync(Mutation(paths, "new body", "new meta")))
            .Should().ThrowAsync<WholeMemoryTransactionException>();

        File.ReadAllText(paths.Body).Should().Be("old body");
        File.ReadAllText(paths.Meta).Should().Be("old meta");
    }

    [Theory]
    [InlineData((int)FileEntityFaultPoint.ArchiveBodyWrite)]
    [InlineData((int)FileEntityFaultPoint.ArchiveMetaWrite)]
    [InlineData((int)FileEntityFaultPoint.BodyWrite)]
    [InlineData((int)FileEntityFaultPoint.MetaWrite)]
    [InlineData((int)FileEntityFaultPoint.AfterBodyDelete)]
    [InlineData((int)FileEntityFaultPoint.VerificationRead)]
    public async Task PublicationFailurePointsLeaveTheOriginalPairIntact(int pointValue)
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "old meta");
        var coordinator = Coordinator(scope);
        coordinator.FailOnceAt((FileEntityFaultPoint)pointValue);

        await FluentActions.Invoking(() => coordinator.ExecuteAsync(Mutation(paths, "new body", "new meta")))
            .Should().ThrowAsync<WholeMemoryTransactionException>();

        File.ReadAllText(paths.Body).Should().Be("old body");
        File.ReadAllText(paths.Meta).Should().Be("old meta");
    }

    [Fact]
    public async Task RollbackFailureRetainsDurableEvidenceAndRetryRecoversIt()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "old meta");
        var coordinator = Coordinator(scope);
        coordinator.FailOnceAt(FileEntityFaultPoint.VerificationRead);
        coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);

        FileEntityMutationResult failed = await coordinator.ExecuteAsync(Mutation(paths, "new body", "new meta"));

        failed.Outcome.Should().Be(WholeMemoryOutcome.RecoveryRequired);
        failed.Recovery.Should().NotBeNull();
        File.Exists(failed.Recovery!.EvidencePath).Should().BeTrue();
        File.ReadAllBytes(failed.Recovery.EvidencePath).Should().NotBeEmpty();

        FileEntityMutationResult retried = await Coordinator(scope).ExecuteAsync(
            Mutation(paths, "final body", "final meta"));

        retried.Outcome.Should().Be(WholeMemoryOutcome.Replaced, retried.Recovery?.Detail);
        File.ReadAllText(paths.Body).Should().Be("final body");
        File.ReadAllText(paths.Meta).Should().Be("final meta");
        File.Exists(failed.Recovery.EvidencePath).Should().BeFalse();
    }

    [Fact]
    public async Task RetainedPostCommitEvidencePreservesTheCommittedPairDuringRecovery()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "old meta");
        var coordinator = Coordinator(scope);
        coordinator.FailOnceAt(FileEntityFaultPoint.EvidenceCleanup);

        FileEntityMutationResult committed = await coordinator.ExecuteAsync(
            Mutation(paths, "committed body", "committed meta"));

        committed.Outcome.Should().Be(WholeMemoryOutcome.SuccessWithWarning);
        committed.Recovery.Should().NotBeNull();
        File.ReadAllText(paths.Body).Should().Be("committed body");
        File.ReadAllText(paths.Meta).Should().Be("committed meta");

        var retry = Coordinator(scope);
        retry.FailOnceAt(FileEntityFaultPoint.RawBodyRead);
        await FluentActions.Invoking(() => retry.ExecuteAsync(
                Mutation(paths, "next body", "next meta")))
            .Should().ThrowAsync<IOException>();

        File.ReadAllText(paths.Body).Should().Be("committed body");
        File.ReadAllText(paths.Meta).Should().Be("committed meta");
        File.Exists(committed.Recovery.EvidencePath).Should().BeFalse();
    }

    [Fact]
    public async Task RecoveryEvidenceWithTraversalPathsIsRetainedWithoutMutatingOutsideEntityDirectory()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "old meta");
        var coordinator = Coordinator(scope);
        coordinator.FailOnceAt(FileEntityFaultPoint.VerificationRead);
        coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);

        FileEntityMutationResult failed = await coordinator.ExecuteAsync(
            Mutation(paths, "new body", "new meta"));
        string evidencePath = failed.Recovery!.EvidencePath;
        string outsidePath = Path.Combine(Path.GetDirectoryName(paths.Body)!, "..", "outside.md");
        string canonicalOutsidePath = Path.GetFullPath(outsidePath);
        File.WriteAllText(canonicalOutsidePath, "outside original");
        OverwriteEvidenceWithTraversalPath(evidencePath, canonicalOutsidePath, paths.Meta);

        FileEntityMutationResult retry = await Coordinator(scope).ExecuteAsync(
            Mutation(paths, "next body", "next meta"));

        retry.Outcome.Should().Be(WholeMemoryOutcome.RecoveryRequired);
        retry.Recovery.Should().NotBeNull();
        File.ReadAllText(canonicalOutsidePath).Should().Be("outside original");
        File.Exists(evidencePath).Should().BeTrue();
    }

    [Fact]
    public async Task CancellationBeforePublicationLeavesBothMembersUntouched()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        File.WriteAllText(paths.Body, "old body");
        File.WriteAllText(paths.Meta, "old meta");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => Coordinator(scope).ExecuteAsync(
                Mutation(paths, "new body", "new meta"), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        File.ReadAllText(paths.Body).Should().Be("old body");
        File.ReadAllText(paths.Meta).Should().Be("old meta");
    }

    [Fact]
    public async Task EntityLockBlocksASecondCoordinatorUntilTheFirstReleasesIt()
    {
        using var scope = new TestHelpers.StoreScope();
        var paths = EntityPaths(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Body)!);
        using FileLock held = FileLock.AcquireExclusive(Path.Combine(Path.GetDirectoryName(paths.Body)!, ".lock"));
        Task<FileEntityMutationResult> pending = Task.Run(() => Coordinator(scope).ExecuteAsync(
            Mutation(paths, "body", "meta")));

        await Task.Delay(100);
        pending.IsCompleted.Should().BeFalse();
        held.Dispose();
        (await pending).Outcome.Should().Be(WholeMemoryOutcome.Created);
    }

    [Theory]
    [InlineData("skill", "locked-skill")]
    [InlineData("agent", "locked-agent")]
    public async Task PublicFileEntityWrite_MapsLockTimeoutToStructuredDurableError(
        string entityKind, string name)
    {
        using var scope = new TestHelpers.StoreScope();
        string entityDirectory = PathRouter.ToFileEntityDir(entityKind, TestHelpers.Store.WorkspaceRoot);
        Directory.CreateDirectory(entityDirectory);
        string lockPath = Path.Combine(entityDirectory, ".lock");
        using FileLock held = FileLock.AcquireExclusive(lockPath);
        var tools = new ScriniaMcpTools();
        string path = $"/{entityKind}/{name}";

        string response = await tools.MemoryRemember(path, ["content"]);

        var parsed = ResponseParser.Parse(response);
        parsed.Status.Should().Be("error");
        parsed.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        parsed.Error.Should().Contain("Failed to acquire file lock");
    }

    [Fact]
    public async Task SkillForget_MapsLockTimeoutToStructuredDurableError()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/skill/locked-forget", ["content"]);

        string skillsDirectory = PathRouter.ToFileEntityDir("skill", TestHelpers.Store.WorkspaceRoot);
        using FileLock held = FileLock.AcquireExclusive(Path.Combine(skillsDirectory, ".lock"));

        string response = await tools.MemoryForget("/skill/locked-forget");

        var parsed = ResponseParser.Parse(response);
        parsed.Status.Should().Be("error");
        parsed.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        parsed.Error.Should().Contain("Failed to acquire file lock");
    }

    private static FileEntityTransactionCoordinator Coordinator(TestHelpers.StoreScope scope) =>
        new(scope.WorkspaceDir);

    private static FileEntityMutation Mutation(
        EntityFilePaths paths,
        string body,
        string? newMeta,
        bool deleteMeta = false) =>
        new("skill", "qa", paths.Body, paths.Meta, paths.Versions,
            System.Text.Encoding.UTF8.GetBytes(body),
            newMeta is null ? null : System.Text.Encoding.UTF8.GetBytes(newMeta),
            deleteBody: false, deleteMeta: deleteMeta);

    private static FileEntityMutation SidecarMutation(EntityFilePaths paths, string metadata) =>
        new("sidecar", "qa", paths.Meta, paths.Meta, paths.Versions,
            [], System.Text.Encoding.UTF8.GetBytes(metadata), deleteBody: false, deleteMeta: false);

    private static EntityFilePaths EntityPaths(TestHelpers.StoreScope scope)
    {
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(directory);
        return new(
            Path.Combine(directory, "qa.md"),
            Path.Combine(directory, "qa.meta.json"),
            Path.Combine(directory, "versions"));
    }

    private static void OverwriteEvidenceWithTraversalPath(string evidencePath, string outsidePath, string metaPath)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SCRINIA-FILE-ENTITY-RECOVERY-1");
            writer.Write((byte)0);
            WriteSnapshot(writer, outsidePath, false, []);
            WriteSnapshot(writer, metaPath, true, System.Text.Encoding.UTF8.GetBytes("old meta"));
            writer.Write(false);
            writer.Write(false);
            writer.Write(false);
            writer.Write(false);
        }

        File.WriteAllBytes(evidencePath, stream.ToArray());
    }

    private static void WriteSnapshot(BinaryWriter writer, string path, bool existed, byte[] bytes)
    {
        writer.Write(path);
        writer.Write(existed);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        writer.Write(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    private sealed record EntityFilePaths(string Body, string Meta, string Versions);
}
