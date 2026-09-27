namespace Scrinia.Core;

/// <summary>
/// Owns the single-append-member recovery verification moved off <see cref="FileMemoryStore"/> in
/// P28. Holds no <see cref="FileMemoryStore"/> instance — only the injected append-fault hook the
/// store exposes. Every other dependency is a frozen seam owner (<see cref="AppendRecoveryVerifier"/>)
/// or a frozen recovery primitive (<see cref="RecoveryEvidenceVerifier"/>, <see cref="RecoveryScopeBinding"/>).
/// No durable journal-state transition and no rollback bookkeeping, so the owner stays free of the
/// recovery conductors that remain resident in the store.
/// </summary>
internal sealed class AppendMemberVerifier
{
    private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;

    public AppendMemberVerifier(Func<AppendFaultPoint, string, string, Exception?> injectAppendFault)
    {
        _injectAppendFault = injectAppendFault;
    }

    /// <summary>Prove a freshly published append member matches its recorded hash, injecting the
    /// <c>VerificationRead</c> fault point before the recovery verification. Pure verification leaf:
    /// no journal-state transition, no rollback bookkeeping.</summary>
    public async Task VerifyAsync(
        RawMemberSnapshot expected, string operationId, CancellationToken cancellationToken)
    {
        Exception? failure = _injectAppendFault(
            AppendFaultPoint.VerificationRead, operationId, expected.Path);
        if (failure is not null)
            throw failure;
        RecoveryVerificationResult verification = await RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(expected.Path)!),
            RecoveryEvidenceKind.AppendJournal, expected.Path,
            new RecoveryExpectedHash(expected.Sha256), MaintenanceLimits.Hard, cancellationToken)
            .ConfigureAwait(false);
        verification.SourceContent?.Dispose();
        if (verification.Outcome != RecoveryVerificationOutcome.Verified)
            throw new IOException($"Append member verification failed after publication: {expected.Path}");
    }
}
