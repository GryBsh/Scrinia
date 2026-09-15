using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class MaintenanceRecoveryCandidateTests
{
    [Fact]
    public void B4MaintenanceProjectionRetainsQuarantineAndRefusesCleanup()
    {
        var state = new B4ParticipantRecoveryState(
            "transaction", "operation", "local", "/notes/item", "participant", 1, "participant-1", "kind",
            ["rollback"], new Dictionary<string, string>(), new Dictionary<string, string>(),
            TransactionalParticipantLifecycleState.Ambiguous, "handle", "v1", 0, 0, true, true,
            TransactionalFailureCode.UnknownTransient, "evidence", "journal", "v1",
            TransactionalQuarantineState.Quarantined, "recovery-mismatch");

        B4ParticipantMaintenanceAdapter adapter = new();
        B4ParticipantMaintenanceProjection projection = adapter.Project(state);
        RecoveryCleanupDecision decision = adapter.EvaluateCleanup(projection,
            new HashSet<string>(StringComparer.Ordinal));

        projection.IsTerminalAndVerified.Should().BeFalse();
        decision.Eligible.Should().BeFalse();
        decision.Protected.Should().BeTrue();
        decision.Protection.Should().Be(MaintenanceProtectionReason.UnresolvedRecovery);
    }

    [Fact]
    public void MaintenanceCursorRejectsUnsafeRelativePaths()
    {
        FluentActions.Invoking(() => new MaintenanceDiscoveryCursor("../foreign"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void B4MaintenanceCleanupMatchesTheActualEvidenceReference()
    {
        var state = new B4ParticipantRecoveryState(
            "transaction", "operation", "local", "/notes/item", "participant", 1, "participant-1", "kind",
            ["rollback"], new Dictionary<string, string>(), new Dictionary<string, string>(),
            TransactionalParticipantLifecycleState.RolledBack, "handle", "v1", 0, 0, false, false,
            null, "evidence/operation.recovery", "journal", "v1", TransactionalQuarantineState.None, null);

        B4ParticipantMaintenanceProjection projection = new B4ParticipantMaintenanceAdapter().Project(state);
        RecoveryCleanupDecision decision = new B4ParticipantMaintenanceAdapter().EvaluateCleanup(
            projection, new HashSet<string>(["evidence/operation.recovery"], StringComparer.Ordinal));

        decision.Protection.Should().Be(MaintenanceProtectionReason.Referenced);
        decision.Eligible.Should().BeFalse();
    }

    [Fact]
    public void B4MissingEvidenceReferenceCannotAuthorizeCleanup()
    {
        var state = new B4ParticipantRecoveryState(
            "transaction", "operation", "local", "/notes/item", "participant", 1, "participant-1", "kind",
            ["rollback"], new Dictionary<string, string>(), new Dictionary<string, string>(),
            TransactionalParticipantLifecycleState.RolledBack, "handle", "v1", 0, 0, false, false,
            null, null, null, null, TransactionalQuarantineState.None, null);

        B4ParticipantMaintenanceProjection projection = new B4ParticipantMaintenanceAdapter().Project(state);
        RecoveryCleanupDecision decision = new B4ParticipantMaintenanceAdapter().EvaluateCleanup(projection,
            new HashSet<string>(StringComparer.Ordinal));

        decision.Eligible.Should().BeFalse();
        decision.Protection.Should().Be(MaintenanceProtectionReason.MissingExpectedMember);
        decision.SafeRelativePath.Should().BeEmpty();
    }

    [Fact]
    public void MaintenanceDiscoveryStopsAtBudgetAndContinuesAfterTheReturnedPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-maintenance-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        int enumerated = 0;
        try
        {
            string[] paths = new[] { ".file-entity-c.recovery", ".file-entity-a.recovery", ".file-entity-b.recovery" }
                .Select(name => Path.Combine(skills, name)).ToArray();
            RecoveryDiscoveryCoordinator coordinator = new(
                new Func<string, IEnumerable<string>>(_ => new[] { skills }),
                new Func<string, IEnumerable<string>>(_ => paths.Select(path => { enumerated++; return path; })));

            RecoveryMaintenanceDiscoveryPage first = coordinator.DiscoverForMaintenance(
                "local", root, new MaintenanceDiscoveryCursor(), 1, TimeSpan.FromSeconds(5), CancellationToken.None);
            first.Recovery.Should().ContainSingle();
            first.HasMore.Should().BeTrue();
            enumerated.Should().Be(3);
            first.Recovery[0].EvidencePath.Should().EndWith(".file-entity-a.recovery");

            RecoveryMaintenanceDiscoveryPage second = coordinator.DiscoverForMaintenance(
                "local", root, first.NextCursor!, 1, TimeSpan.FromSeconds(5), CancellationToken.None);
            second.Recovery.Should().ContainSingle();
            second.Recovery[0].EvidencePath.Should().EndWith(".file-entity-b.recovery");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeadlineAfterEnumerationProgressAdvancesTheContinuationCursor()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-maintenance-deadline-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        try
        {
            string first = Path.Combine(skills, ".file-entity-a.recovery");
            string second = Path.Combine(skills, ".file-entity-b.recovery");
            RecoveryDiscoveryCoordinator coordinator = new(
                new Func<string, IEnumerable<string>>(_ => new[] { skills }),
                new Func<string, IEnumerable<string>>(_ => SlowEvidence(first, second)));

            RecoveryMaintenanceDiscoveryPage page = coordinator.DiscoverForMaintenance(
                "local", root, new MaintenanceDiscoveryCursor(), 10, TimeSpan.FromMilliseconds(100), CancellationToken.None);

            page.HasMore.Should().BeTrue();
            page.NextCursor.Should().NotBeNull();
            page.NextCursor!.AfterRelativePath.Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static IEnumerable<string> SlowEvidence(string first, string second)
        {
            yield return first;
            Thread.Sleep(250);
            yield return second;
        }
    }

    [Fact]
    public void UnsafeDeadlineCandidateCannotAdvanceTheContinuationCursor()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-maintenance-unsafe-cursor-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        string foreign = Path.Combine(Path.GetTempPath(), "foreign.recovery");
        try
        {
            RecoveryDiscoveryCoordinator coordinator = new(
                new Func<string, IEnumerable<string>>(_ => new[] { skills }),
                new Func<string, IEnumerable<string>>(_ => SlowForeignEvidence(foreign)));

            RecoveryMaintenanceDiscoveryPage page = coordinator.DiscoverForMaintenance(
                "local", root, new MaintenanceDiscoveryCursor(), 10, TimeSpan.FromMilliseconds(100), CancellationToken.None);

            page.HasMore.Should().BeTrue();
            page.NextCursor.Should().NotBeNull();
            page.NextCursor!.AfterRelativePath.Should().NotBe("recovery/unsafe");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static IEnumerable<string> SlowForeignEvidence(string path)
        {
            yield return path;
            Thread.Sleep(250);
        }
    }

    [Fact]
    public void UnsafeCandidateUsesSafeContinuationAndDoesNotHideLaterEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-maintenance-unsafe-followed-by-valid-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        string foreign = Path.Combine(Path.GetTempPath(), "foreign.recovery");
        try
        {
            string valid = Path.Combine(skills, ".file-entity-valid.recovery");
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(skills, operationId: "valid");
            RecoveryDiscoveryCoordinator coordinator = new(
                new Func<string, IEnumerable<string>>(_ => new[] { skills }),
                new Func<string, IEnumerable<string>>(_ => new[] { foreign, valid }));

            RecoveryMaintenanceDiscoveryPage first = coordinator.DiscoverForMaintenance(
                "local", root, new MaintenanceDiscoveryCursor(), 1, TimeSpan.FromSeconds(5), CancellationToken.None);

            first.Recovery.Should().ContainSingle();
            first.Recovery[0].IsPathSafe.Should().BeFalse();
            first.Recovery[0].EvidencePath.Should().Be("recovery/unsafe");
            first.HasMore.Should().BeTrue();
            first.NextCursor.Should().NotBeNull();
            first.NextCursor!.AfterRelativePath.Should().NotBe("recovery/unsafe");

            RecoveryMaintenanceDiscoveryPage second = coordinator.DiscoverForMaintenance(
                "local", root, first.NextCursor, 1, TimeSpan.FromSeconds(5), CancellationToken.None);

            second.Recovery.Should().ContainSingle();
            second.Recovery[0].EvidencePath.Should().EndWith("skills/.file-entity-valid.recovery");
            second.Recovery[0].IsPathSafe.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManyUnsafeCandidatesBeforeValidEvidenceConvergeWithBudgetOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-maintenance-many-unsafe-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        string[] foreign = Enumerable.Range(0, 5)
            .Select(index => Path.Combine(Path.GetTempPath(), $".file-entity-unsafe-{index}.recovery"))
            .ToArray();
        try
        {
            string valid = Path.Combine(skills, ".file-entity-valid.recovery");
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(skills, operationId: "valid");
            RecoveryDiscoveryCoordinator coordinator = new(
                new Func<string, IEnumerable<string>>(_ => new[] { skills }),
                new Func<string, IEnumerable<string>>(_ => foreign.Append(valid)));

            var pages = new List<RecoveryMaintenanceDiscoveryPage>();
            MaintenanceDiscoveryCursor cursor = new();
            for (int pageNumber = 0; pageNumber < foreign.Length + 1; pageNumber++)
            {
                RecoveryMaintenanceDiscoveryPage page = coordinator.DiscoverForMaintenance(
                    "local", root, cursor, 1, TimeSpan.FromSeconds(5), CancellationToken.None);
                pages.Add(page);
                cursor = page.NextCursor!;
            }

            pages.SelectMany(page => page.Recovery).Count(view => !view.IsPathSafe).Should().Be(foreign.Length);
            pages[^1].Recovery.Should().ContainSingle(view => view.IsPathSafe);
            pages[^1].Recovery[0].EvidencePath.Should().EndWith("skills/.file-entity-valid.recovery");
            pages.SelectMany(page => page.Recovery).Where(view => view.IsPathSafe)
                .Select(view => view.EvidencePath).Should().OnlyHaveUniqueItems();
            pages.SelectMany(page => page.Recovery).Select(view => view.OperationId)
                .Should().OnlyHaveUniqueItems();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeadlineBeforeProjectionDoesNotSkipTheUnprojectedCandidate()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-maintenance-deadline-unprojected-" + Guid.NewGuid().ToString("N"));
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        try
        {
            string first = Path.Combine(skills, ".file-entity-a.recovery");
            string second = Path.Combine(skills, ".file-entity-b.recovery");
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(skills, operationId: "a");
            File.Copy(first, second);
            RecoveryDiscoveryCoordinator coordinator = new(
                new Func<string, IEnumerable<string>>(_ => new[] { skills }),
                new Func<string, IEnumerable<string>>(_ => SlowBeforeSecond(first, second)));

            RecoveryMaintenanceDiscoveryPage firstPage = coordinator.DiscoverForMaintenance(
                "local", root, new MaintenanceDiscoveryCursor(), 10, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            firstPage.Recovery.Should().BeEmpty();
            firstPage.NextCursor.Should().NotBeNull();
            firstPage.NextCursor!.AfterRelativePath.Should().BeNull();

            RecoveryMaintenanceDiscoveryPage secondPage = coordinator.DiscoverForMaintenance(
                "local", root, firstPage.NextCursor, 10, TimeSpan.FromSeconds(5), CancellationToken.None);
            secondPage.Recovery.Select(view => view.EvidencePath).Should().ContainInOrder(
                "skills/.file-entity-a.recovery", "skills/.file-entity-b.recovery");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static IEnumerable<string> SlowBeforeSecond(string first, string second)
        {
            yield return first;
            Thread.Sleep(250);
            yield return second;
        }
    }

    [Theory]
    [InlineData("Malformed", "Malformed")]
    [InlineData("UnsupportedVersion", "UnsupportedVersion")]
    [InlineData("Ambiguous", "Ambiguous")]
    [InlineData("Inconsistent", "Unverifiable")]
    [InlineData("WrongScope", "Unsafe")]
    [InlineData("Unsafe", "Unsafe")]
    [InlineData("ReplayIoFailed", "Inaccessible")]
    public void BlockedRecoveryEvidenceKeepsItsTypedProtectionReason(
        string reasonName, string expectedName)
    {
        string path = Path.Combine(Path.GetTempPath(), "scrinia-maintenance-evidence");
        RecoveryBlockReason reason = Enum.Parse<RecoveryBlockReason>(reasonName);
        MaintenanceProtectionReason expected = Enum.Parse<MaintenanceProtectionReason>(expectedName);
        RecoveryBlock block = new("local", path, RecoveryOperation.Restore, "operation", reason, "hidden detail");
        B2RecoveryMaintenanceProjection projection = new B2RecoveryMaintenanceAdapter()
            .Project(new RecoveryDiscoveryResult([], block), "local").Single();

        RecoveryCleanupDecision decision = new B2RecoveryMaintenanceAdapter().EvaluateCleanup(
            projection, new HashSet<string>(StringComparer.Ordinal));
        decision.Protection.Should().Be(expected);
        decision.Eligible.Should().BeFalse();
    }

    [Fact]
    public void AppendTerminalProofIsRevokedWhenLiveBytesChange()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-maintenance-proof-" + Guid.NewGuid().ToString("N"));
        string versions = Path.Combine(root, "versions");
        Directory.CreateDirectory(versions);
        try
        {
            RecoveryTestFixtures.WriteLegacyAppendJournal(root, "committed");
            File.WriteAllBytes(Path.Combine(root, "notes.nmp2"), RecoveryTestFixtures.NewArtifact);
            File.WriteAllBytes(Path.Combine(root, "notes.meta.json"), RecoveryTestFixtures.NewSidecar);

            RecoveryDiscoveryResult discovery = new RecoveryDiscoveryCoordinator().Discover("local:notes", root, false);
            B2RecoveryMaintenanceProjection before = new B2RecoveryMaintenanceAdapter(root).Project(discovery, "local:notes").Single();
            before.IsTerminalAndVerified.Should().BeTrue();

            File.WriteAllBytes(Path.Combine(root, "notes.nmp2"), [0xFF]);
            RecoveryDiscoveryResult changed = new RecoveryDiscoveryCoordinator().Discover("local:notes", root, false);
            B2RecoveryMaintenanceProjection after = new B2RecoveryMaintenanceAdapter(root).Project(changed, "local:notes").Single();
            RecoveryCleanupDecision decision = new B2RecoveryMaintenanceAdapter().EvaluateCleanup(after,
                new HashSet<string>(StringComparer.Ordinal));

            after.IsTerminalAndVerified.Should().BeFalse();
            decision.Protection.Should().Be(MaintenanceProtectionReason.UnresolvedRecovery);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
