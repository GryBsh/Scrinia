# P2 Write-Path Unification — Execution Plan

**Date:** 2026-09-24
**Phase:** P2 — Write-Path Unification
**Repository:** scrinia (`main`)
**Scope:** `src/Scrinia.Core/FileMemoryStore.cs` — **this is the only file changed.**
**Design record:** `docs/superpowers/specs/2026-09-24-p2-write-path-unification-design.md` (committed to `main`).

> This plan is gitignored. It is the work order for the coder; it is **not** committed. Only the
> spec is committed to `main`. Do **not** touch `src/` tests, `docs/` beyond the spec, or anything
> outside `FileMemoryStore.cs`.

---

## Execution model: one clean pass

This is a single linear refactor — do **not** chunk it into multiple branches. Three tasks, each
ending on a green build or test gate, then one commit.

```
Task 1: extract shared helpers   → build src/Scrinia.Core only (0 errors, no callers yet)
Task 2: split the dispatch        → build Scrinia.sln (0 errors, grep-verify StoreAsyncCore gone)
Task 3: verify + commit           → dotnet test Scrinia.sln fully green
```

### Guardrails for the whole pass

- **Comment preservation is a hard requirement.** Every invariant comment on the precompute block,
  the ephemeral block, `StorePersistentAsync`, and the non-null branch moves with its code verbatim.
  Do not drop, relocate-and-forget, or paraphrase any of them.
- **Do NOT touch** `RunUnderAppendLock`, `WriteArtifactAsync`'s append-fault-point fork,
  `UpsertCore` / `Upsert` / `UpsertForAppend`, `BuildDerivedEntry`, `PersistEphemeral` (its
  signature), `MemoryDerivation`, `AppendTransaction` / `WholeMemoryTransaction`, the recovery
  methods, the index cache, or any non-write-path code.
- **Validation split stays put.** Plain path validates in `StorePersistentOrEphemeralAsync`
  (`ValidateOrThrow(_writeValidationOptions)`, multi-chunk); append path validates in `AppendAsync`
  (`ValidateOrThrow(_writeValidationOptions, isAppend: true)`, one chunk). `CreateForAppendAsync`
  performs **no** validation.
- **No new public API.** All four new methods are `private`.
- **Append ordering preserved.** `PublishLiveAppendAsync` (`FileMemoryStore.cs:3360`) untouched;
  `CreateForAppendAsync` mirrors the existing create-before-journal ordering.

---

## Task 1 — Extract shared helpers

**Goal:** introduce the two primitives the paths share, with **no callers wired yet.**

1. Add `PrecomputedStore` private record near the write surface:

   ```csharp
   private sealed record PrecomputedStore(
       DerivedMetadata Derived, string Artifact, int ChunkCount, long OriginalBytes);
   ```

2. Add `PrecomputeForStore(MemoryWrite write, bool isEphemeral) → PrecomputedStore`. Move the
   `:2390–2399` block verbatim, preserving the inline comments (D-01 raw keywords, chunk-encoding
   rule). Forward `isEphemeral` to `MemoryDerivation.ForStore(..., isEphemeral: ...)`.

3. Add `PersistEphemeralAsync(MemoryWrite write, CancellationToken ct) → ArtifactEntry` exactly as
   the spec's step 2 (strip prefix → `GetEphemeral` → `PrecomputeForStore(write, isEphemeral: true)`
   → `PersistEphemeral(...)` with the create-vs-update `createdAt`/`updatedAt`).

**Build:**

```bash
dotnet build src/Scrinia.Core/Scrinia.Core.csproj
```

**Gate:** 0 errors. The new methods are unused at this point — the compiler must not complain about
dead code (private methods that are not referenced are allowed). If dead-code analysis is enforced,
this task is still valid because the methods exist for Task 2; confirm the build is clean and move on.

---

## Task 2 — Split the dispatch

**Goal:** replace `StoreAsyncCore` with two independent entry points and rewire the four callers.

Do these in order, keeping the file compiling after each sub-step:

