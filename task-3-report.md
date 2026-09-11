# B2 Task 3 — Discovery and deterministic precedence

## Changes

- `src/Scrinia.Core/RecoveryDiscovery.cs` now performs scope-local, bounded discovery at the preserved restore, append, and prescribed `agent`/`skills` entity locations; it rejects reparse paths before reading, normalizes legacy evidence, sorts restore → append → file entity with ordinal subject/operation ordering, detects duplicate claims, and returns typed blocking results.
- `src/Scrinia.Core/RecoveryEnvelope.cs` now validates and canonicalizes every file-entity old, committed, and optional archive member path at admission. It accepts publisher-generated absolute paths only after canonicalization and direct-child containment checks, while rejecting absolute escapes, traversal, wrong-entity, wrong-kind, and out-of-directory paths through the typed malformed/unsafe boundary.
- `src/Scrinia.Core/FileMemoryStore.cs` invokes discovery and typed admission from the exclusive recovery boundary and both synchronous/asynchronous read barriers. Validated non-restore candidates remain blocked with `Inconsistent` until their operation-specific replay boundary is supplied by later adapter work.
- `src/Scrinia.Core/RecoveryContracts.cs`, `RecoveryEnvelope.cs`, and `RestoreJournal.cs` preserve typed admission/discovery outcomes and carry discovery blocks through the existing restore exception contract without changing public command/tool names.
- `tests/Scrinia.Tests/RecoveryDiscoveryTests.cs` covers preserved locations, deterministic precedence, admission, malformed/newer/wrong-scope evidence with recoverable IDs, reserved-directory exclusion, duplicate claims, filename/operation mismatch, injected enumeration failure, and sibling-scope isolation.

## Verification

- Focused absolute-path compatibility regression: **3 passed, 0 failed** (`FileEntityAdapterAcceptsPublisherShapedAbsolutePaths` prepared/committed cases and `FileEntityAdapterRejectsAbsolutePathOutsideEntityDirectory`).
- Focused Task 3 plus B1 regression lane: **71 passed, 0 failed** (`RecoveryDiscoveryTests`, `JournaledRestoreRecoveryTests`, and `CliRecoveryBlockedRouteTests`).
- `dotnet build tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore`: **0 errors, 0 warnings**.
- `git diff --check`: **passed**.

## Deferred matrix items

- Explicit filesystem reparse-point fixture coverage remains deferred because the deterministic test seam can inject enumeration failures and ordering without safely creating platform-dependent links; the production path rejects reparse evidence before bounded reads. Operation-specific append/file-entity replay remains deferred to later B2 tasks.
- No Task 4 adapter replay or unrelated worktree file was changed.
