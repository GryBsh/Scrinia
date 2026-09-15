using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Scrinia.Core;

internal enum MaintenanceCandidateKind
{
    ArchivePair,
    CompletedRecoveryEvidence
}

internal enum MaintenanceProtectionReason
{
    Current,
    Active,
    Referenced,
    ExplicitlyProtected,
    UnresolvedRecovery,
    IncompletePair,
    Malformed,
    UnsupportedVersion,
    Ambiguous,
    Unsafe,
    Inaccessible,
    Unverifiable,
    YoungerThanMinimumAge,
    WithinRetentionLimit,
    MissingExpectedMember,
    InvalidContinuation,
    DeletionFailed
}

internal sealed record MaintenanceRequest
{
    internal MaintenanceRequest(IReadOnlyList<string>? scopes, int? batchSizeOverride,
        IReadOnlySet<string>? explicitlyProtectedRelativePaths, string? continuationToken)
    {
        Scopes = scopes is null ? null : MaintenanceContractValidation.CopyScopes(scopes);
        BatchSizeOverride = ValidateBatchSize(batchSizeOverride);
        ExplicitlyProtectedRelativePaths = explicitlyProtectedRelativePaths is null
            ? MaintenanceScopes.EmptyProtectionSet
            : MaintenanceContractValidation.CopyRelativePathSet(explicitlyProtectedRelativePaths);
        ContinuationToken = continuationToken is null
            ? null
            : MaintenanceContractValidation.RequireToken(continuationToken, nameof(continuationToken));
    }

    public IReadOnlyList<string>? Scopes { get; }
    public int? BatchSizeOverride { get; }
    public IReadOnlySet<string> ExplicitlyProtectedRelativePaths { get; }
    public string? ContinuationToken { get; }

    private static int? ValidateBatchSize(int? value)
    {
        if (value is not (null or >= 1 and <= 10000))
            throw new ArgumentException("Batch size override must be between 1 and 10000.", nameof(value));

        return value;
    }
}

internal static class MaintenanceScopes
{
    internal const string DefaultLocalScope = "local";
    internal static IReadOnlySet<string> EmptyProtectionSet { get; } =
        new HashSet<string>(StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);
}

internal sealed record MaintenanceCandidate
{
    internal MaintenanceCandidate(string scope, string subject, MaintenanceCandidateKind kind,
        string bodyPath, string? sidecarPath, DateTimeOffset? candidateTimestamp, string? operationId,
        MaintenanceProtectionReason? protection, string relativePath)
    {
        Scope = MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
        Subject = MaintenanceContractValidation.RequireSafeText(subject, nameof(subject));
        Kind = kind;
        BodyPath = MaintenanceContractValidation.RequireRelativePath(bodyPath, nameof(bodyPath));
        SidecarPath = sidecarPath is null ? null : MaintenanceContractValidation.RequireRelativePath(sidecarPath, nameof(sidecarPath));
        CandidateTimestamp = candidateTimestamp;
        OperationId = operationId is null ? null : MaintenanceContractValidation.RequireSafeText(operationId, nameof(operationId));
        Protection = protection;
        RelativePath = MaintenanceContractValidation.RequireRelativePath(relativePath, nameof(relativePath));
    }

    public string Scope { get; }
    public string Subject { get; }
    public MaintenanceCandidateKind Kind { get; }
    public string BodyPath { get; }
    public string? SidecarPath { get; }
    public DateTimeOffset? CandidateTimestamp { get; }
    public string? OperationId { get; }
    public MaintenanceProtectionReason? Protection { get; }
    public string RelativePath { get; }
}

internal sealed record MaintenanceCandidateResult
{
    internal MaintenanceCandidateResult(MaintenanceCandidate candidate, bool deleted,
        MaintenanceProtectionReason? reason, string? warning)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Deleted = deleted;
        Reason = reason;
        Warning = warning;
    }

    public MaintenanceCandidate Candidate { get; }
    public bool Deleted { get; }
    public MaintenanceProtectionReason? Reason { get; }
    public string? Warning { get; }
}

