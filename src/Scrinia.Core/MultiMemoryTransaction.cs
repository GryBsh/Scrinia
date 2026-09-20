using Scrinia.Core.Models;
using System.Collections.ObjectModel;
using static Scrinia.Core.RequireTextHelper;

namespace Scrinia.Core;

internal enum MultiMemoryTransactionState
{
    Prepared,
    Committed,
    RecoveryRequired
}

internal enum MultiMemoryOutcome
{
    Committed,
    CommittedCleanupPending,
    RolledBack,
    RecoveryRequired,
    Rejected
}

internal enum MultiMemoryFailure
{
    Admission,
    Prepare,
    DecisionPublication,
    ParticipantCommit,
    Verification,
    Rollback,
    Cleanup,
    Malformed,
    UnsupportedVersion,
    Unsafe,
    Inconsistent,
    Ambiguous,
    ReplayIoFailed,
    LockTimeout
}

internal enum MultiMemoryTransactionTransition
{
    PreparedPublished,
    CommittedPublished,
    CompletionProofPublished,
    RecordDeleted
}

internal enum MultiMemoryCommitStatus
{
    Unknown,
    Prepared,
    Committed
}

internal enum TransactionalParticipantLifecycleState { Prepared, Committed, RolledBack, Changed, Ambiguous }
internal enum TransactionalQuarantineState { None, Quarantined }
internal enum TransactionalFailureCode
{
    Veto, DeclaredTransient, Permanent, UnknownTransient, Cancelled, InvalidResponse,
    CommitFailed, VerificationFailed, RollbackFailed, EvidenceInvalid,
    RetryMonotonicClockFailed, RetryMonotonicClockMovedBackwards, RetryElapsedOverflow
}

internal interface IMultiMemoryParticipantAdapterFactory
{
    Task<IMultiMemoryParticipantAdapter> CreateAsync(MultiMemoryParticipantRequest request, CancellationToken cancellationToken);
    Task<IMultiMemoryParticipantAdapter> RehydrateAsync(B4ParticipantRehydrationRequest request, CancellationToken cancellationToken);
}

internal sealed class MultiMemoryParticipantMismatchException : Exception;

internal sealed record MultiMemoryParticipantRequest
{
    internal MultiMemoryParticipantRequest(MemoryAddress address, string operationId, string kind,
        string? transactionId = null, string? scope = null)
    {
        if (!address.IsValid)
            throw new ArgumentException("A valid memory address is required.", nameof(address));

        Address = address;
        OperationId = RequireText(operationId, nameof(operationId));
        Kind = RequireText(kind, nameof(kind));
        TransactionId = transactionId is null ? null : RequireText(transactionId, nameof(transactionId));
        Scope = scope is null ? null : RequireText(scope, nameof(scope));
    }

    internal MemoryAddress Address { get; }
    internal string OperationId { get; }
    internal string Kind { get; }
    internal string? TransactionId { get; }
    internal string? Scope { get; }
    internal string? ParticipantIdentity { get; private init; }
    internal int? ContractVersion { get; private init; }
    internal string? OrderingKey { get; private init; }
    internal IReadOnlyDictionary<string, string>? Payload { get; private init; }
    internal IReadOnlyDictionary<string, string>? Metadata { get; private init; }

    internal static MultiMemoryParticipantRequest ForTransactionalParticipant(
        string scope, MultiMemoryTransactionalParticipantRequest participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        return new MultiMemoryParticipantRequest(participant.Address, participant.OperationId, participant.Kind, null, scope)
        {
            ParticipantIdentity = participant.ParticipantIdentity,
            ContractVersion = participant.ContractVersion,
            OrderingKey = participant.OrderingKey,
            Payload = participant.Payload,
            Metadata = participant.Metadata
        };
    }

    internal static MultiMemoryParticipantRequest ForTransactionalParticipant(
        string transactionId, string scope, MultiMemoryTransactionalParticipantRequest participant) =>
        new MultiMemoryParticipantRequest(participant.Address, participant.OperationId, participant.Kind, transactionId, scope)
        {
            ParticipantIdentity = participant.ParticipantIdentity, ContractVersion = participant.ContractVersion,
            OrderingKey = participant.OrderingKey, Payload = participant.Payload, Metadata = participant.Metadata
        };

