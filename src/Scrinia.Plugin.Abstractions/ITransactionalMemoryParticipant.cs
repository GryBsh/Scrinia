using Scrinia.Core.Models;

namespace Scrinia.Plugin.Abstractions;

public interface ITransactionalMemoryParticipant : IScriniaPlugin
{
    TransactionalParticipantDescriptor Descriptor { get; }
    Task<TransactionalPrepareResult> PrepareAsync(TransactionalPrepareContext context, CancellationToken cancellationToken = default);
    Task<TransactionalParticipantResult> CommitAsync(TransactionalCommitContext context, CancellationToken cancellationToken = default);
    Task<TransactionalParticipantResult> RollbackAsync(TransactionalRollbackContext context, CancellationToken cancellationToken = default);
    Task<TransactionalCommitStatusResult> GetCommitStatusAsync(TransactionalCommitContext context, CancellationToken cancellationToken = default);
}

public sealed record TransactionalParticipantDescriptor
{
    public TransactionalParticipantDescriptor(string participantIdentity, int contractVersion,
        IReadOnlyList<string> capabilities, TransactionalRetryPolicy retryPolicy, string orderingKey)
    {
        ParticipantIdentity = ContractValidation.Text(participantIdentity, nameof(participantIdentity));
        if (contractVersion != 1) throw new ArgumentException("Contract version must be 1.", nameof(contractVersion));
        ContractVersion = contractVersion;
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count > 32) throw new ArgumentException("Capabilities cannot exceed 32 entries.", nameof(capabilities));
        var canonicalCapabilities = capabilities.Select((value, index) => ContractValidation.Capability(value, index)).ToArray();
        if (canonicalCapabilities.Distinct(StringComparer.Ordinal).Count() != canonicalCapabilities.Length)
            throw new ArgumentException("Capabilities must be ordinal-distinct.", nameof(capabilities));
        Capabilities = canonicalCapabilities.Order(StringComparer.Ordinal).ToArray();
        RetryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        OrderingKey = ContractValidation.OrderingKey(orderingKey);
    }
    public string ParticipantIdentity { get; }
    public int ContractVersion { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public TransactionalRetryPolicy RetryPolicy { get; }
    public string OrderingKey { get; }
}

public sealed record TransactionalRetryPolicy
{
    public TransactionalRetryPolicy(int maxAttempts = 3, TimeSpan? maxElapsed = null,
        TimeSpan? initialDelay = null, TimeSpan? maximumDelay = null)
    {
        if (maxAttempts is < 1 or > 8) throw new ArgumentException("Max attempts must be between 1 and 8.", nameof(maxAttempts));
        MaxAttempts = maxAttempts;
        MaxElapsed = maxElapsed ?? TimeSpan.FromSeconds(30);
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(100);
        MaximumDelay = maximumDelay ?? TimeSpan.FromSeconds(2);
        if (MaxElapsed < TimeSpan.Zero || MaxElapsed > TimeSpan.FromMinutes(5) || MaxElapsed == TimeSpan.Zero)
            throw new ArgumentException("Max elapsed must be between 1 tick and 5 minutes.", nameof(maxElapsed));
        if (InitialDelay < TimeSpan.Zero || MaximumDelay < TimeSpan.Zero || MaximumDelay > TimeSpan.FromSeconds(30) || InitialDelay > MaximumDelay)
            throw new ArgumentException("Retry delays must be nonnegative, ordered, and at most 30 seconds.", nameof(maximumDelay));
    }
    public int MaxAttempts { get; }
    public TimeSpan MaxElapsed { get; }
    public TimeSpan InitialDelay { get; }
    public TimeSpan MaximumDelay { get; }
}

public sealed record TransactionalPrepareContext
{
    public TransactionalPrepareContext(string transactionId, string scope, MemoryAddress address,
        string operationId, string kind, IReadOnlyList<TransactionalMemoryManifestEntry> manifest)
    {
        TransactionId = ContractValidation.Text(transactionId, nameof(transactionId));
        Scope = ContractValidation.Text(scope, nameof(scope)); Address = ContractValidation.Address(address);
        OperationId = ContractValidation.Text(operationId, nameof(operationId)); Kind = ContractValidation.Text(kind, nameof(kind));
        Manifest = ContractValidation.Manifest(manifest);
    }
    public string TransactionId { get; } public string Scope { get; } public MemoryAddress Address { get; }
    public string OperationId { get; } public string Kind { get; } public IReadOnlyList<TransactionalMemoryManifestEntry> Manifest { get; }
}