internal sealed record MaintenanceScopeResult
{
    internal MaintenanceScopeResult(string scope, int inspected, int deletedArchives, int deletedRecoveryEvidence,
        int retained, bool lockContended, bool moreWork, string? continuationToken,
        IReadOnlyList<MaintenanceCandidateResult> candidates, IReadOnlyList<string> warnings)
    {
        Scope = MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
        Inspected = RequireNonNegative(inspected, nameof(inspected));
        DeletedArchives = RequireNonNegative(deletedArchives, nameof(deletedArchives));
        DeletedRecoveryEvidence = RequireNonNegative(deletedRecoveryEvidence, nameof(deletedRecoveryEvidence));
        Retained = RequireNonNegative(retained, nameof(retained));
        LockContended = lockContended;
        MoreWork = moreWork;
        ContinuationToken = continuationToken is null ? null : MaintenanceContractValidation.RequireToken(continuationToken, nameof(continuationToken));
        Candidates = MaintenanceContractValidation.CopyCandidates(candidates);
        Warnings = MaintenanceContractValidation.CopyStrings(warnings, nameof(warnings));
    }

    public string Scope { get; }
    public int Inspected { get; }
    public int DeletedArchives { get; }
    public int DeletedRecoveryEvidence { get; }
    public int Retained { get; }
    public bool LockContended { get; }
    public bool MoreWork { get; }
    public string? ContinuationToken { get; }
    public IReadOnlyList<MaintenanceCandidateResult> Candidates { get; }
    public IReadOnlyList<string> Warnings { get; }

    private static int RequireNonNegative(int value, string name) => value < 0
        ? throw new ArgumentException("Counts cannot be negative.", name)
        : value;
}

internal sealed record MaintenanceResult
{
    internal MaintenanceResult(int inspected, int deletedArchives, int deletedRecoveryEvidence, bool completed,
        bool moreWork, bool recoveryRetained, bool lockContended, bool policyInvalid, bool partialFailure,
        bool hasWarnings, string? continuationToken, IReadOnlyList<MaintenanceScopeResult> scopes,
        IReadOnlyList<string> warnings, string? policyError)
    {
        Inspected = RequireNonNegative(inspected, nameof(inspected));
        DeletedArchives = RequireNonNegative(deletedArchives, nameof(deletedArchives));
        DeletedRecoveryEvidence = RequireNonNegative(deletedRecoveryEvidence, nameof(deletedRecoveryEvidence));
        Completed = completed;
        MoreWork = moreWork;
        RecoveryRetained = recoveryRetained;
        LockContended = lockContended;
        PolicyInvalid = policyInvalid;
        PartialFailure = partialFailure;
        ContinuationToken = continuationToken is null ? null : MaintenanceContractValidation.RequireToken(continuationToken, nameof(continuationToken));
        Scopes = MaintenanceContractValidation.CopyScopesResults(scopes);
        Warnings = MaintenanceContractValidation.CopyStrings(warnings, nameof(warnings));
        HasWarnings = Warnings.Count > 0;
        PolicyError = policyError;
    }

    public int Inspected { get; }
    public int DeletedArchives { get; }
    public int DeletedRecoveryEvidence { get; }
    public bool Completed { get; }
    public bool MoreWork { get; }
    public bool RecoveryRetained { get; }
    public bool LockContended { get; }
    public bool PolicyInvalid { get; }
    public bool PartialFailure { get; }
    public bool HasWarnings { get; }
    public string? ContinuationToken { get; }
    public IReadOnlyList<MaintenanceScopeResult> Scopes { get; }
    public IReadOnlyList<string> Warnings { get; }
    public string? PolicyError { get; }

    private static int RequireNonNegative(int value, string name) => value < 0
        ? throw new ArgumentException("Counts cannot be negative.", name)
        : value;
}

internal sealed record MaintenanceContinuationPayload
{
    internal MaintenanceContinuationPayload(int version, string workspaceBinding, string requestedScopesBinding,
        string scope, string phase, string subject, string lastRelativePath,
        IReadOnlyList<MaintenanceSnapshotCandidate> snapshotCandidates, long snapshotBytes, DateTimeOffset expiresAt)
    {
        Version = version;
        WorkspaceBinding = MaintenanceContractValidation.RequireSafeText(workspaceBinding, nameof(workspaceBinding));
        RequestedScopesBinding = MaintenanceContractValidation.RequireSafeText(requestedScopesBinding, nameof(requestedScopesBinding));
        Scope = MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
        Phase = MaintenanceContractValidation.RequireSafeText(phase, nameof(phase));
        Subject = MaintenanceContractValidation.RequireSafeText(subject, nameof(subject));
        LastRelativePath = MaintenanceContractValidation.RequireRelativePath(lastRelativePath, nameof(lastRelativePath));
        SnapshotCandidates = new ReadOnlyCollection<MaintenanceSnapshotCandidate>(snapshotCandidates
            .Select(candidate => candidate ?? throw new ArgumentException("Snapshot candidates cannot contain null elements.", nameof(snapshotCandidates)))
            .ToArray());
        SnapshotBytes = snapshotBytes < 0 ? throw new ArgumentException("Snapshot bytes cannot be negative.", nameof(snapshotBytes)) : snapshotBytes;
        ExpiresAt = expiresAt;
    }

