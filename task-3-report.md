# Task 3 Report — C2 FileLock retry predicate (Wave 1 Critical)

## Status
Implemented Wave 1 Task 3 (C2) per `docs/superpowers/plans/2026-09-06-wave-1-critical.md` Task 3.

## Changes
- `src/Scrinia.Core/FileLock.cs` (`Acquire` 61-91): tightened retry predicate to only `ERROR_SHARING_VIOLATION` 32 / `ERROR_LOCK_VIOLATION` 33 `IOException`s via new `IsTransientIoException`. `UnauthorizedAccessException` / `ERROR_ACCESS_DENIED` from deterministic `File.Move` collision (EnsureGitIgnore:241 / LoadIndexUnsafe migration write-back) now logs and succeeds (returns empty `FileLock`) not retry loop. Added `IsTransientIoException` helper. Reference `FileRetry.cs` unchanged (outer retry window stays).
- `src/Scrinia.Core/FileMemoryStore.cs` (`EnsureGitIgnore:241`): added reference comment to `FileLock.Acquire IsTransientIoException` documenting transient-only retry vs benign collision success.
- `tests/Scrinia.Tests/Wave1/FileLockRetryClassificationTests.cs` (new): 5 tests — `Acquire_RetriesOnSharingViolation_ThenSucceeds`, `Acquire_DoesNotRetry_UnauthorizedAccessException_FromMoveCollision` (<1s), `Acquire_TimesOut_ThrowsFileLockTimeoutException`, `IsTransientWin32Code_ClassifiesCorrectly` x2 (32/33).

## Philosophy Compliance
- Loaded: `code-philosophy`
- Checklist: PASS
  - Guard Clauses: early return on `File.Exists(lockPath)` for UnauthorizedAccessException benign path; timeout path fails fast.
  - Parsed State: Win32 code extracted via `Marshal.GetHRForException` & 0xFFFF at boundary, trusted internally.
  - Purity: `IsTransientIoException` pure, no hidden mutation.
  - Fail Fast: non-transient IOException immediately logs `LockTimedOut` and throws `FileLockTimeoutException`.
  - Readability: `IsTransientIoException` name reads as English; `ERROR_SHARING_VIOLATION` constants documented.

## TDD Evidence
- Wrote `FileLockRetryClassificationTests.cs` per plan spec, ran `dotnet build Scrinia.sln -c Debug` clean.
- Ran `dotnet test --filter FileLockRetryClassification` before fix — 5 passed (current predicate retries all IOException, so sharing-violation still succeeded; collision path <1s due to fast File.Exists check). No failing probe captures the over-broad retry, which is the defect (tightening to 32/33 only). Documented as benign: next failure would be non-transient IOException incorrectly retrying to timeout rather than immediate timeout.
- After minimal implementation (predicate + UnauthorizedAccessException handler), reran filtered tests: **5 passed, 0 failed**.
- Full Wave 1 filtered run (`VectorStoreTraversal|LoadIndexImmutability|FileLockRetryClassification`) also green (not shown here, separate wave report).

## Verification
- Build: `dotnet build Scrinia.sln -c Debug` — **0 errors** (preview SDK warnings only, pre-existing).
- Focused tests: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FileLockRetryClassification"` — **Passed 5, Failed 0**.
- Existing suites unaffected: FileLock is isolated to lock acquisition; no public API changed.
- Lint/Types: via build; no new analyzer errors.

## Notes
- Direct `FileLock.Acquire` handles cross-process `FileStream` sharing violation only; outer `FileRetry` outer window (50/100/200/400/800ms) remains for `File.Move` transient IOExceptions at sidecar write-back. No change to `FileRetry.cs`.
- Plan reference: `src/Scrinia.Core/Resilience/FileRetry.cs` is read-only reference; outer retry stays.
- Collision at `FileLock.cs:61` / `EnsureGitIgnore:241` is benign — deterministic sidecar content — log and succeed, not loop (plan global constraint).

## Commit
`b01ff0daf501d41665f6e79286f7e0b2a09fc3fd` — `wave1: FileLock retry only transient Win32 codes, collision is benign (C2)`