1. **`StorePersistentAsync` — swap the signature.** Change from
   `(MemoryWrite write, string artifact, long originalBytes, int chunkCount, DerivedMetadata derived, CancellationToken, Action<WholeMemoryResult>?)`
   to
   `(MemoryWrite write, CancellationToken cancellationToken, Action<WholeMemoryResult>? transactionResult = null)`.
   At the top insert `PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: false);` and
   replace every use of the removed params (`artifact`, `originalBytes`, `chunkCount`, `derived`)
   with `pre.Artifact`, `pre.OriginalBytes`, `pre.ChunkCount`, `pre.Derived`. Leave the rest of the
   body byte-identical.

2. **`CreateForAppendAsync` — lift the non-null branch.** Copy the verbatim `:2414–2586` block into
   a new `private async Task<ArtifactEntry> CreateForAppendAsync(MemoryWrite write, string
   appendOperationId, CancellationToken ct)`. At the top insert `PrecomputedStore pre =
   PrecomputeForStore(write, isEphemeral: false);` and introduce `derived`, `artifact`, `chunkCount`,
   `originalBytes` from `pre`. **Keep every comment verbatim.** Do not add `ValidateOrThrow`; do not
   handle ephemeral.

3. **Replace `StoreAsyncCore` with `StorePersistentOrEphemeralAsync`** exactly as the spec's step 5:
   plain `ValidateOrThrow`, ephemeral → `PersistEphemeralAsync`, else → `StorePersistentAsync`. The
   appendOperationId annotation catch (was `:2378–2382`) is dead and removed.

4. **Rewire the four callers:**
   - `StoreAsync` (`:2357`): `=> await StorePersistentOrEphemeralAsync(write, ct);`
   - `StoreWithTransactionResultAsync` (`:2360`): `entry = await StorePersistentOrEphemeralAsync(write, ct, result => transaction = result);`
   - `AppendAsync` ephemeral-create (`:2786`): `ArtifactEntry created = await PersistEphemeralAsync(write, ct);` (keep the `if (GetEphemeral(...) is null)` guard).
   - `AppendAsync` persistent-orphan-create (`:2812`): `ArtifactEntry created = await CreateForAppendAsync(write, operationId, ct);` (keep the `catch (AppendPostCommitCleanupException …)` → `SuccessWithWarning` wrapper).

5. **Delete `StoreAsyncCore`** entirely once the four callers are rewired.

**Build:**

```bash
dotnet build Scrinia.sln
```

**Gate:** 0 errors. Then confirm no references to `StoreAsyncCore` remain:

```bash
grep -rn "StoreAsyncCore" src/
```

Expected result: **no matches.** If any reference survives, the file does not compile — fix before
proceeding.

---

## Task 3 — Verify + commit

**Test gate:**

```bash
dotnet test Scrinia.sln --nologo
```

**Gate:** fully green, 3750+ tests. **Any failure is a behavior regression — do not sweep it under
the rug.** Walk the failing family against the testing matrix in the spec; the refactor is
behavior-preserving, so a red test means the comment/code move drifted from the original ordering,
the signature swap leaked a param, or a nested fork was touched.

**Commit (the FileMemoryStore.cs change only):**

```bash
git commit -am "core: unify write path by splitting the StoreAsyncCore flag-fork" -m "StoreAsyncCore's appendOperationId null-flag coupled ~10 downstream decisions (exception annotation, index-load strategy, orphan reset, journaling, locking, AppendFaultPoint vs WholeMemoryFaultPoint injection, AppendTransaction vs WholeMemoryTransaction). Split into StorePersistentAsync (whole-memory, raw write) and CreateForAppendAsync (append-create, raw write), sharing PrecomputeForStore + PersistEphemeralAsync. StorePersistentOrEphemeralAsync is the new plain-surface entry; StoreAsyncCore is deleted. Nested model-intrinsic forks (RunUnderAppendLock, WriteArtifactAsync append-fault-point, UpsertCore) are unchanged. Behavior-preserving; full suite green."
```

---

## Report back

After Task 3, report:

1. **Build** — Task 1 (`Scrinia.Core`) pass/fail, Task 2 (`Scrinia.sln`) pass/fail.
2. **Tests** — total count per project (or at least the solution total) and whether the suite is green.
3. **Commit hash** — the SHA of the `core:` commit from Task 3.
4. **Scope check** — confirm `FileMemoryStore.cs` was the only changed file (`git status` /
   `git diff --stat` should show exactly that path).
