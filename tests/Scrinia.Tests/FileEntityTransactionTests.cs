using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;
using System.Text.Json;
using Xunit;

namespace Scrinia.Tests;

public sealed class FileEntityTransactionTests
{
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
