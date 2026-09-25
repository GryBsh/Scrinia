# P22 — Committed-Hash Proof Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p22-committed-hash-proof-seam.md`
**Phase:** P22 (twenty-first god-store seam extraction; append recovery-orchestration phase).
**Shape:** add one leaf to the existing `AppendRecoveryVerifier` owner (P21) + two store edits. Single wave, sequential.

## Workstreams

- W1 — add `HasCommittedHash` to `AppendRecoveryVerifier` (P21 owner).
- W2 — remove the `HasCommittedHash` body from `FileMemoryStore.cs`.
- W3 — repoint the single call site in `RecoverAppendUnsafeAsync`.
- W4 — build + seam test verification.
- W5 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Extend `AppendRecoveryVerifier`

Edit `src/Scrinia.Core/AppendRecoveryVerifier.cs` (created P21). Append the `HasCommittedHash` leaf (verbatim body, no injected hooks):

```csharp
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
```

## W2 — Remove the `HasCommittedHash` body

Delete the `HasCommittedHash` method body from `src/Scrinia.Core/FileMemoryStore.cs` (lines :3328–:3348).

## W3 — Repoint the call site

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. In `RecoverAppendUnsafeAsync` (:3204–:3207), repoint the call site:

```csharp
if (currentMembers && !AppendRecoveryVerifier.HasCommittedHash(transaction, journal.CommittedHash, ct))
```

## W4 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRecoveryVerifier|FullyQualifiedName~HasCommittedHash"
   ```
   Confirm `Passed!` / 0 failed.

## W5 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendRecoveryVerifier` owns the committed-hash proof; `FileMemoryStore` retains only the repointed call site within `RecoverAppendUnsafeAsync`.
- No new public API; `IMemoryStore` surface unchanged.
- Full suite green.
