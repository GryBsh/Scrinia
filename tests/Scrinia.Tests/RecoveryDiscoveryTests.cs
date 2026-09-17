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
        string entityDirectory = Path.Combine(scopeDirectory, "skills");
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

    [Fact]
    public void Discover_ExcludesReservedDirectoriesAndPreservesOrdinalOrdering()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        string versions = Path.Combine(root, "versions");
        Directory.CreateDirectory(skills);
        Directory.CreateDirectory(versions);
        try
        {
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(skills, operationId: "z-operation");
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(versions, operationId: "reserved-operation");

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator(
                _ => [versions, skills],
                directory => Directory.EnumerateFiles(directory, ".file-entity-*.recovery"))
                .Discover(RecoveryTestFixtures.LocalNotesScope, root);

            result.BlockingEvidence.Should().BeNull();
            result.Candidates.Should().ContainSingle();
            result.Candidates[0].OperationId.Should().Be("z-operation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_EnumerationFailureIsTypedAndDoesNotEscape()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator(
                _ => throw new IOException("injected enumeration failure"))
                .Discover(RecoveryTestFixtures.LocalNotesScope, root);

            result.BlockingEvidence.Should().NotBeNull();
            result.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.ReplayIoFailed);
            result.BlockingEvidence.OperationId.Should().Be("unknown");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_PreservesOperationIdForMalformedAndWrongScopeJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string appendPath = RecoveryTestFixtures.AppendJournalPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(appendPath)!);
            File.WriteAllBytes(appendPath, RecoveryTestFixtures.BuildLegacyAppendJournal(
                RecoveryTestFixtures.LocalOtherScope));

            RecoveryBlock block = new RecoveryDiscoveryCoordinator()
                .Discover(RecoveryTestFixtures.LocalNotesScope, root).BlockingEvidence!;

            block.Reason.Should().Be(RecoveryBlockReason.Malformed);
            block.OperationId.Should().Be(RecoveryTestFixtures.OperationId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_DuplicateSubjectClaimsBlockWithDeterministicIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        try
        {
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(skills, operationId: "operation-a");
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(skills, operationId: "operation-b");

            RecoveryBlock block = new RecoveryDiscoveryCoordinator()
                .Discover(RecoveryTestFixtures.LocalNotesScope, root).BlockingEvidence!;

            block.Reason.Should().Be(RecoveryBlockReason.Ambiguous);
            block.Operation.Should().Be(RecoveryOperation.FileEntity);
            block.OperationId.Should().Be("operation-a");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_FilenameOperationMismatchRetainsEnvelopeOperationId()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        try
        {
            File.WriteAllText(RecoveryTestFixtures.AppendJournalPath(root), """
                {"format":"scrinia.recovery","version":1,"operation":"restore","bodyVersion":1,"operationId":"mismatch-id","scope":"local:notes","address":null,"state":"prepared","createdAt":"2026-09-11T00:00:00Z","updatedAt":"2026-09-11T00:00:00Z","body":{}}
                """);

            RecoveryBlock block = new RecoveryDiscoveryCoordinator()
                .Discover(RecoveryTestFixtures.LocalNotesScope, root).BlockingEvidence!;

            block.Reason.Should().Be(RecoveryBlockReason.Inconsistent);
            block.OperationId.Should().Be("mismatch-id");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_RejectsTraversalInFileEntityMemberPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(agent, bodyPath: "../escape.md");

            RecoveryBlock block = new RecoveryDiscoveryCoordinator()
                .Discover(RecoveryTestFixtures.LocalNotesScope, root).BlockingEvidence!;

            block.Reason.Should().Be(RecoveryBlockReason.Unsafe);
            block.OperationId.Should().Be(RecoveryTestFixtures.OperationId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_AdmitsEvidenceInTheQualifiedAgentDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(agent);

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator()
                .Discover(RecoveryTestFixtures.LocalNotesScope, root);

            result.BlockingEvidence.Should().BeNull();
            result.Candidates.Should().ContainSingle(candidate => candidate.Operation == RecoveryOperation.FileEntity);
            result.Candidates[0].CanonicalSubject.Should().Be("agent/entity");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_stops_before_inspecting_more_than_the_journal_scan_ceiling()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        try
        {
            int inspected = 0;
            string validPath = RecoveryTestFixtures.WriteLegacyFileEntityRecovery(skills);
            IEnumerable<string> files = Enumerable.Repeat(validPath, MaintenanceLimits.JournalBudgetMaximum + 1);

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator(
                _ => [skills],
                _ => files.Select(path =>
                {
                    inspected++;
                    return path;
                }))
                .Discover(RecoveryTestFixtures.LocalNotesScope, root);

            result.BlockingEvidence.Should().NotBeNull();
            result.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.ReplayIoFailed);
            inspected.Should().Be(MaintenanceLimits.JournalBudgetMaximum + 1);
            result.Candidates.Should().HaveCount(MaintenanceLimits.JournalBudgetMaximum);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_propagates_cancellation_during_entity_enumeration()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        using var cancellation = new CancellationTokenSource();
        try
        {
            IEnumerable<string> Enumerate(string _)
            {
                cancellation.Cancel();
                yield return Path.Combine(skills, ".file-entity-cancelled.recovery");
            }

            Action discover = () => new RecoveryDiscoveryCoordinator(_ => [skills], Enumerate)
                .Discover(RecoveryTestFixtures.LocalNotesScope, root, cancellationToken: cancellation.Token);

            discover.Should().Throw<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
