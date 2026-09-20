using Scrinia.Core.Configuration;

namespace Scrinia.Core;

// B5 design §5 — bounded candidate planning. Pure planning only: no deletion,
// no lock admission, no cursor signing, no policy loading, no CLI, no scheduler.
// Consumes MaintenanceContracts, MaintenancePolicy, IRecoveryEvidenceVerifier
// results, B2/B3/B4 projections, and PathContainment without modifying them.
internal sealed record MaintenancePlannerCandidate(
    ushort ScopeOrdinal,
    MaintenanceCandidateKind Kind,
    MaintenanceCandidateKey CandidateKey,
    MaintenanceHash256 SubjectDigest,
    ulong EntryOrdinal,
    bool IsJournal,
    bool IsUnsafe,
    MaintenanceProtectionReason? Protection,
    bool IsAdapterEligible,
    bool IsVerifiedTerminal,
    bool IsSubjectComplete,
    bool IsMissingOrDeleted,
    bool IsExplicitlyProtected);

internal sealed record MaintenancePlannerProjection(
    MaintenanceCandidateKey CandidateKey,
    ushort ScopeOrdinal,
    MaintenanceCandidateKind Kind,
    MaintenanceHash256 SubjectDigest,
    ulong EntryOrdinal,
    bool Eligible,
    MaintenanceProtectionReason? Protection,
    bool RequiresLockedRevalidation);

internal sealed class MaintenancePlannerPage
{
    internal MaintenancePlannerPage(
        IReadOnlyList<MaintenancePlannerProjection> projections,
        int scanned,
        int projected,
        int metadataReads,
        int returned,
        int lookahead,
        bool hasMore,
        int unsafeScanned,
        int unsafeJournalScanned,
        int unsafeNonJournalScanned,
        int safeJournalInspected,
        int safeNonJournalInspected,
        int journalBudget,
        int journalBudgetConsumed,
        int journalInspected,
        int nonJournalInspected,
        bool journalCapReached,
        int journalDeferred)
    {
        ArgumentNullException.ThrowIfNull(projections);
        Projections = projections;
        Scanned = scanned;
        Projected = projected;
        MetadataReads = metadataReads;
        Returned = returned;
        Lookahead = lookahead;
        HasMore = hasMore;
        UnsafeScanned = unsafeScanned;
        UnsafeJournalScanned = unsafeJournalScanned;
        UnsafeNonJournalScanned = unsafeNonJournalScanned;
        SafeJournalInspected = safeJournalInspected;
        SafeNonJournalInspected = safeNonJournalInspected;
        JournalBudget = journalBudget;
        JournalBudgetConsumed = journalBudgetConsumed;
        JournalInspected = journalInspected;
        NonJournalInspected = nonJournalInspected;
        JournalCapReached = journalCapReached;
        JournalDeferred = journalDeferred;

        if (scanned != journalInspected + nonJournalInspected)
            throw new ArgumentException("Admission counters must satisfy Scanned = JournalInspected + NonJournalInspected.");
        if (scanned > MaintenanceLimits.CandidatePageSize)
            throw new ArgumentException("Scanned exceeds the shared admission bound.");
        if (journalInspected > journalBudget || journalBudget > MaintenanceLimits.JournalBudgetMaximum)
            throw new ArgumentException("Journal counters exceed their budget.");
        if (unsafeScanned > MaintenanceLimits.UnsafeScanLimit)
            throw new ArgumentException("Unsafe counters exceed their bound.");
        if (metadataReads > MaintenanceLimits.MetadataReadLimit)
            throw new ArgumentException("Metadata reads exceed their bound.");
        if (returned > MaintenanceLimits.ReturnedProjectionLimit)
            throw new ArgumentException("Returned exceeds its bound.");
        if (lookahead is < 0 or > MaintenanceLimits.LookaheadSize)
            throw new ArgumentException("Lookahead exceeds its bound.");
        if (returned + lookahead > MaintenanceLimits.ReturnedProjectionLimit + MaintenanceLimits.LookaheadSize)
            throw new ArgumentException("Returned plus lookahead exceeds its bound.");
        if (nonJournalInspected > MaintenanceLimits.CandidatePageSize - journalInspected)
            throw new ArgumentException("Non-journal capacity exceeded.");
        if (journalDeferred is < 0 or > 1)
            throw new ArgumentException("Deferred must be saturated 0 or 1.");
    }

