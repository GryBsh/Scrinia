using System.Security.Cryptography;

namespace Scrinia.Core;

/// <summary>
/// Owns the pure append-member verification predicates moved off <see cref="FileMemoryStore"/> in
/// P21. Holds no <see cref="FileMemoryStore"/> instance — no fault injection, no durable write, no
/// rollback bookkeeping. These predicates verify append member integrity against expected SHA-256
/// hashes (via the frozen <see cref="RecoveryEvidenceVerifier"/>) for recovery/rollback decisions.
/// </summary>
internal static class AppendRecoveryVerifier
{
    public static bool MatchesAppendMember(RawMemberSnapshot expected, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(expected.Path))
            return false;
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(expected.Path)!), RecoveryEvidenceKind.AppendJournal,
            expected.Path, new RecoveryExpectedHash(expected.Sha256), MaintenanceLimits.Hard,
             cancellationToken).AsTask().GetAwaiter().GetResult();
        verification.SourceContent?.Dispose();
        return verification.Outcome == RecoveryVerificationOutcome.Verified;
    }

    public static bool MatchesArchiveBytes(string path, RawMemberSnapshot expected,
        CancellationToken cancellationToken = default)
    {
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(path)!), RecoveryEvidenceKind.AppendJournal,
            path, new RecoveryExpectedHash(expected.Sha256), MaintenanceLimits.Hard,
            cancellationToken).AsTask().GetAwaiter().GetResult();
        verification.SourceContent?.Dispose();
        return verification.Outcome == RecoveryVerificationOutcome.Verified;
    }

    public static bool MatchesAppendState(RawMemberSnapshot expected) =>
        expected.Existed ? MatchesAppendMember(expected) : !File.Exists(expected.Path);

    /// <summary>Prove that a recovered append's new artifact + sidecar members are both present and
    /// verified. Pure predicate: no fault injection, no durable write, no rollback bookkeeping.</summary>
    public static bool AreAppendMembersCurrent(AppendTransaction transaction, CancellationToken cancellationToken = default) =>
        MatchesAppendMember(transaction.NewArtifact, cancellationToken) &&
        MatchesAppendMember(transaction.NewSidecar, cancellationToken);

    /// <summary>Prove that a committed append's live artifact + sidecar bytes, recovered through the
    /// frozen verifier, hash-combine to the journal's recorded committed hash. Pure committed-integrity
    /// proof: no fault injection, no durable write, no rollback.</summary>
    public static bool HasCommittedHash(AppendTransaction transaction, string? committedHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(committedHash))
            return false;

        RecoveryVerificationResult artifact = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(transaction.NewArtifact.Path)!),
            RecoveryEvidenceKind.AppendJournal, transaction.NewArtifact.Path,
            new RecoveryExpectedHash(transaction.NewArtifact.Sha256), MaintenanceLimits.Hard,
            cancellationToken).AsTask().GetAwaiter().GetResult();
        RecoveryVerificationResult sidecar = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(transaction.NewSidecar.Path)!),
            RecoveryEvidenceKind.AppendJournal, transaction.NewSidecar.Path,
            new RecoveryExpectedHash(transaction.NewSidecar.Sha256), MaintenanceLimits.Hard,
            cancellationToken).AsTask().GetAwaiter().GetResult();
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(
            (artifact.SourceContent?.Bytes.ToArray() ?? []).Concat(sidecar.SourceContent?.Bytes.ToArray() ?? []).ToArray()));
        artifact.SourceContent?.Dispose(); sidecar.SourceContent?.Dispose();
        return string.Equals(actualHash, committedHash, StringComparison.Ordinal);
    }
}