    internal void Deconstruct(out MemoryAddress address, out string operationId, out string kind) =>
        (address, operationId, kind) = (Address, OperationId, Kind);


}

internal sealed record MultiMemoryTransactionRequest
{
    internal MultiMemoryTransactionRequest(
        string scope,
        IReadOnlyList<MultiMemoryParticipantRequest> participants,
        IReadOnlyList<MultiMemoryTransactionalParticipantRequest>? transactionalParticipants = null)
    {
        Scope = RequireText(scope, nameof(scope));
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        if (participants.Any(participant => participant is null))
            throw new ArgumentException("Participants cannot contain null values.", nameof(participants));

        var copiedParticipants = participants.ToArray();
        if (copiedParticipants
            .GroupBy(participant => participant.Address.ToString(), StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new ArgumentException("Participant subjects must be unique.", nameof(participants));

        Participants = Array.AsReadOnly(copiedParticipants);
        transactionalParticipants ??= Array.Empty<MultiMemoryTransactionalParticipantRequest>();
        if (transactionalParticipants.Any(participant => participant is null))
            throw new ArgumentException("Transactional participants cannot contain null values.", nameof(transactionalParticipants));
        if (copiedParticipants.Length + transactionalParticipants.Count > 4096)
            throw new ArgumentException("The combined participant count exceeds 4096.", nameof(transactionalParticipants));
        var copiedTransactional = transactionalParticipants.ToArray();
        if (copiedTransactional.Select(item => item.ParticipantIdentity).Distinct(StringComparer.Ordinal).Count() != copiedTransactional.Length)
            throw new ArgumentException("Transactional participant identities must be unique.", nameof(transactionalParticipants));
        if (copiedTransactional.Select(item => item.OrderingKey).Concat(Enumerable.Repeat<string?>(null, copiedParticipants.Length))
            .Where(key => key is not null).GroupBy(key => key!, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Participant ordering keys must be unique.", nameof(transactionalParticipants));
        TransactionalParticipants = Array.AsReadOnly(copiedTransactional);
    }

    internal string Scope { get; }
    internal IReadOnlyList<MultiMemoryParticipantRequest> Participants { get; }
    internal IReadOnlyList<MultiMemoryTransactionalParticipantRequest> TransactionalParticipants { get; }

    internal IReadOnlyList<MultiMemoryParticipantRequest> RequestedParticipants(string? transactionId = null) =>
        Participants.Concat(TransactionalParticipants.Select(participant =>
                transactionId is null
                    ? MultiMemoryParticipantRequest.ForTransactionalParticipant(Scope, participant)
                    : MultiMemoryParticipantRequest.ForTransactionalParticipant(transactionId, Scope, participant)))
            .ToArray();

    internal static MemoryAddress AddressForScope(string scope, string subject)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("A transaction scope is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("A participant subject is required.", nameof(subject));
        if (scope.Equals("local", StringComparison.Ordinal))
            return MemoryAddress.Parse($"/{subject}");
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return MemoryAddress.Parse($"/{scope["local-topic:".Length..]}/{subject}");
        if (scope.StartsWith("local:", StringComparison.Ordinal))
            return MemoryAddress.Parse($"/{scope["local:".Length..]}/{subject}");
        throw new ArgumentException($"Unknown transaction scope: {scope}", nameof(scope));
    }

    internal void Deconstruct(
        out string scope,
        out IReadOnlyList<MultiMemoryParticipantRequest> participants) =>
        (scope, participants) = (Scope, Participants);


}

internal sealed record MultiMemoryTransactionalParticipantRequest
{
    internal MultiMemoryTransactionalParticipantRequest(MemoryAddress address, string participantIdentity, int contractVersion,
        string orderingKey, string operationId, string kind, IReadOnlyDictionary<string, string> payload,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (!address.IsValid || address.Kind != MemoryAddressKind.Persistent) throw new ArgumentException("A persistent address is required.", nameof(address));
        Address = address;
        ParticipantIdentity = B4Validation.Text(participantIdentity, nameof(participantIdentity));
        if (contractVersion != 1) throw new ArgumentException("Contract version must be 1.", nameof(contractVersion));
        ContractVersion = contractVersion;
        OrderingKey = B4Validation.OrderingKey(orderingKey);
        OperationId = B4Validation.Text(operationId, nameof(operationId));
        Kind = B4Validation.Text(kind, nameof(kind));
        Payload = B4Validation.Dictionary(payload, 4096, nameof(payload));
        Metadata = B4Validation.Dictionary(metadata, 512, nameof(metadata));
    }
    internal MemoryAddress Address { get; }
    internal string ParticipantIdentity { get; }
    internal int ContractVersion { get; }
    internal string OrderingKey { get; }
    internal string OperationId { get; }
    internal string Kind { get; }
    internal IReadOnlyDictionary<string, string> Payload { get; }
    internal IReadOnlyDictionary<string, string> Metadata { get; }
}

internal static class B4Validation
{
    internal static string Text(string value, string name, int max = 512) => string.IsNullOrWhiteSpace(value) || value.Length > max
        ? throw new ArgumentException($"{name} must be nonblank and at most {max} characters.", name) : value;
    internal static string OrderingKey(string value)
    {
        Text(value, nameof(value), 64);
        if (value.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-') || value[0] == '-')
            throw new ArgumentException("Ordering key must be canonical.", nameof(value));
        return value;
    }
    internal static IReadOnlyList<string> Capabilities(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > 32) throw new ArgumentException("Capabilities must contain 1 to 32 entries.", nameof(values));
        var canonical = values.Select((value, index) => Text(value, $"capabilities[{index}]").Trim()).ToArray();
        if (canonical.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64) || canonical.Distinct(StringComparer.Ordinal).Count() != canonical.Length)
            throw new ArgumentException("Capabilities must be nonblank and ordinal-distinct.", nameof(values));
        Array.Sort(canonical, StringComparer.Ordinal);
        return Array.AsReadOnly(canonical);
    }
    internal static bool SafeReference(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
        !Path.IsPathRooted(value) && !value.Contains('\\') && !value.Contains(':') &&
        value.Split('/').All(segment => segment is not ("." or ".."));
    internal static IReadOnlyDictionary<string, string> Dictionary(IReadOnlyDictionary<string, string> values, int valueLimit, string name)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Count > 64) throw new ArgumentException("Dictionary cannot contain more than 64 entries.", name);
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            Text(pair.Key, "key", 128);
            Text(pair.Value, "value", valueLimit);
            if (!copy.TryAdd(pair.Key, pair.Value)) throw new ArgumentException("Dictionary keys must be unique.", name);
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }

    internal static void ValidateLifecycle(TransactionalParticipantLifecycleState state, string? handle, string? version,
        bool changed, bool ambiguous)
    {
        bool hasHandle = handle is not null && version is not null;
        if (state is TransactionalParticipantLifecycleState.Prepared or TransactionalParticipantLifecycleState.Committed && !hasHandle)
            throw new ArgumentException("Prepared and committed participants require a complete handle.");
        if (state is TransactionalParticipantLifecycleState.Changed && (!changed || ambiguous) ||
            state is TransactionalParticipantLifecycleState.Ambiguous && (!ambiguous || !changed) ||
            state is not (TransactionalParticipantLifecycleState.Changed or TransactionalParticipantLifecycleState.Ambiguous) && (changed || ambiguous))
            throw new ArgumentException("Lifecycle state and change flags are inconsistent.");
    }
}

internal sealed record MultiMemoryParticipantManifest
{
    public MultiMemoryParticipantManifest(string subject, string operationId, string kind)
    {
        Subject = RequireText(subject, nameof(subject));
        OperationId = RequireText(operationId, nameof(operationId));
        Kind = RequireText(kind, nameof(kind));
    }

    public string Subject { get; }
    public string OperationId { get; }
    public string Kind { get; }

    internal void Deconstruct(out string subject, out string operationId, out string kind) =>
        (subject, operationId, kind) = (Subject, OperationId, Kind);


}

internal sealed record MultiMemoryParticipantOutcome
{
    internal MultiMemoryParticipantOutcome(
        string subject,
        string operationId,
        string kind,
        string? evidencePath,
        bool committed,
        bool rolledBack,
        string? reason,
        int ordinal = -1)
    {
        Subject = RequireText(subject, nameof(subject));
        OperationId = RequireText(operationId, nameof(operationId));
        Kind = RequireText(kind, nameof(kind));
        if (evidencePath is not null && !IsSafeEvidencePath(evidencePath))
            throw new ArgumentException("Evidence path must be a safe relative path.", nameof(evidencePath));
        if (committed && rolledBack)
            throw new ArgumentException("A participant cannot be both committed and rolled back.");

        EvidencePath = evidencePath;
        Committed = committed;
        RolledBack = rolledBack;
        Reason = reason;
        Ordinal = ordinal;
    }

    internal string Subject { get; }
    internal string OperationId { get; }
    internal string Kind { get; }
    internal string? EvidencePath { get; }
    internal bool Committed { get; }
    internal bool RolledBack { get; }
    internal string? Reason { get; }
    internal int Ordinal { get; }

    internal void Deconstruct(
        out string subject,
        out string operationId,
        out string kind,
        out string? evidencePath,
        out bool committed,
        out bool rolledBack,
        out string? reason) =>
        (subject, operationId, kind, evidencePath, committed, rolledBack, reason) =
        (Subject, OperationId, Kind, EvidencePath, Committed, RolledBack, Reason);



    internal static bool IsSafeEvidencePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Contains('\\') &&
        !path.Contains(':') &&
        path.Split('/', StringSplitOptions.None).All(segment => segment is not ".." and not ".");
}

internal sealed record MultiMemoryTransactionalParticipantOutcome
{
    internal MultiMemoryTransactionalParticipantOutcome(string participantIdentity, string orderingKey,
        string operationId, string kind, bool admitted, TransactionalParticipantLifecycleState lifecycleState,
        bool committed, bool rolledBack, string? evidencePath, string? reason, int ordinal = -1)
    {
        ParticipantIdentity = B4Validation.Text(participantIdentity, nameof(participantIdentity));
        OrderingKey = B4Validation.OrderingKey(orderingKey);
        OperationId = B4Validation.Text(operationId, nameof(operationId));
        Kind = B4Validation.Text(kind, nameof(kind));
        if (!Enum.IsDefined(lifecycleState)) throw new ArgumentException("Lifecycle state is unsupported.", nameof(lifecycleState));
        if (committed && rolledBack) throw new ArgumentException("A participant cannot be both committed and rolled back.");
        Admitted = admitted; LifecycleState = lifecycleState; Committed = committed; RolledBack = rolledBack;
        EvidencePath = evidencePath; Reason = reason; Ordinal = ordinal;
    }
    internal string ParticipantIdentity { get; }
    internal string OrderingKey { get; }
    internal string OperationId { get; }
    internal string Kind { get; }
    internal bool Admitted { get; }
    internal TransactionalParticipantLifecycleState LifecycleState { get; }
    internal bool Committed { get; }
    internal bool RolledBack { get; }
    internal string? EvidencePath { get; }
    internal string? Reason { get; }
    internal int Ordinal { get; }
}

internal sealed record MultiMemoryAggregateProjectionInput(
    IReadOnlyList<MultiMemoryParticipantOutcome> B3Outcomes,
    IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> AdmittedB4Outcomes,
    IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> NonAdmittedB4Outcomes);

internal sealed record MultiMemoryAggregateResult
{
    internal MultiMemoryAggregateResult(
        string transactionId,
        string scope,
        MultiMemoryOutcome outcome,
        IReadOnlyList<MultiMemoryParticipantOutcome> participants,
        MultiMemoryFailure? failure,
        string? warning,
        string? evidencePath)
        : this(transactionId, scope, outcome,
            participants.Select(participant => new MultiMemoryParticipantRequest(
                MultiMemoryTransactionRequest.AddressForScope(scope, participant.Subject),
                participant.OperationId, participant.Kind)).ToArray(),
            BuildLegacyProjection(participants, scope), failure, warning, evidencePath)
    {
    }

    private MultiMemoryAggregateResult(
        string transactionId,
        string scope,
        MultiMemoryOutcome outcome,
        IReadOnlyList<MultiMemoryParticipantRequest> expectedParticipants,
        ValidatedMultiMemoryAggregateProjection projection,
        MultiMemoryFailure? failure,
        string? warning,
        string? evidencePath)
    {
        TransactionId = RequireText(transactionId, nameof(transactionId));
        Scope = RequireText(scope, nameof(scope));
        ArgumentNullException.ThrowIfNull(expectedParticipants);
        ArgumentNullException.ThrowIfNull(projection);
        if (outcome == MultiMemoryOutcome.RecoveryRequired &&
            !MultiMemoryParticipantOutcome.IsSafeEvidencePath(evidencePath ?? string.Empty))
            throw new ArgumentException(
                "A recovery-required result needs a safe evidence path.", nameof(evidencePath));

        Outcome = outcome;
        Participants = projection.Participants;
        TransactionalParticipants = projection.TransactionalParticipants;
        NonAdmittedParticipants = projection.NonAdmittedParticipants;
        Failure = failure;
        Warning = warning;
        EvidencePath = evidencePath;
    }

    internal string TransactionId { get; }
    internal string Scope { get; }
    internal MultiMemoryOutcome Outcome { get; }
    internal IReadOnlyList<MultiMemoryParticipantOutcome> Participants { get; }
    internal IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> TransactionalParticipants { get; }
    internal IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> NonAdmittedParticipants { get; }
    internal MultiMemoryFailure? Failure { get; }
    internal string? Warning { get; }
    internal string? EvidencePath { get; }

    internal void Deconstruct(
        out string transactionId,
        out string scope,
        out MultiMemoryOutcome outcome,
        out IReadOnlyList<MultiMemoryParticipantOutcome> participants,
        out MultiMemoryFailure? failure,
        out string? warning,
        out string? evidencePath) =>
        (transactionId, scope, outcome, participants, failure, warning, evidencePath) =
        (TransactionId, Scope, Outcome, Participants, Failure, Warning, EvidencePath);



    internal static MultiMemoryAggregateResult Create(string transactionId, string scope,
        MultiMemoryOutcome outcome, IReadOnlyList<MultiMemoryParticipantRequest> expectedParticipants,
        MultiMemoryAggregateProjectionInput input, MultiMemoryFailure? failure, string? warning,
        string? evidencePath)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidatedMultiMemoryAggregateProjection projection = BuildAggregateProjection(expectedParticipants, input);
        return new MultiMemoryAggregateResult(transactionId, scope, outcome, expectedParticipants, projection,
            failure, warning, evidencePath);
    }

    private static ValidatedMultiMemoryAggregateProjection BuildLegacyProjection(
        IReadOnlyList<MultiMemoryParticipantOutcome> participants, string scope) =>
        participants.Count == 0
            ? ValidatedMultiMemoryAggregateProjection.Create([], [], [])
            : BuildAggregateProjection(
            participants.Select(participant => new MultiMemoryParticipantRequest(
                MultiMemoryTransactionRequest.AddressForScope(scope, participant.Subject),
                participant.OperationId, participant.Kind)).ToArray(),
            new MultiMemoryAggregateProjectionInput(
                participants.Select((participant, ordinal) => new MultiMemoryParticipantOutcome(
                    participant.Subject, participant.OperationId, participant.Kind, participant.EvidencePath,
                    participant.Committed, participant.RolledBack, participant.Reason, ordinal)).ToArray(), [], []));

    private static ValidatedMultiMemoryAggregateProjection BuildAggregateProjection(
        IReadOnlyList<MultiMemoryParticipantRequest> requestedParticipants,
        MultiMemoryAggregateProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(requestedParticipants);
        if (requestedParticipants.Count == 0) throw new ArgumentException("At least one participant is required.", nameof(requestedParticipants));
        var requested = requestedParticipants.Select((participant, index) =>
            (Participant: participant, Key: ParticipantOrderKey.Create(participant, index))).ToArray();
        var ordered = requested.OrderBy(item => item.Key).ToArray();
        Dictionary<(string Subject, string OperationId, string Kind), MultiMemoryParticipantOutcome> b3;
        Dictionary<(string ParticipantIdentity, string OperationId, string Kind), MultiMemoryTransactionalParticipantOutcome> admitted;
        Dictionary<(string ParticipantIdentity, string OperationId, string Kind), MultiMemoryTransactionalParticipantOutcome> rejected;
        try
        {
            b3 = input.B3Outcomes.ToDictionary(outcome => (outcome.Subject, outcome.OperationId, outcome.Kind));
            admitted = input.AdmittedB4Outcomes.ToDictionary(outcome => (outcome.ParticipantIdentity, outcome.OperationId, outcome.Kind));
            rejected = input.NonAdmittedB4Outcomes.ToDictionary(outcome => (outcome.ParticipantIdentity, outcome.OperationId, outcome.Kind));
        }
        catch (ArgumentException exception)
        {
            throw InconsistentProjection("Aggregate projection contains duplicate participant outcomes.", exception);
        }
        var projectedB3 = new List<MultiMemoryParticipantOutcome>();
        var projectedAdmitted = new List<MultiMemoryTransactionalParticipantOutcome>();
        var projectedRejected = new List<MultiMemoryTransactionalParticipantOutcome>();
        for (int ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            MultiMemoryParticipantRequest participant = ordered[ordinal].Participant;
            if (participant.ParticipantIdentity is null)
            {
                string subject = participant.Address.Value[(participant.Address.Value.LastIndexOf('/') + 1)..];
                if (!b3.Remove((subject, participant.OperationId, participant.Kind)))
                    throw InconsistentProjection("B3 projection does not match the requested participant set.");
                MultiMemoryParticipantOutcome outcome = input.B3Outcomes.Single(item => item.Subject == subject && item.OperationId == participant.OperationId && item.Kind == participant.Kind);
                ValidateOrdinal(outcome.Ordinal, ordinal);
                projectedB3.Add(new MultiMemoryParticipantOutcome(outcome.Subject, outcome.OperationId, outcome.Kind,
                    outcome.EvidencePath, outcome.Committed, outcome.RolledBack, outcome.Reason, ordinal));
                continue;
            }
            var key = (participant.ParticipantIdentity, participant.OperationId, participant.Kind);
            if (admitted.Remove(key))
            {
                MultiMemoryTransactionalParticipantOutcome outcome = input.AdmittedB4Outcomes.Single(item => item.ParticipantIdentity == key.ParticipantIdentity && item.OperationId == key.OperationId && item.Kind == key.Kind);
                ValidateOrdinal(outcome.Ordinal, ordinal);
                projectedAdmitted.Add(new MultiMemoryTransactionalParticipantOutcome(outcome.ParticipantIdentity, outcome.OrderingKey, outcome.OperationId,
                    outcome.Kind, true, outcome.LifecycleState, outcome.Committed, outcome.RolledBack, outcome.EvidencePath, outcome.Reason, ordinal));
            }
            else if (rejected.Remove(key))
            {
                MultiMemoryTransactionalParticipantOutcome outcome = input.NonAdmittedB4Outcomes.Single(item => item.ParticipantIdentity == key.ParticipantIdentity && item.OperationId == key.OperationId && item.Kind == key.Kind);
                ValidateOrdinal(outcome.Ordinal, ordinal);
                projectedRejected.Add(new MultiMemoryTransactionalParticipantOutcome(outcome.ParticipantIdentity, outcome.OrderingKey, outcome.OperationId,
                    outcome.Kind, false, outcome.LifecycleState, false, false, outcome.EvidencePath, outcome.Reason, ordinal));
            }
            else throw InconsistentProjection("B4 projection does not match the requested participant set.");
        }
        if (b3.Count != 0 || admitted.Count != 0 || rejected.Count != 0)
            throw InconsistentProjection("Aggregate projection contains an extra participant.");
        return ValidatedMultiMemoryAggregateProjection.Create(projectedB3.AsReadOnly(), projectedAdmitted.AsReadOnly(), projectedRejected.AsReadOnly());
    }

    private static void ValidateOrdinal(int ordinal, int expectedOrdinal)
    {
        if (ordinal < 0 || ordinal != expectedOrdinal)
            throw InconsistentProjection("Aggregate projection ordinal is not canonical.");
    }

    private static MultiMemoryTransactionRecordException InconsistentProjection(string message, Exception? inner = null) =>
        new(message, MultiMemoryFailure.Inconsistent, inner);

    private sealed class ValidatedMultiMemoryAggregateProjection
    {
        internal static ValidatedMultiMemoryAggregateProjection Create(
            IReadOnlyList<MultiMemoryParticipantOutcome> participants,
            IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> transactionalParticipants,
            IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> nonAdmittedParticipants) =>
            new(participants, transactionalParticipants, nonAdmittedParticipants);

        private ValidatedMultiMemoryAggregateProjection(IReadOnlyList<MultiMemoryParticipantOutcome> participants,
            IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> transactionalParticipants,
            IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> nonAdmittedParticipants)
        {
            Participants = participants; TransactionalParticipants = transactionalParticipants; NonAdmittedParticipants = nonAdmittedParticipants;
        }
        internal IReadOnlyList<MultiMemoryParticipantOutcome> Participants { get; }
        internal IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> TransactionalParticipants { get; }
        internal IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> NonAdmittedParticipants { get; }
    }

    private readonly record struct ParticipantOrderKey(string? OrderingKey, int ParticipantClass,
        string CanonicalIdentity, string OperationId, string Kind) : IComparable<ParticipantOrderKey>
    {
        internal static ParticipantOrderKey Create(MultiMemoryParticipantRequest participant, int _) =>
            participant.ParticipantIdentity is null
                ? new(null, 0, participant.Address.Value, participant.OperationId, participant.Kind)
                : new(participant.OrderingKey, 1, participant.ParticipantIdentity, participant.OperationId, participant.Kind);
        public int CompareTo(ParticipantOrderKey other)
        {
            int ordering = OrderingKey is null
                ? other.OrderingKey is null ? 0 : -1
                : other.OrderingKey is null ? 1 : string.CompareOrdinal(OrderingKey, other.OrderingKey);
            if (ordering != 0) return ordering;
            int participantClass = ParticipantClass.CompareTo(other.ParticipantClass);
            if (participantClass != 0) return participantClass;
            int identity = string.CompareOrdinal(CanonicalIdentity, other.CanonicalIdentity);
            if (identity != 0) return identity;
            int operation = string.CompareOrdinal(OperationId, other.OperationId);
            return operation != 0 ? operation : string.CompareOrdinal(Kind, other.Kind);
        }
    }
}

internal interface IMultiMemoryParticipantAdapter
{
    string Kind { get; }
    string Scope { get; }
    string Subject { get; }
    string OperationId { get; }
    string EvidencePath { get; }
    B4ParticipantRecoveryState? RecoveryState => null;
    bool? ObservableStateChanged => true;
    Task PrepareAsync(CancellationToken cancellationToken);
    Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task VerifyCommittedAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
    Task CleanupAsync(CancellationToken cancellationToken);
}

internal interface IMultiMemoryTransactionCoordinator
{
    Task<MultiMemoryAggregateResult> ExecuteAsync(
        MultiMemoryTransactionRequest request,
        Func<MultiMemoryParticipantRequest, CancellationToken,
            Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken = default);

    Task<MultiMemoryAggregateResult> RecoverAsync(
        string scope,
        CancellationToken cancellationToken = default);
}
