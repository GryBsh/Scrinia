# P30 — Append-Commit Prune Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p30-append-prune-seam.md`
**Phase:** P30 (thirtieth god-store seam extraction; append recovery-orchestration phase).
**Shape:** hoist `PruneCommittedArchives` + `ArchiveCleanupResult` into `ArchiveService` (add `_injectAppendFault` hook), wire the hook, delete two methods from the store, repoint three call sites. Single wave, sequential.

## Workstreams

- W1 — add `_injectAppendFault` hook to `ArchiveService` (field + constructor param).
- W2 — move `ArchiveCleanupResult` record verbatim into `ArchiveService.cs`.
- W3 — move `PruneCommittedArchives` verbatim into `ArchiveService` as a public instance method; replace both `InjectAppendFault(...)` calls with `_injectAppendFault(...)`.
- W4 — update the store's `ArchiveService` construction to pass `InjectAppendFault` (positional slot after the four existing hooks); delete `PruneCommittedArchives` + `ArchiveCleanupResult` from the store.
- W5 — repoint the three call sites (`AppendAsyncCore`, `PruneCommittedArchivesForTest`, `RecoverAppendUnsafeAsync`).
- W6 — build + seam test verification.
- W7 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 → W6 → W7 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add the hook to ArchiveService

Edit `src/Scrinia.Core/ArchiveService.cs`. Add after the existing four `private readonly Func<>` hook fields:

```csharp
    private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;
```

Add the constructor param `Func<AppendFaultPoint, string, string, Exception?> injectAppendFault` in the correct positional slot (after `_metaPath`) and assign it.

## W2 — Move ArchiveCleanupResult

Copy the `private sealed record ArchiveCleanupResult(bool Failed, string[] Failures)` block (with its `Detail` property) verbatim from `src/Scrinia.Core/FileMemoryStore.cs` into `src/Scrinia.Core/ArchiveService.cs` as a top-level record in the same file (after the class close brace). Change `private sealed` to `internal sealed`.

## W3 — Move PruneCommittedArchives

Copy the `PruneCommittedArchives` method body verbatim from `src/Scrinia.Core/FileMemoryStore.cs` into `src/Scrinia.Core/ArchiveService.cs` as a `public` instance method (change `private static` → `public`). Replace both `InjectAppendFault(` calls with `_injectAppendFault(`.

## W4 — Delete from store + wire hook

Delete `ArchiveCleanupResult` (:2985–:2990) and `PruneCommittedArchives` (:2992–:3048) from `src/Scrinia.Core/FileMemoryStore.cs`. Locate the `_archiveService = new ArchiveService(...)` construction and append the `InjectAppendFault` argument in the positional slot matching W1.

## W5 — Repoint call sites

Three edits in `src/Scrinia.Core/FileMemoryStore.cs`:
- `AppendAsyncCore` :2854 → `_archiveService.PruneCommittedArchives(transaction, ct)`
- `PruneCommittedArchivesForTest` :2929 → `_archiveService.PruneCommittedArchives(transaction, CancellationToken.None).Failures`
- `RecoverAppendUnsafeAsync` :3164 → `_archiveService.PruneCommittedArchives(transaction, ct)`

Confirm via grep that no remaining bare `PruneCommittedArchives(` call site is left in the store after the edit.

## W6 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append"`.
   Confirm `Passed!` / 0 failed.

## W7 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `ArchiveService` owns `PruneCommittedArchives` + `ArchiveCleanupResult`; `FileMemoryStore` retains only the repointed call sites.
- No new public API on the `IMemoryStore` surface; `ArchiveVersionUnsafe` unchanged.
- No `FileMemoryStore` instance state referenced by the owner.
- Full suite green.
