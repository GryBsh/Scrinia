using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class FileEntityTransactionTests
{
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

    private static EntityFilePaths EntityPaths(TestHelpers.StoreScope scope)
    {
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(directory);
        return new(
            Path.Combine(directory, "qa.md"),
            Path.Combine(directory, "qa.meta.json"),
            Path.Combine(directory, "versions"));
    }

    private sealed record EntityFilePaths(string Body, string Meta, string Versions);
}
