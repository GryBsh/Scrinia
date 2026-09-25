# P21 — Append-Member Verification Predicates Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p21-append-verification-predicates-seam.md`
**Phase:** P21 (twenty-first god-store seam extraction; append recovery-orchestration phase).
**Shape:** one new static owner (`AppendRecoveryVerifier`) + four store edits. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/AppendRecoveryVerifier.cs` with the three pure verification predicates.
- W2 — remove the three predicate bodies from `FileMemoryStore.cs`.
- W3 — repoint the 6 call sites (`MatchesAppendMember` ×5 across :3326/:3464/:3473, `MatchesArchiveBytes` ×2 across :3427/:3431, `MatchesAppendState` ×2 across :3369).
- W4 — build + seam test verification.
- W5 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `AppendRecoveryVerifier`

Create `src/Scrinia.Core/AppendRecoveryVerifier.cs` (new file) with the verbatim static owner class from the parent spec (the three pure predicates `MatchesAppendMember`, `MatchesArchiveBytes`, `MatchesAppendState`; all frozen primitives).

## W2 — Remove the predicate bodies

Delete the three predicate method bodies from `src/Scrinia.Core/FileMemoryStore.cs`:
- `MatchesAppendMember` (:3350–:3360)
- `MatchesArchiveBytes` (:3446–:3455)
- `MatchesAppendState` (:3487–:3488)

Leave `RecoveryRequired` (:3490) resident.

## W3 — Repoint the call sites

Six edits in `src/Scrinia.Core/FileMemoryStore.cs`, each a one-line call-site replacement:

- `MatchesAppendMember` → `AppendRecoveryVerifier.MatchesAppendMember`
  - :3326 (`AreAppendMembersCurrent`, 2 calls)
  - :3464 (`RestoreAppendMember`, 2 calls)
  - :3473 (`RestoreAppendMember`, 1 call)
- `MatchesArchiveBytes` → `AppendRecoveryVerifier.MatchesArchiveBytes`
  - :3427 (`ReconcileArchiveMember`, 1 call)
  - :3431 (`ReconcileArchiveMember`, 1 call)
- `MatchesAppendState` → `AppendRecoveryVerifier.MatchesAppendState`
  - :3369 (`TryRestoreAppendTransaction`, 2 calls)

Confirm via grep that no remaining bare `MatchesAppendMember(` / `MatchesArchiveBytes(` / `MatchesAppendState(` call site is left in the file after these edits.

## W4 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRecoveryVerifier|FullyQualifiedName~MatchesAppendMember|FullyQualifiedName~MatchesArchiveBytes|FullyQualifiedName~MatchesAppendState"
   ```
   Confirm `Passed!` / 0 failed.

## W5 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendRecoveryVerifier` owns the three pure verification predicates; `FileMemoryStore` retains only the repointed call sites within resident recovery-orchestration methods.
- No new public API; `IMemoryStore` surface unchanged.
- Full suite green.