    internal IReadOnlyList<MaintenancePlannerProjection> Projections { get; }
    internal int Scanned { get; }
    internal int Projected { get; }
    internal int MetadataReads { get; }
    internal int Returned { get; }
    internal int Lookahead { get; }
    internal bool HasMore { get; }
    internal int UnsafeScanned { get; }
    internal int UnsafeJournalScanned { get; }
    internal int UnsafeNonJournalScanned { get; }
    internal int SafeJournalInspected { get; }
    internal int SafeNonJournalInspected { get; }
    internal int JournalBudget { get; }
    internal int JournalBudgetConsumed { get; }
    internal int JournalInspected { get; }
    internal int NonJournalInspected { get; }
    internal bool JournalCapReached { get; }
    internal int JournalDeferred { get; }
}

internal static class MaintenancePlanner
{
    internal static int EffectiveJournalBudget(MaintenancePolicy policy, int? requestedJournalBatchSize)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.JournalCleanupBatchSize is < 1 or > MaintenanceLimits.JournalBudgetMaximum)
            throw new ArgumentException("Configured journal budget is outside 1..50.", nameof(policy));
        if (requestedJournalBatchSize is null)
            return policy.JournalCleanupBatchSize;
        if (requestedJournalBatchSize.Value is < 1 or > MaintenanceLimits.JournalBudgetMaximum)
            throw new ArgumentException("Requested journal budget is outside 1..50.", nameof(requestedJournalBatchSize));
        return Math.Min(requestedJournalBatchSize.Value, policy.JournalCleanupBatchSize);
    }

    internal static int CompareCandidates(MaintenancePlannerCandidate left, MaintenancePlannerCandidate right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int scope = left.ScopeOrdinal.CompareTo(right.ScopeOrdinal);
        if (scope != 0)
            return scope;
        int kind = ((int)left.Kind).CompareTo((int)right.Kind);
        if (kind != 0)
            return kind;
        int key = CompareGuids(left.CandidateKey.Value, right.CandidateKey.Value);
        if (key != 0)
            return key;
        int digest = left.SubjectDigest.Bytes.Span.SequenceCompareTo(right.SubjectDigest.Bytes.Span);
        if (digest != 0)
            return digest;
        return left.EntryOrdinal.CompareTo(right.EntryOrdinal);
    }

    internal static MaintenancePlannerPage Plan(
        IReadOnlyList<MaintenancePlannerCandidate> candidates,
        int journalBudget,
        DateTimeOffset? deadlineUtc = null,
        JournalBoundaryProof? deferredProof = null,
        JournalBoundaryValidationContext? deferredContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (journalBudget is < 1 or > MaintenanceLimits.JournalBudgetMaximum)
            throw new ArgumentException("Journal budget must be within 1..50.", nameof(journalBudget));
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPastDeadline(deadlineUtc);

        MaintenancePlannerCandidate[] ordered = candidates.OrderBy(static candidate => candidate, Comparer<MaintenancePlannerCandidate>.Create(CompareCandidates)).ToArray();

        var returned = new List<MaintenancePlannerProjection>(MaintenanceLimits.ReturnedProjectionLimit);
        MaintenancePlannerProjection? lookahead = null;
        int scanned = 0;
        int metadataReads = 0;
        int journalInspected = 0;
        int nonJournalInspected = 0;
        int unsafeJournal = 0;
        int unsafeNonJournal = 0;
        bool capReached = false;

        foreach (MaintenancePlannerCandidate candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfPastDeadline(deadlineUtc);
            if (scanned >= MaintenanceLimits.CandidatePageSize)
                break;
            bool isJournal = candidate.IsJournal;
            bool isUnsafe = candidate.IsUnsafe;
            if (isJournal && journalInspected >= journalBudget)
            {
                capReached = true;
                continue;
            }
            if (isUnsafe && unsafeJournal + unsafeNonJournal >= MaintenanceLimits.UnsafeScanLimit)
                continue;
            if (returned.Count >= MaintenanceLimits.ReturnedProjectionLimit)
            {
                if (lookahead is not null)
                    break;
                if (isUnsafe)
                    continue;
                scanned++;
                metadataReads++;
                if (isJournal)
                    journalInspected++;
                else
                    nonJournalInspected++;
                lookahead = ToProjection(candidate);
                break;
            }
            scanned++;
            metadataReads++;
            if (isJournal)
                journalInspected++;
            else
                nonJournalInspected++;
            if (isUnsafe)
            {
                if (isJournal)
                    unsafeJournal++;
                else
                    unsafeNonJournal++;
                continue;
            }
            returned.Add(ToProjection(candidate));
        }

        int deferred = 0;
        if (capReached && deferredProof is not null && deferredContext.HasValue &&
            JournalBoundaryProof.ValidateJournalBoundaryProof(deferredProof, deferredContext.Value, out _))
            deferred = 1;

        bool hasMore = lookahead is not null || (capReached && deferred == 1);

        int unsafeScanned = unsafeJournal + unsafeNonJournal;
        int safeJournal = journalInspected - unsafeJournal;
        int safeNonJournal = nonJournalInspected - unsafeNonJournal;
        int lookaheadCount = lookahead is null ? 0 : 1;
        var projections = returned.AsReadOnly();
        return new MaintenancePlannerPage(
            projections,
            scanned,
            returned.Count + lookaheadCount,
            metadataReads,
            returned.Count,
            lookaheadCount,
            hasMore,
            unsafeScanned,
            unsafeJournal,
            unsafeNonJournal,
            safeJournal,
            safeNonJournal,
            journalBudget,
            journalInspected,
            journalInspected,
            nonJournalInspected,
            capReached,
            deferred);
    }

    internal static MaintenancePlannerPage Plan(
        IReadOnlyList<MaintenancePlannerCandidate> candidates,
        MaintenancePolicy policy,
        int? requestedJournalBatchSize,
        DateTimeOffset? deadlineUtc = null,
        JournalBoundaryProof? deferredProof = null,
        JournalBoundaryValidationContext? deferredContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Plan(candidates, EffectiveJournalBudget(policy, requestedJournalBatchSize), deadlineUtc, deferredProof, deferredContext, cancellationToken);
    }

    internal static bool IsContained(string scopeRoot, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(scopeRoot) || string.IsNullOrWhiteSpace(candidatePath))
            return false;
        return PathContainment.IsWithin(scopeRoot, candidatePath);
    }

    internal static MaintenancePlannerCandidate FromB2(
        B2RecoveryMaintenanceProjection view,
        ushort scopeOrdinal,
        MaintenanceCandidateKey key,
        MaintenanceHash256 subjectDigest,
        ulong entryOrdinal,
        bool isJournal,
        bool isUnsafe,
        bool subjectComplete,
        bool missing)
    {
        ArgumentNullException.ThrowIfNull(view);
        // Exhaustive by intent: Restore/Append/FileEntity are the terminal recovery
        // operations; any future RecoveryOperation falls through to ArchivePair
        // rather than silently claiming completed-recovery semantics.
        return new MaintenancePlannerCandidate(
            scopeOrdinal,
            view.Operation is RecoveryOperation.Restore or RecoveryOperation.Append or RecoveryOperation.FileEntity
                ? MaintenanceCandidateKind.CompletedRecoveryEvidence
                : MaintenanceCandidateKind.ArchivePair,
            key,
            subjectDigest,
            entryOrdinal,
            isJournal,
            isUnsafe || !view.IsPathSafe,
            view.Protection,
            view.IsTerminalAndVerified,
            view.IsTerminalAndVerified,
            subjectComplete,
            missing,
            false);
    }

    internal static MaintenancePlannerCandidate FromB3(
        B3CoordinatorMaintenanceProjection view,
        ushort scopeOrdinal,
        MaintenanceCandidateKey key,
        MaintenanceHash256 subjectDigest,
        ulong entryOrdinal,
        bool isJournal,
        bool isUnsafe,
        bool subjectComplete,
        bool missing)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new MaintenancePlannerCandidate(
            scopeOrdinal,
            MaintenanceCandidateKind.CompletedRecoveryEvidence,
            key,
            subjectDigest,
            entryOrdinal,
            isJournal,
            isUnsafe || !view.IsPathSafe,
            view.Protection,
            view.IsTerminalAndVerified,
            view.IsTerminalAndVerified,
            subjectComplete,
            missing,
            false);
    }

    internal static MaintenancePlannerCandidate FromB4(
        B4ParticipantMaintenanceProjection view,
        ushort scopeOrdinal,
        MaintenanceCandidateKey key,
        MaintenanceHash256 subjectDigest,
        ulong entryOrdinal,
        bool isJournal,
        bool isUnsafe,
        bool subjectComplete,
        bool missing)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new MaintenancePlannerCandidate(
            scopeOrdinal,
            MaintenanceCandidateKind.CompletedRecoveryEvidence,
            key,
            subjectDigest,
            entryOrdinal,
            isJournal,
            isUnsafe || !view.IsPathSafe,
            view.Protection,
            view.IsTerminalAndVerified,
            view.IsTerminalAndVerified,
            subjectComplete,
            missing,
            false);
    }

    /// <summary>
    /// Maps a recovery verification result to a planner protection reason.
    /// The <paramref name="verifier"/> is accepted-but-ignored: the planner maps
    /// already-produced verification results and never calls the verifier itself.
    /// The parameter exists so call sites can thread the active verifier through
    /// for auditing without changing planner signatures later.
    /// </summary>
    internal static MaintenanceProtectionReason? MapVerification(
        in RecoveryVerificationResult result,
        IRecoveryEvidenceVerifier? verifier)
    {
        _ = verifier;
        return result.Outcome switch
        {
            RecoveryVerificationOutcome.Verified => null,
            RecoveryVerificationOutcome.ProtectedUnsafe => MaintenanceProtectionReason.Unsafe,
            RecoveryVerificationOutcome.ProtectedMissing => MaintenanceProtectionReason.MissingExpectedMember,
            RecoveryVerificationOutcome.ProtectedInvalidLimits => MaintenanceProtectionReason.Unverifiable,
            RecoveryVerificationOutcome.ProtectedOversized => MaintenanceProtectionReason.Unverifiable,
            RecoveryVerificationOutcome.ProtectedBlocked => MaintenanceProtectionReason.Inaccessible,
            RecoveryVerificationOutcome.ProtectedHashMismatch => MaintenanceProtectionReason.Unverifiable,
            RecoveryVerificationOutcome.ProtectedIoFailure => MaintenanceProtectionReason.Inaccessible,
            _ => MaintenanceProtectionReason.UnresolvedRecovery,
        };
    }

    private static MaintenancePlannerProjection ToProjection(MaintenancePlannerCandidate candidate)
    {
        if (candidate.IsMissingOrDeleted)
            return new MaintenancePlannerProjection(candidate.CandidateKey, candidate.ScopeOrdinal, candidate.Kind,
                candidate.SubjectDigest, candidate.EntryOrdinal, false,
                candidate.Protection ?? MaintenanceProtectionReason.MissingExpectedMember, true);
        if (candidate.IsUnsafe)
            return new MaintenancePlannerProjection(candidate.CandidateKey, candidate.ScopeOrdinal, candidate.Kind,
                candidate.SubjectDigest, candidate.EntryOrdinal, false,
                candidate.Protection ?? MaintenanceProtectionReason.Unsafe, false);
        if (candidate.IsExplicitlyProtected)
            return new MaintenancePlannerProjection(candidate.CandidateKey, candidate.ScopeOrdinal, candidate.Kind,
                candidate.SubjectDigest, candidate.EntryOrdinal, false,
                candidate.Protection ?? MaintenanceProtectionReason.ExplicitlyProtected, false);
        if (!candidate.IsSubjectComplete)
            return new MaintenancePlannerProjection(candidate.CandidateKey, candidate.ScopeOrdinal, candidate.Kind,
                candidate.SubjectDigest, candidate.EntryOrdinal, false,
                candidate.Protection ?? MaintenanceProtectionReason.IncompletePair, false);
        if (candidate.Protection is not null || !candidate.IsAdapterEligible)
            return new MaintenancePlannerProjection(candidate.CandidateKey, candidate.ScopeOrdinal, candidate.Kind,
                candidate.SubjectDigest, candidate.EntryOrdinal, false,
                candidate.Protection ?? MaintenanceProtectionReason.UnresolvedRecovery, false);
        if (candidate.Kind == MaintenanceCandidateKind.CompletedRecoveryEvidence && !candidate.IsVerifiedTerminal)
            return new MaintenancePlannerProjection(candidate.CandidateKey, candidate.ScopeOrdinal, candidate.Kind,
                candidate.SubjectDigest, candidate.EntryOrdinal, false,
                MaintenanceProtectionReason.UnresolvedRecovery, false);
        return new MaintenancePlannerProjection(candidate.CandidateKey, candidate.ScopeOrdinal, candidate.Kind,
            candidate.SubjectDigest, candidate.EntryOrdinal, true, null, false);
    }

    private static void ThrowIfPastDeadline(DateTimeOffset? deadlineUtc)
    {
        if (deadlineUtc.HasValue && DateTimeOffset.UtcNow >= deadlineUtc.Value)
            throw new TimeoutException("The maintenance planning deadline has expired.");
    }

    private static int CompareGuids(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes, bigEndian: true, out _);
        right.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return leftBytes.SequenceCompareTo(rightBytes);
    }
}