public sealed record TransactionalCommitContext
{
    public TransactionalCommitContext(string transactionId, string scope, MemoryAddress address,
        string operationId, string kind, TransactionalPreparedHandle preparedHandle)
    {
        TransactionId = ContractValidation.Text(transactionId, nameof(transactionId)); Scope = ContractValidation.Text(scope, nameof(scope));
        Address = ContractValidation.Address(address); OperationId = ContractValidation.Text(operationId, nameof(operationId));
        Kind = ContractValidation.Text(kind, nameof(kind)); PreparedHandle = preparedHandle ?? throw new ArgumentNullException(nameof(preparedHandle));
    }
    public string TransactionId { get; } public string Scope { get; } public MemoryAddress Address { get; }
    public string OperationId { get; } public string Kind { get; } public TransactionalPreparedHandle PreparedHandle { get; }
}

public sealed record TransactionalRollbackContext
{
    public TransactionalRollbackContext(string transactionId, string scope, MemoryAddress address,
        string operationId, string kind, TransactionalPreparedHandle preparedHandle)
    {
        TransactionId = ContractValidation.Text(transactionId, nameof(transactionId)); Scope = ContractValidation.Text(scope, nameof(scope));
        Address = ContractValidation.Address(address); OperationId = ContractValidation.Text(operationId, nameof(operationId));
        Kind = ContractValidation.Text(kind, nameof(kind)); PreparedHandle = preparedHandle ?? throw new ArgumentNullException(nameof(preparedHandle));
    }
    public string TransactionId { get; } public string Scope { get; } public MemoryAddress Address { get; }
    public string OperationId { get; } public string Kind { get; } public TransactionalPreparedHandle PreparedHandle { get; }
}

public sealed record TransactionalMemoryManifestEntry
{
    public TransactionalMemoryManifestEntry(MemoryAddress address, string operationId, string kind)
    { Address = ContractValidation.Address(address); OperationId = ContractValidation.Text(operationId, nameof(operationId)); Kind = ContractValidation.Text(kind, nameof(kind)); }
    public MemoryAddress Address { get; }
    public string OperationId { get; }
    public string Kind { get; }
}

public sealed record TransactionalPreparedHandle
{
    public TransactionalPreparedHandle(string reference, string version) { Reference = ContractValidation.Text(reference, nameof(reference)); Version = ContractValidation.Text(version, nameof(version)); }
    public string Reference { get; } public string Version { get; }
}

public sealed record TransactionalParticipantFailure
{
    public TransactionalParticipantFailure(TransactionalFailureCode code, string detail, bool stateMayHaveChanged)
    { ContractValidation.FailureCode(code); Code = code; Detail = ContractValidation.Text(detail, nameof(detail)); StateMayHaveChanged = stateMayHaveChanged; }
    public TransactionalFailureCode Code { get; } public string Detail { get; } public bool StateMayHaveChanged { get; }
}

public sealed record TransactionalRecoveryEvidence
{
    public TransactionalRecoveryEvidence(string evidenceReference, string evidenceKind, string evidenceVersion)
    { EvidenceReference = ContractValidation.EvidencePath(evidenceReference); EvidenceKind = ContractValidation.Text(evidenceKind, nameof(evidenceKind)); EvidenceVersion = ContractValidation.Text(evidenceVersion, nameof(evidenceVersion)); }
    public string EvidenceReference { get; } public string EvidenceKind { get; } public string EvidenceVersion { get; }
}

public enum TransactionalParticipantState { Committed, RolledBack, Changed, Ambiguous }
public enum TransactionalCommitStatus { Unknown, Prepared, Committed }
public enum TransactionalFailureCode { Veto, DeclaredTransient, Permanent, UnknownTransient, Cancelled, InvalidResponse, CommitFailed, VerificationFailed, RollbackFailed, EvidenceInvalid, RetryMonotonicClockFailed, RetryMonotonicClockMovedBackwards, RetryElapsedOverflow }

