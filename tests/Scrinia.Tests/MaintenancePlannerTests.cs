using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class MaintenancePlannerTests
{
    private static MaintenanceHash256 Digest(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());

    private static MaintenancePlannerCandidate Candidate(
        ushort scopeOrdinal = 0,
        MaintenanceCandidateKind kind = MaintenanceCandidateKind.ArchivePair,
        string? keyGuid = null,
        byte digestFill = 1,
        ulong entryOrdinal = 0,
        bool isJournal = false,
        bool isUnsafe = false,
        MaintenanceProtectionReason? protection = null,
        bool adapterEligible = true,
        bool verifiedTerminal = true,
        bool subjectComplete = true,
        bool missing = false,
        bool explicitlyProtected = false) =>
        new(
            scopeOrdinal,
            kind,
            new MaintenanceCandidateKey(Guid.Parse(keyGuid ?? "00000000-0000-0000-0000-000000000001")),
            Digest(digestFill),
            entryOrdinal,
            isJournal,
            isUnsafe,
            protection,
            adapterEligible,
            verifiedTerminal,
            subjectComplete,
            missing,
            explicitlyProtected);

    // B5 design §5 — path-free total order: (scope ordinal, kind, candidate key, subject digest, entry ordinal).
    [Fact]
    public void Planner_orders_by_scope_kind_key_digest_entry()
    {
        var policy = new MaintenancePolicy(10, 30, 50, 5, true);
        int budget = MaintenancePlanner.EffectiveJournalBudget(policy, null);
        var keys = new[]
        {
            "00000000-0000-0000-0000-000000000003",
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002",
        };
        var shuffled = new[]
        {
            Candidate(scopeOrdinal: 1, keyGuid: keys[0], digestFill: 3, entryOrdinal: 0),
            Candidate(scopeOrdinal: 0, kind: MaintenanceCandidateKind.CompletedRecoveryEvidence, keyGuid: keys[1], digestFill: 1, entryOrdinal: 0, isJournal: true),
            Candidate(scopeOrdinal: 0, kind: MaintenanceCandidateKind.ArchivePair, keyGuid: keys[2], digestFill: 2, entryOrdinal: 0),
        };

        MaintenancePlannerPage page = MaintenancePlanner.Plan(shuffled, budget, cancellationToken: CancellationToken.None);

        page.Projections.Select(p => p.ScopeOrdinal).Should().Equal((ushort)0, (ushort)0, (ushort)1);
        page.Projections[0].Kind.Should().Be(MaintenanceCandidateKind.ArchivePair);
        page.Projections[1].Kind.Should().Be(MaintenanceCandidateKind.CompletedRecoveryEvidence);
        page.Projections.Select(p => p.CandidateKey.Value.ToString()).Should().ContainInOrder(
            "00000000-0000-0000-0000-000000000002",
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000003");
    }

    // B5 design §5 — non-journal candidates precede completedRecoveryEvidence.
    [Fact]
    public void Planner_places_non_journal_before_completed_recovery_evidence()
    {
        var journal = Candidate(kind: MaintenanceCandidateKind.CompletedRecoveryEvidence, keyGuid: "00000000-0000-0000-0000-000000000001", isJournal: true);
        var nonJournal = Candidate(kind: MaintenanceCandidateKind.ArchivePair, keyGuid: "00000000-0000-0000-0000-000000000002");
        MaintenancePlannerPage page = MaintenancePlanner.Plan([journal, nonJournal], 50, cancellationToken: CancellationToken.None);

        page.Projections[0].Kind.Should().Be(MaintenanceCandidateKind.ArchivePair);
        page.Projections[1].Kind.Should().Be(MaintenanceCandidateKind.CompletedRecoveryEvidence);
    }

    // B5 design §5 — exact class equations and bounds on every page.
    [Fact]
    public void Planner_asserts_exact_class_equations_and_bounds()
    {
        var candidates = new[]
        {
            Candidate(keyGuid: "00000000-0000-0000-0000-000000000001", digestFill: 1),
            Candidate(keyGuid: "00000000-0000-0000-0000-000000000002", digestFill: 2, isJournal: true),
            Candidate(keyGuid: "00000000-0000-0000-0000-000000000003", digestFill: 3, isUnsafe: true),
            Candidate(keyGuid: "00000000-0000-0000-0000-000000000004", digestFill: 4, isJournal: true, isUnsafe: true),
        };
        MaintenancePlannerPage page = MaintenancePlanner.Plan(candidates, 50, cancellationToken: CancellationToken.None);

        page.Scanned.Should().Be(page.JournalInspected + page.NonJournalInspected);
        (page.SafeJournalInspected + page.UnsafeJournalScanned + page.SafeNonJournalInspected + page.UnsafeNonJournalScanned).Should().Be(page.Scanned);
        (page.SafeJournalInspected + page.SafeNonJournalInspected).Should().Be(page.Scanned - page.UnsafeScanned);
        page.UnsafeScanned.Should().Be(page.UnsafeJournalScanned + page.UnsafeNonJournalScanned);
        page.JournalInspected.Should().Be(page.SafeJournalInspected + page.UnsafeJournalScanned);
        page.NonJournalInspected.Should().Be(page.SafeNonJournalInspected + page.UnsafeNonJournalScanned);
        page.Scanned.Should().BeLessThanOrEqualTo(128);
        page.JournalInspected.Should().BeLessThanOrEqualTo(page.JournalBudget);
        page.JournalBudget.Should().BeLessThanOrEqualTo(50);
        page.UnsafeScanned.Should().BeLessThanOrEqualTo(32);
        page.MetadataReads.Should().BeLessThanOrEqualTo(128);
        page.Returned.Should().BeLessThanOrEqualTo(64);
        page.Lookahead.Should().BeLessThanOrEqualTo(1);
        (page.Returned + page.Lookahead).Should().BeLessThanOrEqualTo(65);
        page.NonJournalInspected.Should().BeLessThanOrEqualTo(128 - page.JournalInspected);
        page.UnsafeScanned.Should().Be(2);
        page.UnsafeJournalScanned.Should().Be(1);
        page.UnsafeNonJournalScanned.Should().Be(1);
    }

    // B5 design §5 — effective journal budget is min(requested, JournalCleanupBatchSize), both 1..50.
    [Fact]
    public void Planner_uses_min_requested_and_configured_journal_budget()
    {
        var policy = new MaintenancePolicy(10, 30, 20, 5, true);

        MaintenancePlanner.EffectiveJournalBudget(policy, null).Should().Be(20);
        MaintenancePlanner.EffectiveJournalBudget(policy, 5).Should().Be(5);
        MaintenancePlanner.EffectiveJournalBudget(policy, 50).Should().Be(20);
        MaintenancePlanner.EffectiveJournalBudget(new MaintenancePolicy(10, 30, 50, 5, true), 1).Should().Be(1);
    }

    // B5 design §5 — greedy ordinal multi-scope allocation with no reset.
    [Fact]
    public void Planner_allocates_one_invocation_journal_budget_greedily_in_scope_order()
    {
        var scope0 = Enumerable.Range(1, 3).Select(i =>
            Candidate(scopeOrdinal: 0, kind: MaintenanceCandidateKind.CompletedRecoveryEvidence,
                keyGuid: $"00000000-0000-0000-0000-00000000000{i}", digestFill: (byte)i, isJournal: true)).ToArray();
        var scope1 = Enumerable.Range(4, 3).Select(i =>
            Candidate(scopeOrdinal: 1, kind: MaintenanceCandidateKind.CompletedRecoveryEvidence,
                keyGuid: $"00000000-0000-0000-0000-00000000000{i}", digestFill: (byte)i, isJournal: true)).ToArray();

        MaintenancePlannerPage page = MaintenancePlanner.Plan([.. scope0, .. scope1], 3, cancellationToken: CancellationToken.None);

        page.JournalBudget.Should().Be(3);
        page.JournalBudgetConsumed.Should().Be(3);
        page.JournalInspected.Should().Be(3);
        page.JournalCapReached.Should().BeTrue();
        page.Projections.Should().HaveCount(3);
        page.Projections.Select(p => p.ScopeOrdinal).Should().OnlyContain(o => o == 0);
    }

    // B5 design §5 — every unsafe consumes one shared admission; unsafe journals also consume journal budget.
    [Fact]
    public void Planner_charges_unsafe_journal_to_both_budgets_and_unsafe_non_journal_to_shared_only()
    {
        var candidates = new[]
        {
            Candidate(keyGuid: "00000000-0000-0000-0000-000000000001", digestFill: 1, isJournal: true, isUnsafe: true),
            Candidate(keyGuid: "00000000-0000-0000-0000-000000000002", digestFill: 2, isUnsafe: true),
            Candidate(keyGuid: "00000000-0000-0000-0000-000000000003", digestFill: 3),
        };
        MaintenancePlannerPage page = MaintenancePlanner.Plan(candidates, 50, cancellationToken: CancellationToken.None);

        page.Scanned.Should().Be(3);
        page.JournalInspected.Should().Be(1);
        page.NonJournalInspected.Should().Be(2);
        page.UnsafeJournalScanned.Should().Be(1);
        page.UnsafeNonJournalScanned.Should().Be(1);
        page.JournalBudgetConsumed.Should().Be(1);
    }

    // B5 design §5 — UnsafeScanLimit 32 is invocation-wide.
    [Fact]
    public void Planner_bounds_unsafe_scans_at_32()
    {
        var candidates = Enumerable.Range(1, 40).Select(i =>
            Candidate(keyGuid: $"00000000-0000-0000-0000-{i:D12}", digestFill: (byte)(i % 250 + 1), isUnsafe: true)).ToArray();
        MaintenancePlannerPage page = MaintenancePlanner.Plan(candidates, 50, cancellationToken: CancellationToken.None);

        page.UnsafeScanned.Should().BeLessThanOrEqualTo(32);
        page.Scanned.Should().BeLessThanOrEqualTo(128);
    }

    // B5 design §5 — at most one safe lookahead, counted but not returned; journal lookahead forbidden after cap.
    [Fact]
    public void Planner_returns_at_most_one_safe_lookahead_counted_not_returned()
    {
        var candidates = Enumerable.Range(1, 70).Select(i =>
            Candidate(keyGuid: $"00000000-0000-0000-0000-{i:D12}", digestFill: (byte)(i % 250 + 1))).ToArray();
        MaintenancePlannerPage page = MaintenancePlanner.Plan(candidates, 50, cancellationToken: CancellationToken.None);

        page.Returned.Should().Be(64);
        page.Lookahead.Should().Be(1);
        (page.Returned + page.Lookahead).Should().Be(65);
        page.Scanned.Should().Be(65);
        page.MetadataReads.Should().Be(65);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public void Planner_forbids_journal_lookahead_after_global_budget_reached()
    {
        // Two scope-0 journals sort before all scope-1 non-journals: the first
        // consumes the budget of 1, the second trips the cap, then 64 safe
        // non-journals fill Returned (64) plus exactly one non-journal lookahead.
        var journals = new[]
        {
            Candidate(scopeOrdinal: 0, kind: MaintenanceCandidateKind.CompletedRecoveryEvidence,
                keyGuid: "00000000-0000-0000-0000-000000000001", digestFill: 1, isJournal: true),
            Candidate(scopeOrdinal: 0, kind: MaintenanceCandidateKind.CompletedRecoveryEvidence,
                keyGuid: "00000000-0000-0000-0000-000000000002", digestFill: 2, isJournal: true),
        };
        var safeNonJournals = Enumerable.Range(100, 64).Select(i =>
            Candidate(scopeOrdinal: 1, kind: MaintenanceCandidateKind.ArchivePair,
                keyGuid: $"00000000-0000-0000-0000-{i:D12}", digestFill: (byte)(i % 200 + 1))).ToArray();
        MaintenancePlannerPage page = MaintenancePlanner.Plan([.. journals, .. safeNonJournals], 1, cancellationToken: CancellationToken.None);

        page.JournalCapReached.Should().BeTrue();
        page.JournalDeferred.Should().Be(0);
        page.JournalInspected.Should().Be(1);
        page.NonJournalInspected.Should().Be(64);
        page.Scanned.Should().Be(65);
        page.Returned.Should().Be(64);
        page.Lookahead.Should().Be(1);
        page.HasMore.Should().BeTrue();
        // The 65th scanned candidate (the lookahead) is a non-journal: 64
        // non-journals inspected against a single journal, and the admitted
        // tail is entirely ArchivePair following the single journal head.
        page.Projections[0].Kind.Should().Be(MaintenanceCandidateKind.CompletedRecoveryEvidence);
        page.Projections.Skip(1).Select(p => p.Kind).Should().OnlyContain(k => k == MaintenanceCandidateKind.ArchivePair);
    }

    // B5 design §5 — JournalBoundaryProof positive: each fixed location/name class validates.
    [Theory]
    [InlineData((byte)JournalBoundaryLocation.Restore, (byte)JournalBoundaryNameClass.RestoreJournal)]
    [InlineData((byte)JournalBoundaryLocation.AppendVersions, (byte)JournalBoundaryNameClass.AppendJournal)]
    [InlineData((byte)JournalBoundaryLocation.FileEntity, (byte)JournalBoundaryNameClass.FileEntityRecovery)]
    public void Planner_derives_deferred_only_from_valid_proof(byte locationValue, byte nameClassValue)
    {
        var location = (JournalBoundaryLocation)locationValue;
        var nameClass = (JournalBoundaryNameClass)nameClassValue;
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var root = new MaintenanceScopeBinding(Guid.NewGuid());
        var key = new MaintenanceCandidateKey(Guid.NewGuid());
        using var enumerator = JournalBoundaryEnumerator.Create(run, snapshot, root, 0, 0, null);
        var boundary = enumerator.BufferJournal(1, key, location, nameClass, JournalDirectoryEntryKind.File)!;
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(boundary, out JournalBoundaryProof? proof).Should().BeTrue();
        var context = new JournalBoundaryValidationContext(run, snapshot, root, enumerator.Id, 0, null, 0, key, JournalDirectoryEntryKind.File, 0);

        // Force a reached cap with budget 1 fully consumed by one journal.
        var journals = new[]
        {
            Candidate(kind: MaintenanceCandidateKind.CompletedRecoveryEvidence, keyGuid: "00000000-0000-0000-0000-000000000001", digestFill: 1, isJournal: true),
            Candidate(kind: MaintenanceCandidateKind.CompletedRecoveryEvidence, keyGuid: "00000000-0000-0000-0000-000000000002", digestFill: 2, isJournal: true),
        };
        MaintenancePlannerPage page = MaintenancePlanner.Plan(journals, 1, null, proof, context, CancellationToken.None);

        page.JournalCapReached.Should().BeTrue();
        page.JournalDeferred.Should().Be(1);
        page.HasMore.Should().BeTrue();
    }

    // B5 design §5 — JournalBoundaryProof negative: validator rejects every non-conforming input.
    [Fact]
    public void Planner_rejects_non_journal_unsafe_stale_and_post_inspection_proofs()
    {
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var root = new MaintenanceScopeBinding(Guid.NewGuid());
        var key = new MaintenanceCandidateKey(Guid.NewGuid());

        // Non-journal syntax cannot be captured.
        using (var enumerator = JournalBoundaryEnumerator.Create(run, snapshot, root, 0, 0, null))
        {
            var directoryBoundary = enumerator.BufferJournal(1, key, JournalBoundaryLocation.Restore, JournalBoundaryNameClass.RestoreJournal, JournalDirectoryEntryKind.Directory)!;
            JournalBoundaryProof.TryCaptureFromBufferedMetadata(directoryBoundary, out _).Should().BeFalse();
        }

        // Post-inspection proof fails validation.
        using (var enumerator = JournalBoundaryEnumerator.Create(run, snapshot, root, 0, 0, null))
        {
            var boundary = enumerator.BufferJournal(1, key, JournalBoundaryLocation.AppendVersions, JournalBoundaryNameClass.AppendJournal, JournalDirectoryEntryKind.File)!;
            JournalBoundaryProof.TryCaptureFromBufferedMetadata(boundary, out JournalBoundaryProof? proof).Should().BeTrue();
            var context = new JournalBoundaryValidationContext(run, snapshot, root, enumerator.Id, 0, null, 0, key, JournalDirectoryEntryKind.File, 0);
            enumerator.Advance();
            JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, context, out JournalBoundaryRejection rejection).Should().BeFalse();
            rejection.Should().Be(JournalBoundaryRejection.PostInspection);

            var journals = new[]
            {
                Candidate(kind: MaintenanceCandidateKind.CompletedRecoveryEvidence, keyGuid: "00000000-0000-0000-0000-000000000001", digestFill: 1, isJournal: true),
                Candidate(kind: MaintenanceCandidateKind.CompletedRecoveryEvidence, keyGuid: "00000000-0000-0000-0000-000000000002", digestFill: 2, isJournal: true),
            };
            MaintenancePlannerPage consumed = MaintenancePlanner.Plan(journals, 1, null, proof, context, CancellationToken.None);
            consumed.JournalDeferred.Should().Be(0);
        }

        // Wrong run and nonzero remaining budget are rejected.
        using (var enumerator = JournalBoundaryEnumerator.Create(run, snapshot, root, 0, 0, null))
        {
            var boundary = enumerator.BufferJournal(1, key, JournalBoundaryLocation.FileEntity, JournalBoundaryNameClass.FileEntityRecovery, JournalDirectoryEntryKind.File)!;
            JournalBoundaryProof.TryCaptureFromBufferedMetadata(boundary, out JournalBoundaryProof? proof).Should().BeTrue();
            var wrongRun = new JournalBoundaryValidationContext(new MaintenanceRunId(Guid.NewGuid()), snapshot, root, enumerator.Id, 0, null, 0, key, JournalDirectoryEntryKind.File, 0);
            JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, wrongRun, out JournalBoundaryRejection rejection).Should().BeFalse();
            rejection.Should().Be(JournalBoundaryRejection.WrongRun);

            var budgetRemaining = new JournalBoundaryValidationContext(run, snapshot, root, enumerator.Id, 0, null, 0, key, JournalDirectoryEntryKind.File, 1);
            JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, budgetRemaining, out rejection).Should().BeFalse();
            rejection.Should().Be(JournalBoundaryRejection.JournalBudgetRemaining);
        }
    }

    [Fact]
    public void Planner_deferred_content_failures_cannot_produce_a_proof()
    {
        // A proof minted from buffered metadata then consumed cannot validate again.
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var root = new MaintenanceScopeBinding(Guid.NewGuid());
        var key = new MaintenanceCandidateKey(Guid.NewGuid());
        using var enumerator = JournalBoundaryEnumerator.Create(run, snapshot, root, 0, 0, null);
        var boundary = enumerator.BufferJournal(1, key, JournalBoundaryLocation.Restore, JournalBoundaryNameClass.RestoreJournal, JournalDirectoryEntryKind.File)!;
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(boundary, out JournalBoundaryProof? proof).Should().BeTrue();
        boundary.Consume();
        var context = new JournalBoundaryValidationContext(run, snapshot, root, enumerator.Id, 0, null, 0, key, JournalDirectoryEntryKind.File, 0);
        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, context, out _).Should().BeFalse();
    }

    // Protection and retention: every non-eligible classification is retained, never deleted by the planner.
    [Theory]
    [InlineData("Current")]
    [InlineData("Active")]
    [InlineData("Referenced")]
    [InlineData("UnresolvedRecovery")]
    [InlineData("Malformed")]
    [InlineData("Unsafe")]
    [InlineData("WithinRetentionLimit")]
    public void Planner_retains_protected_candidates_without_eligibility(string reasonName)
    {
        var reason = Enum.Parse<MaintenanceProtectionReason>(reasonName);
        var candidate = Candidate(protection: reason, adapterEligible: false);
        MaintenancePlannerPage page = MaintenancePlanner.Plan([candidate], 50, cancellationToken: CancellationToken.None);

        page.Projections.Should().ContainSingle();
        page.Projections[0].Eligible.Should().BeFalse();
        page.Projections[0].Protection.Should().Be(reason);
    }

    [Fact]
    public void Planner_requires_verified_terminal_for_recovery_evidence()
    {
        var unverified = Candidate(kind: MaintenanceCandidateKind.CompletedRecoveryEvidence, isJournal: true, verifiedTerminal: false);
        MaintenancePlannerPage page = MaintenancePlanner.Plan([unverified], 50, cancellationToken: CancellationToken.None);

        page.Projections.Single().Eligible.Should().BeFalse();
    }

    // Revalidation rule: missing/deleted candidates require locked revalidation and stay protected.
    [Fact]
    public void Planner_marks_missing_candidates_for_locked_revalidation()
    {
        var missing = Candidate(missing: true);
        MaintenancePlannerPage page = MaintenancePlanner.Plan([missing], 50, cancellationToken: CancellationToken.None);

        page.Projections.Single().Eligible.Should().BeFalse();
        page.Projections.Single().RequiresLockedRevalidation.Should().BeTrue();
    }

    // Per-subject completeness before quota decisions.
    [Fact]
    public void Planner_retains_subject_incomplete_candidates_before_quota()
    {
        var incomplete = Candidate(subjectComplete: false);
        MaintenancePlannerPage page = MaintenancePlanner.Plan([incomplete], 50, cancellationToken: CancellationToken.None);

        page.Projections.Single().Eligible.Should().BeFalse();
        page.Projections.Single().Protection.Should().Be(MaintenanceProtectionReason.IncompletePair);
    }

    // Deadline and cancellation: no new candidate starts and no advanced state is returned.
    [Fact]
    public void Planner_observes_cancellation_before_consuming_candidates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Action plan = () => MaintenancePlanner.Plan([Candidate()], 50, null, cancellationToken: cancellation.Token);

        plan.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Planner_observes_expired_deadline_before_first_candidate()
    {
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);

        Action plan = () => MaintenancePlanner.Plan([Candidate()], 50, past, cancellationToken: CancellationToken.None);

        plan.Should().Throw<TimeoutException>();
    }

    // Privacy: no paths or content in planner results.
    [Fact]
    public void Planner_results_carry_no_paths_or_content()
    {
        MaintenancePlannerPage page = MaintenancePlanner.Plan([Candidate(), Candidate(digestFill: 2)], 50, cancellationToken: CancellationToken.None);

        foreach (var projection in page.Projections)
            projection.ToString().Should().NotContain("/").And.NotContain("\\");
        typeof(MaintenancePlannerProjection).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(name => name.Contains("Path"));
        typeof(MaintenancePlannerPage).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(name => name.Contains("Path"));
    }

    // Guard: planner performs no deletion, locking, cursor-key, policy-load, CLI, or scheduler work,
    // but consumes the approved contracts.
    [Fact]
    public void Planner_source_has_no_deletion_lock_cursor_key_policy_cli_or_scheduler_work()
    {
        string path = FindPlannerSource();
        string source = File.ReadAllText(path);

        source.Should().NotContain("File.Delete");
        source.Should().NotContain("WithExclusiveScopeAdmission");
        source.Should().NotContain("ComputeCandidateKey");
        source.Should().NotContain("ComputeWorkspaceKey");
        source.Should().NotContain("CursorCodec.Encode");
        source.Should().NotContain("MaintenancePolicy.Load");
        source.Should().NotContain("MaintenanceScheduler");
        source.Should().NotContain("ScriniaCommands");

        source.Should().Contain("MaintenancePolicy");
        source.Should().Contain("IRecoveryEvidenceVerifier");
        source.Should().Contain("B2RecoveryMaintenanceProjection");
        source.Should().Contain("B3CoordinatorMaintenanceProjection");
        source.Should().Contain("B4ParticipantMaintenanceProjection");
        source.Should().Contain("PathContainment");
        source.Should().Contain("ValidateJournalBoundaryProof");
    }

    [Fact]
    public void Planner_rejects_invalid_budgets_fail_fast()
    {
        Action zero = () => MaintenancePlanner.Plan([Candidate()], 0, cancellationToken: CancellationToken.None);
        Action over = () => MaintenancePlanner.Plan([Candidate()], 51, cancellationToken: CancellationToken.None);
        Action nullCandidates = () => MaintenancePlanner.Plan(null!, 10, cancellationToken: CancellationToken.None);

        zero.Should().Throw<ArgumentException>();
        over.Should().Throw<ArgumentException>();
        nullCandidates.Should().Throw<ArgumentNullException>();
    }

    private static string FindPlannerSource()
    {
        string directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "src", "Scrinia.Core", "MaintenancePlanner.cs");
            if (File.Exists(candidate))
                return candidate;
            string parent = Path.GetDirectoryName(directory)!;
            if (parent == directory)
                break;
            directory = parent;
        }
        // Fallback for the known workspace layout.
        const string fallback = @"C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia\src\Scrinia.Core\MaintenancePlanner.cs";
        if (File.Exists(fallback))
            return fallback;
        throw new FileNotFoundException("MaintenancePlanner.cs was not found.");
    }
}
