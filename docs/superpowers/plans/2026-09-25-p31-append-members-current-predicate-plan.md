# P31 — Append-Members-Current Predicate Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p31-append-members-current-predicate.md`
**Phase:** P31 (thirty-first god-store seam extraction; append recovery-orchestration phase).
**Shape:** move the static predicate `AreAppendMembersCurrent` into `AppendRecoveryVerifier`; delete it from the store; repoint two call sites. Single wave, sequential.

## Workstreams

- W1 — add `AreAppendMembersCurrent` to `src/Scrinia.Core/AppendRecoveryVerifier.cs`.
- W2 — delete `AreAppendMembersCurrent` from `src/Scrinia.Core/FileMemoryStore.cs`.
- W3 — repoint the two call sites in `RecoverAppendUnsafeAsync`.
- W4 — build + seam test verification.
- W5 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add AreAppendMembersCurrent to AppendRecoveryVerifier

Edit `src/Scrinia.Core/AppendRecoveryVerifier.cs`. Add after `MatchesAppendState` and before `HasCommittedHash`:

```csharp
    /// <summary>Prove that a recovered append's new artifact + sidecar members are both present and
    /// verified. Pure predicate: no fault injection, no durable write, no rollback bookkeeping.</summary>
    public static bool AreAppendMembersCurrent(AppendTransaction transaction, CancellationToken cancellationToken = default) =>
        MatchesAppendMember(transaction.NewArtifact, cancellationToken) &&
        MatchesAppendMember(transaction.NewSidecar, cancellationToken);
```

Note the body calls `MatchesAppendMember` directly (same class), and the `cancellationToken` parameter is retained verbatim to preserve the token's flow to `VerifySingleAsync`.

## W2 — Delete from the store

Delete `AreAppendMembersCurrent` (the two-line static predicate) from `src/Scrinia.Core/FileMemoryStore.cs`.

## W3 — Repoint call sites

Two edits in `src/Scrinia.Core/FileMemoryStore.cs`, both inside `RecoverAppendUnsafeAsync`:
- :3084 `AreAppendMembersCurrent(transaction, ct)` → `AppendRecoveryVerifier.AreAppendMembersCurrent(transaction, ct)`
- :3093 `AreAppendMembersCurrent(transaction, ct)` → `AppendRecoveryVerifier.AreAppendMembersCurrent(transaction, ct)`

Confirm via grep that no remaining bare `AreAppendMembersCurrent(` call site is left in the store after the edit.

## W4 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append"`.
   Confirm `Passed!` / 0 failed.

## W5 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendRecoveryVerifier` owns `AreAppendMembersCurrent`; `FileMemoryStore` retains only the two repointed call sites within `RecoverAppendUnsafeAsync`.
- No new public API on the `IMemoryStore` surface.
- No `FileMemoryStore` instance state referenced by the moved predicate.
- Full suite green.
