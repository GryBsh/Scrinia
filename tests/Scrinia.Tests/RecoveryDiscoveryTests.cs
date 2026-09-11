using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class RecoveryDiscoveryTests
{
    [Fact]
    public void Discover_AdmitsPreservedEvidenceInDeterministicOperationOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string scopeDirectory = Path.Combine(root, "local-notes");
        string entityDirectory = Path.Combine(scopeDirectory, "note");
        Directory.CreateDirectory(entityDirectory);
        try
        {
            RecoveryTestFixtures.WriteLegacyRestoreJournal(scopeDirectory);
            RecoveryTestFixtures.WriteLegacyAppendJournal(scopeDirectory);
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(entityDirectory);

            RecoveryDiscoveryCoordinator coordinator = new();

            RecoveryDiscoveryResult result = coordinator.Discover(RecoveryTestFixtures.LocalNotesScope, scopeDirectory);

            result.BlockingEvidence.Should().BeNull(result.BlockingEvidence?.Detail);
            result.Candidates.Select(candidate => candidate.Operation).Should()
                .ContainInOrder(RecoveryOperation.Restore, RecoveryOperation.Append, RecoveryOperation.FileEntity);
            result.Candidates.Select(candidate => candidate.EvidencePath).Should().ContainInOrder(
                RecoveryTestFixtures.RestoreJournalPath(scopeDirectory),
                RecoveryTestFixtures.AppendJournalPath(scopeDirectory),
                RecoveryTestFixtures.FileEntityRecoveryPath(entityDirectory));

            RecoveryOutcome admission = coordinator.AdmitNext(
                RecoveryTestFixtures.LocalNotesScope, scopeDirectory, result, CancellationToken.None);
            admission.IsBlocked.Should().BeFalse();
            admission.Warning.Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_MalformedEvidenceBlocksOnlyTheSelectedScope()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string notes = Path.Combine(root, "notes");
        string other = Path.Combine(root, "other");
        Directory.CreateDirectory(notes);
        Directory.CreateDirectory(other);
        try
        {
            File.WriteAllText(RecoveryTestFixtures.RestoreJournalPath(notes), "{");

            RecoveryDiscoveryCoordinator coordinator = new();

            RecoveryDiscoveryResult blocked = coordinator.Discover(RecoveryTestFixtures.LocalNotesScope, notes);
            RecoveryDiscoveryResult unaffected = coordinator.Discover(RecoveryTestFixtures.LocalOtherScope, other);

            blocked.BlockingEvidence.Should().NotBeNull();
            blocked.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.Malformed);
            unaffected.BlockingEvidence.Should().BeNull();
            unaffected.Candidates.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_OrdersCandidatesBySubjectAndOperationIdAfterOperationPrecedence()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RecoveryTestFixtures.WriteLegacyAppendJournal(root);

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator().Discover(
                RecoveryTestFixtures.LocalNotesScope, root);

            result.Candidates.Should().ContainSingle();
            result.Candidates[0].CanonicalSubject.Should().Be(RecoveryTestFixtures.CanonicalAddress);
            result.Candidates[0].IsLegacy.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_NewVersionAndWrongScopeEvidenceRemainTypedAndScopeLocal()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string notes = Path.Combine(root, "notes");
        Directory.CreateDirectory(notes);
        try
        {
            File.WriteAllText(RecoveryTestFixtures.RestoreJournalPath(notes),
                "{\"format\":\"scrinia.recovery\",\"version\":2}");
            RecoveryDiscoveryCoordinator coordinator = new();

            RecoveryDiscoveryResult newer = coordinator.Discover(RecoveryTestFixtures.LocalNotesScope, notes);
            newer.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.UnsupportedVersion);

            File.WriteAllBytes(RecoveryTestFixtures.RestoreJournalPath(notes),
                RecoveryTestFixtures.BuildLegacyRestoreJournal(RecoveryTestFixtures.LocalOtherScope));
            RecoveryDiscoveryResult wrongScope = coordinator.Discover(RecoveryTestFixtures.LocalNotesScope, notes);
            wrongScope.BlockingEvidence.Should().NotBeNull();
            wrongScope.BlockingEvidence!.Scope.Should().Be(RecoveryTestFixtures.LocalNotesScope);
            coordinator.Discover(RecoveryTestFixtures.LocalOtherScope, Path.Combine(root, "other"))
                .Candidates.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