    public int Version { get; }
    public string WorkspaceBinding { get; }
    public string RequestedScopesBinding { get; }
    public string Scope { get; }
    public string Phase { get; }
    public string Subject { get; }
    public string LastRelativePath { get; }
    public IReadOnlyList<MaintenanceSnapshotCandidate> SnapshotCandidates { get; }
    public long SnapshotBytes { get; }
    public DateTimeOffset ExpiresAt { get; }
}

internal sealed record MaintenanceSnapshotCandidate
{
    internal MaintenanceSnapshotCandidate(string subject, string relativePath, DateTimeOffset candidateTimestamp,
        string pairIdentity, bool eligible, MaintenanceProtectionReason? protection)
    {
        Subject = MaintenanceContractValidation.RequireSafeText(subject, nameof(subject));
        RelativePath = MaintenanceContractValidation.RequireRelativePath(relativePath, nameof(relativePath));
        CandidateTimestamp = candidateTimestamp;
        PairIdentity = MaintenanceContractValidation.RequireSafeText(pairIdentity, nameof(pairIdentity));
        Eligible = eligible;
        Protection = protection;
    }

    public string Subject { get; }
    public string RelativePath { get; }
    public DateTimeOffset CandidateTimestamp { get; }
    public string PairIdentity { get; }
    public bool Eligible { get; }
    public MaintenanceProtectionReason? Protection { get; }
}

internal static class MaintenanceContractValidation
{
    internal static string RequireSafeText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("Value must be nonblank and contain no control characters.", parameterName);

        return value;
    }

    internal static string RequireRelativePath(string value, string parameterName)
    {
        RequireSafeText(value, parameterName);
        if (Path.IsPathRooted(value) || Uri.TryCreate(value, UriKind.Absolute, out _) ||
            value.Contains('\\') || value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Path must be a safe relative non-URI path.", parameterName);

        return value;
    }

    internal static IReadOnlyList<string> CopyScopes(IReadOnlyList<string> values)
    {
        if (values.Count is < 1 or > 128)
            throw new ArgumentException("Scopes must contain between 1 and 128 values.", nameof(values));
        var copy = values.Select(value => RequireSafeText(value, nameof(values))).ToArray();
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Scopes must not contain duplicates.", nameof(values));
        return Array.AsReadOnly(copy);
    }

    internal static IReadOnlySet<string> CopyRelativePathSet(IReadOnlySet<string> values)
    {
        if (values.Count > 4096)
            throw new ArgumentException("Protection paths cannot exceed 4096 values.", nameof(values));
        return values.Select(value => RequireRelativePath(value, nameof(values)))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    internal static string RequireToken(string value, string parameterName)
    {
        RequireSafeText(value, parameterName);
        if (value.Length > 32 * 1024 || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("Continuation token must be nonblank, whitespace-free, and at most 32 KiB.", parameterName);
        return value;
    }

    internal static IReadOnlyList<MaintenanceCandidateResult> CopyCandidates(IReadOnlyList<MaintenanceCandidateResult> values) =>
        new ReadOnlyCollection<MaintenanceCandidateResult>(values.Select(value => value ?? throw new ArgumentException("Candidates cannot contain null elements.", nameof(values))).ToArray());

    internal static IReadOnlyList<MaintenanceScopeResult> CopyScopesResults(IReadOnlyList<MaintenanceScopeResult> values) =>
        new ReadOnlyCollection<MaintenanceScopeResult>(values.Select(value => value ?? throw new ArgumentException("Scopes cannot contain null elements.", nameof(values))).ToArray());

    internal static IReadOnlyList<string> CopyStrings(IReadOnlyList<string> values, string parameterName) =>
        new ReadOnlyCollection<string>(values.Select(value => RequireSafeText(value, parameterName)).ToArray());
}