public sealed record TransactionalPrepareResult
{
    public TransactionalPrepareResult(TransactionalPreparedHandle? handle, TransactionalParticipantFailure? failure, TransactionalRecoveryEvidence? evidence)
    {
        if ((handle is null) == (failure is null) || handle is not null && evidence is not null) throw new ArgumentException("Invalid prepare result combination.");
        Handle = handle; Failure = failure; Evidence = evidence;
    }
    public TransactionalPreparedHandle? Handle { get; } public TransactionalParticipantFailure? Failure { get; } public TransactionalRecoveryEvidence? Evidence { get; }
}

public sealed record TransactionalParticipantResult
{
    public TransactionalParticipantResult(TransactionalParticipantState state, TransactionalParticipantFailure? failure, TransactionalRecoveryEvidence? evidence)
    {
        if (!Enum.IsDefined(state) || (state is TransactionalParticipantState.Committed or TransactionalParticipantState.RolledBack) != (failure is null && evidence is null) || (state is TransactionalParticipantState.Changed or TransactionalParticipantState.Ambiguous) != (failure is not null && evidence is not null)) throw new ArgumentException("Invalid participant result combination.");
        State = state; Failure = failure; Evidence = evidence;
    }
    public TransactionalParticipantState State { get; } public TransactionalParticipantFailure? Failure { get; } public TransactionalRecoveryEvidence? Evidence { get; }
}

public sealed record TransactionalCommitStatusResult
{
    public TransactionalCommitStatusResult(TransactionalCommitStatus state, TransactionalRecoveryEvidence? evidence, TransactionalParticipantFailure? failure)
    {
        var valid = state is TransactionalCommitStatus.Prepared or TransactionalCommitStatus.Committed ? evidence is null && failure is null : state == TransactionalCommitStatus.Unknown && evidence is not null && failure is not null;
        if (!Enum.IsDefined(state) || !valid) throw new ArgumentException("Invalid commit status result combination.");
        State = state; Evidence = evidence; Failure = failure;
    }
    public TransactionalCommitStatus State { get; } public TransactionalRecoveryEvidence? Evidence { get; } public TransactionalParticipantFailure? Failure { get; }
}

internal static class ContractValidation
{
    internal static string Text(string value, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException("Text must be nonblank and at most 512 characters.", name); return value; }
    internal static string Capability(string value, int index) { var trimmed = Text(value, $"capabilities[{index}]").Trim(); if (trimmed.Length > 64 || trimmed.Length == 0) throw new ArgumentException("Capability must be nonblank and at most 64 characters."); return trimmed; }
    internal static string OrderingKey(string value) { Text(value, nameof(value)); if (value.Length > 64 || value.Any(c => !(c is >= 'a' and <= 'z') && !(c is >= '0' and <= '9') && c != '-') || value[0] == '-') throw new ArgumentException("Ordering key is not canonical.", nameof(value)); return value; }
    internal static MemoryAddress Address(MemoryAddress value) { if (!value.IsValid) throw new ArgumentException("Address must be valid.", nameof(value)); return value; }
    internal static IReadOnlyList<TransactionalMemoryManifestEntry> Manifest(IReadOnlyList<TransactionalMemoryManifestEntry> value) { ArgumentNullException.ThrowIfNull(value); if (value.Count is < 1 or > 4096 || value.Any(item => item is null)) throw new ArgumentException("Manifest must contain 1 to 4096 entries.", nameof(value)); return value.ToArray(); }
    internal static string EvidencePath(string value) { Text(value, nameof(value)); if (Path.IsPathRooted(value) || value.Contains('\\') || value.Contains(':') || value.Split('/').Any(segment => segment is "." or "..")) throw new ArgumentException("Evidence reference must be a safe relative path.", nameof(value)); return value; }
    internal static void FailureCode(TransactionalFailureCode value) { if (!Enum.IsDefined(value)) throw new ArgumentException("Unknown failure code.", nameof(value)); }
}
