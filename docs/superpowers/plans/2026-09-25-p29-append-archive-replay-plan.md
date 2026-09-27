# P29 — Append-Archive-Replay Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p29-append-archive-replay-seam.md`
**Phase:** P29 (twenty-ninth god-store seam extraction; append recovery-orchestration phase).
**Shape:** new owner + field + constructor wiring + two method deletions + four call-site repoints. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/AppendArchiveReplayer.cs` with the instance owner (`WriteArchiveMember` + `ReplayArchiveMembers`).
- W2 — declare the `_archiveReplayer` field in `FileMemoryStore.cs`.
- W3 — construct `_archiveReplayer` in the store constructor.
- W4 — delete `WriteArchiveMember` and `ReplayArchiveMembers` from `FileMemoryStore.cs`.
- W5 — repoint the four call sites (`WriteArchiveMember` ×2 in `PublishFrozenArchiveAsync`, `ReplayArchiveMembers` ×1 in `RecoverAppendUnsafeAsync`).
- W6 — build + seam test verification.
- W7 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 → W6 → W7 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `AppendArchiveReplayer`

Create `src/Scrinia.Core/AppendArchiveReplayer.cs` (new file) with the verbatim instance owner class from the parent spec (the `WriteArchiveMember` durable-write leaf + `ReplayArchiveMembers` recovery replay conductor; `_injectAppendFault` hook wired to the store's static `InjectAppendFault`; `DurableFile.AtomicWriteAllBytesForAppend(...)` kept verbatim; `_appendJournalWriter` reference held as a referenced seam owner).

## W2 — Declare the field

Edit `src/Scrinia.Core/FileMemoryStore.cs`. After the `_appendJournalWriter` field declaration (:155), declare:

```csharp
    private readonly AppendArchiveReplayer _archiveReplayer;
```

## W3 — Construct in the store constructor

Edit `src/Scrinia.Core/FileMemoryStore.cs`. After the `_appendJournalWriter` construction block (:319–:321), add:

```csharp
        _archiveReplayer = new AppendArchiveReplayer(
            InjectAppendFault, _appendJournalWriter);
```

## W4 — Delete the methods

Delete `WriteArchiveMember` (:2981–:3036) and `ReplayArchiveMembers` (:3259–:3284) from `src/Scrinia.Core/FileMemoryStore.cs`.

## W5 — Repoint the call sites

Four edits in `src/Scrinia.Core/FileMemoryStore.cs`:
- `PublishFrozenArchiveAsync` :2966 `WriteArchiveMember("artifact", ...)` → `_archiveReplayer.WriteArchiveMember("artifact", ...)`
- `PublishFrozenArchiveAsync` :2973 `WriteArchiveMember("sidecar", ...)` → `_archiveReplayer.WriteArchiveMember("sidecar", ...)`
- `RecoverAppendUnsafeAsync` :3189 `ReplayArchiveMembers(transaction, journalPath, ct)` → `_archiveReplayer.ReplayArchiveMembers(transaction, journalPath, ct)`

Confirm via grep that no remaining bare `WriteArchiveMember(` / `ReplayArchiveMembers(` call site is left in the store after the edit.

## W6 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendArchiveReplayer"
   ```
   Confirm `Passed!` / 0 failed. (VSTest `--filter` does not accept `|`; if a two-sided filter is used, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"` instead.)

## W7 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendArchiveReplayer` owns the durable archive-write leaf + recovery replay conductor; `FileMemoryStore` retains only the repointed call sites within `PublishFrozenArchiveAsync` and `RecoverAppendUnsafeAsync`.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner; `DurableFile` unchanged.
- Full suite green.
