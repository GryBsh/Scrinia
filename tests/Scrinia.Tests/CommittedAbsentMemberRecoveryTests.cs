using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class CommittedAbsentMemberRecoveryTests
{
    [Theory]
    [InlineData("touch", "body")]
    [InlineData("maintenance", "body")]
    [InlineData("touch", "meta")]
    [InlineData("maintenance", "meta")]
    [InlineData("touch", "both")]
    [InlineData("maintenance", "both")]
    [InlineData("touch", "sidecar")]
    [InlineData("maintenance", "sidecar")]
    [InlineData("touch", "missing-meta")]
    [InlineData("maintenance", "missing-meta")]
    [InlineData("touch", "present-empty")]
    [InlineData("maintenance", "present-empty")]
    public Task ActualCommittedPublisherSnapshotsCleanOnlyAfterExistenceIsProven(string route, string shape) =>
        Exercise(route, shape, "missing");

    [SkippableTheory]
    [InlineData("touch", "empty")]
    [InlineData("maintenance", "empty")]
    [InlineData("touch", "nonempty")]
    [InlineData("maintenance", "nonempty")]
    [InlineData("touch", "directory")]
    [InlineData("maintenance", "directory")]
    [InlineData("touch", "link")]
    [InlineData("maintenance", "link")]
    [InlineData("touch", "denied")]
    [InlineData("maintenance", "denied")]
    [InlineData("touch", "io")]
    [InlineData("maintenance", "io")]
    public Task UnexpectedEntriesRetainCommittedDeletionEvidence(string route, string obstruction) =>
        Exercise(route, "body", obstruction);

    private static async Task Exercise(string route, string shape, string obstruction)
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = PathRouter.ToFileEntityDir("skill", scope.WorkspaceDir);
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "absence.md");
        string meta = Path.ChangeExtension(body, ".meta.json");
        string versions = Path.Combine(directory, "versions");
        File.WriteAllText(body, "old body");
        if (shape != "missing-meta") File.WriteAllText(meta, "{}");
        bool sidecar = shape == "sidecar";
        var mutation = new FileEntityMutation(sidecar ? "sidecar" : "skill", "absence",
            sidecar ? meta : body, meta, versions,
            shape == "present-empty" ? [] : "new body"u8.ToArray(),
            shape == "missing-meta" ? null : "{}"u8.ToArray(),
            deleteBody: shape is "body" or "both",
            deleteMeta: shape is "meta" or "both" or "sidecar");
        var coordinator = new FileEntityTransactionCoordinator(scope.WorkspaceDir);
        coordinator.FailOnceAt(FileEntityFaultPoint.EvidenceCleanup);
        try
        {
            FileEntityMutationResult committed = await coordinator.ExecuteAsync(mutation);
            committed.Recovery.Should().NotBeNull();
            string evidence = Directory.GetFiles(directory, ".file-entity-*.recovery").Should().ContainSingle().Subject;
            byte[] priorEvidence = File.ReadAllBytes(evidence);
            var archives = Directory.GetFiles(versions).ToDictionary(p => p, File.ReadAllBytes);
            if (obstruction == "empty") File.WriteAllBytes(body, []);
            if (obstruction == "nonempty") File.WriteAllText(body, "unexpected");
            if (obstruction == "directory") Directory.CreateDirectory(body);
            if (obstruction == "link")
            {
                string linkTarget = Path.Combine(scope.WorkspaceDir, ".scrinia", "contained-target");
                Directory.CreateDirectory(linkTarget);
                try { Directory.CreateSymbolicLink(body, linkTarget); }
                catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
                { Skip.If(true, "Symbolic link creation is unavailable: " + exception.GetType().Name); }
            }
            if (obstruction is "denied" or "io")
                RecoveryAbsenceVerifier.InspectionFailureInjector.Value = path => path == body
                    ? obstruction == "denied" ? new UnauthorizedAccessException("unprovable inspection") : new IOException("unprovable inspection")
                    : null;
            bool shouldClean = obstruction == "missing";
            byte[]? currentBody = File.Exists(body) ? File.ReadAllBytes(body) : null;
            byte[]? currentMeta = File.Exists(meta) ? File.ReadAllBytes(meta) : null;

            if (route == "touch")
            {
                bool prepared = false;
                Func<Task> touch = async () =>
                {
                    FileEntityMutationResult result = await coordinator.TransformAsync(
                        new FileEntityTarget(mutation.EntityKind, "absence", mutation.BodyPath, meta, versions),
                        (_, _) => { prepared = true; throw new IOException("after recovery probe"); });
                    result.Outcome.Should().Be(WholeMemoryOutcome.RecoveryRequired);
                };
                if (shouldClean)
                    await touch.Should().ThrowAsync<IOException>().WithMessage("after recovery probe");
                else
                    await touch.Should().NotThrowAsync();
                prepared.Should().Be(shouldClean);
            }
            else
            {
                using WorkspaceLease lease = new WorkspaceAdmission(scope.WorkspaceDir, null)
                    .EnterMutation(TimeSpan.FromSeconds(5), CancellationToken.None);
                using FileLock held = FileLock.AcquireExclusive(Path.Combine(directory, ".lock"));
                string root = Path.Combine(scope.WorkspaceDir, ".scrinia");
                var recovery = new RecoveryDiscoveryCoordinator();
                RecoveryDiscoveryResult discovery = recovery.Discover(".scrinia", root);
                if (discovery.BlockingEvidence is not null)
                {
                    shouldClean.Should().BeFalse();
                    discovery.BlockingEvidence.Reason.Should().Be(RecoveryBlockReason.Unsafe);
                    discovery.BlockingEvidence.EvidencePath.Should().Be(evidence);
                }
                else
                {
                    discovery.Candidates.Should().ContainSingle();
                    RecoveryOutcome? outcome = null;
                    Exception? failure = Record.Exception(() => outcome = recovery.AdmitNext(".scrinia", root, discovery, CancellationToken.None));
                    if (shouldClean)
                    {
                        failure.Should().BeNull();
                        outcome!.IsBlocked.Should().BeFalse();
                        outcome.IsCommitted.Should().BeTrue();
                        outcome.CleanupPending.Should().BeFalse();
                    }
                    else
                        (failure is RecoveryEnvelopeException || outcome?.IsBlocked == true).Should().BeTrue();
                }
            }
            File.Exists(evidence).Should().Be(!shouldClean);
            if (!shouldClean) File.ReadAllBytes(evidence).Should().Equal(priorEvidence);
            if (currentBody is not null) File.ReadAllBytes(body).Should().Equal(currentBody);
            else if (obstruction is "directory" or "link") Directory.Exists(body).Should().BeTrue();
            else File.Exists(body).Should().BeFalse();
            if (currentMeta is not null) File.ReadAllBytes(meta).Should().Equal(currentMeta);
            else File.Exists(meta).Should().BeFalse();
            Directory.GetFiles(versions).Should().BeEquivalentTo(archives.Keys);
            foreach (var archive in archives) File.ReadAllBytes(archive.Key).Should().Equal(archive.Value);
        }
        finally
        {
            RecoveryAbsenceVerifier.InspectionFailureInjector.Value = null;
            B0FaultSeam.ClearFaults();
        }
    }
}
