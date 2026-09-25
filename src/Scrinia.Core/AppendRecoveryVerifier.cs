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
}
