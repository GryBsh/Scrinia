# P2 Write-Path Unification — Design Spec

**Date:** 2026-09-24
**Phase:** P2 — Write-Path Unification
**Owner:** (implementation handoff — coder)
**Status:** Approved for execution under the companion plan.

> **Where this lives:** `docs/superpowers/specs/`. This file is committed to `main`; it is the
> design record, not the change record. The executable plan lives beside it under
> `docs/superpowers/plans/` (gitignored) and is never committed.

---

## Context

### The problem

`StoreAsyncCore` (`src/Scrinia.Core/FileMemoryStore.cs:2369`) is a single flag-forked entry point.
Its `appendOperationId` null-flag threads roughly ten downstream decisions across one method:

| # | Decision driven by `appendOperationId != null` | Current route (null) | Current route (non-null) |
|---|---|---|---|
| 1 | Exception annotation | plain `InvalidMemoryWriteException` | `AppendOperationId` attached (lines :2378–2382) |
| 2 | Route selection | `StorePersistentAsync` (whole-memory coordinator) | append-create branch |
| 3 | Index-load strategy | `LoadIndexUnderExclusiveAppendLock` → `WithExclusiveLock` | live append lock (lock-free) |
| 4 | Orphan reset | n/a (whole-memory) | orphan is forced to create (:2441–2443) |
| 5 | Transaction type | `WholeMemoryTransaction` + `ArchivePairPlan` | `AppendTransaction` + `ArchivePlan` |
| 6 | Derivation / capture | `CaptureWholeMemberAsync` + `WholeMemoryFaultPoint` | `CaptureRawMemberAsync` + `AppendFaultPoint` |
| 7 | Locking | `RunUnderAppendLock(null)` → `WithExclusiveLock` | `RunUnderAppendLock(id)` → lock-free |
| 8 | Journaling | archive publication (`_wholeMemoryTransactions`) | `PersistAppendJournal` / `VerifyAppendMemberAsync` / `TryRestoreAppendTransaction` |
| 9 | Fault injection | `WholeMemoryFaultPoint` | `AppendFaultPoint` |
| 10 | Rollback | compensation via coordinator | create-rollback restore-or-delete (:2531–2583) |

It literally builds two different transaction types behind one flag: `WholeMemoryTransaction` +
`ArchivePairPlan` (null) vs `AppendTransaction` + `ArchivePlan` (non-null). The flag is not a
parameter — it is the de facto control flow of the entire write surface, and every one of those ten
decisions is a `when (appendOperationId is null)` / `is not null` split.

### Why this is fragile

The two paths are already *structurally* distinct; the flag is only the shared seam that keeps them
in lock-step. Any change to one path must be mirrored on the other by reading the flag, not by
following a clear method boundary. The append-annotation catch (:2378–2382), the orphan-reset gate
(:2441–2443), and the rollback's create-vs-exist split (:2531–2583) are three of the most subtle
invariants in the file, and they are currently reachable only through a null-check on a single local.

---

## Decision

### Chosen approach: split the flag-fork into two method bodies

Replace `StoreAsyncCore` with two independent entry points that each own their full write path,
sharing **only** the two primitives the paths genuinely share: `PrecomputeForStore` and
`PersistEphemeralAsync`. The flag is deleted. Behavior is preserved exactly; the change is a
re-layout, not a behavior change.

### Alternatives considered

| Alternative | Description | Rejected because |
|---|---|---|
| **A. Keep the flag, add a guard comment** | Leave `StoreAsyncCore` as-is, document the ten-way split. | Does not remove the coupling; the next change pays the same tax. |
| **B. Extract per-branch helper methods** | Move the null- and non-null- branches into two `private` methods but keep `StoreAsyncCore` dispatching on the flag. | The flag is still the seam; the entry point is still flag-forked. Same fragility, two extra methods. |
| **C. Introduce a `StoreKind` enum** | Replace `string? appendOperationId` with an explicit `enum { WholeMemory, AppendCreate }`. | A typed flag is still a flag — the ten-way split survives, just renamed. The paths are not two cases of one thing; they are two things. |
| **D. Split into two methods (chosen)** | `StorePersistentAsync` (whole-memory) and `CreateForAppendAsync` (append-create), each a single linear body; `PrecomputeForStore` / `PersistEphemeralAsync` are the shared primitives; `StorePersistentOrEphemeralAsync` is the new plain-surface dispatch. | — |

### Invariant: comment preservation

`FileMemoryStore.cs` carries dense invariant comments (D-01 raw keywords, the refuse-before-any-side-effect
gate, copy-on-write cache divergence, orphan-as-create, rollback-restores-not-deletes, case-folding
rationale). **Every comment that currently sits on the precompute block, the ephemeral block,
`StorePersistentAsync`, and the non-null branch must move with its code.** A lost comment is a
regression in the same way a lost line is. The refactor moves code *and* its commentary atomically;
it never paraphrases, collapses, or relocates a comment.

---

## Architecture & Components

### The two paths (already structurally distinct)

**Plain store** (`appendOperationId` null):

```
StoreAsync / StoreWithTransactionResultAsync
    └─> StoreAsyncCore(null)
         └─> StorePersistentAsync
              ├─> WholeMemoryTransaction + _wholeMemoryTransactions coordinator
              ├─> MemoryDerivation.ForStore
              ├─> CaptureWholeMemberAsync / WholeMemoryFaultPoint
              └─> RunUnderAppendLock(null) → WithExclusiveLock
```

**Append create** (non-null):

```
AppendAsync
    └─> StoreAsyncCore(operationId)
         └─> (non-null branch)
              ├─> AppendTransaction + PersistAppendJournal / VerifyAppendMemberAsync / TryRestoreAppendTransaction
              ├─> CaptureRawMemberAsync / AppendFaultPoint
              └─> RunUnderAppendLock(id) → lock-free
```

### Shared primitives (the ONLY things the paths share)

1. **`BuildDerivedEntry`** (`FileMemoryStore.cs:3813`) — single entry-materialization primitive.
   Both paths call it; unchanged.
2. **Precompute block** (`:2390–2399`) — `MemoryDerivation.ForStore` + NMP-2 artifact encoding +
   `chunkCount` + `originalBytes`. Both paths derive with `ForStore` (append-create is a whole write).
   `write.Keywords` is the caller's **RAW** list per `MemoryWrite`'s contract (D-01); nothing this
   block computes is ever fed back in as input.
3. **Ephemeral handling** — both plain-store-ephemeral-create and append-ephemeral-create route to
   `PersistEphemeral`.

### The refactor (one clean pass, behavior-preserving)

**1. `PrecomputeForStore(MemoryWrite write, bool isEphemeral) → PrecomputedStore`**

`PrecomputedStore` is a private record:

```csharp
private sealed record PrecomputedStore(
    DerivedMetadata Derived, string Artifact, int ChunkCount, long OriginalBytes);
```

Moves the `:2390–2399` block verbatim (preserving the inline comments about D-01 and the
chunk-encoding rule). `isEphemeral` is forwarded to `MemoryDerivation.ForStore(..., isEphemeral: ...)`.

**2. `PersistEphemeralAsync(MemoryWrite write, CancellationToken ct) → ArtifactEntry`**

Ephemeral create **or** update for the plain surface:

```csharp
private async Task<ArtifactEntry> PersistEphemeralAsync(MemoryWrite write, CancellationToken ct)
{
    string ephemeralKey = MemoryNaming.StripEphemeralPrefix(write.Name);
    EphemeralEntry? existingEph = GetEphemeral(ephemeralKey);
    PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: true);
    return PersistEphemeral(write.Name, pre.Artifact, pre.OriginalBytes, pre.ChunkCount, pre.Derived,
        createdAt: existingEph?.CreatedAt ?? DateTimeOffset.UtcNow,
        updatedAt: existingEph is not null ? DateTimeOffset.UtcNow : null);
}
```

**3. `StorePersistentAsync(MemoryWrite write, CancellationToken cancellationToken, Action<WholeMemoryResult>? transactionResult = null)`**

Change its signature from taking precomputed values to taking the raw `write`. At its top:

```csharp
PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: false);
```

and replace all uses of the removed params with `pre.Derived`, `pre.Artifact`, `pre.ChunkCount`,
`pre.OriginalBytes. The rest of the body stays byte-identical: `ResolveAddress`, `EnsureMaterialized`,
exclusive lock, `RecoverScopeUnsafe` + `RecoverAppendUnsafeAsync("store-recovery")`, index load,
`IsReadOnly` gate, `BuildDerivedEntry`, `CaptureWholeMemberAsync`, `BuildArchivePlan`,
`WholeMemoryTransaction` / `ArchivePairPlan`, `_wholeMemoryTransactions` `ExecuteAsync`, archive
publication, live write, `UpsertForAppend`, verification, prune.

**4. `CreateForAppendAsync(MemoryWrite write, string appendOperationId, CancellationToken ct) → ArtifactEntry`**

The verbatim `:2414–2586` non-null branch. At its top:

```csharp
PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: false);
var derived = pre.Derived;
var artifact = pre.Artifact;
var chunkCount = pre.ChunkCount;
var originalBytes = pre.OriginalBytes;
```

Keep the branch's existing comments verbatim (refuse-before-side-effect gate, previous-artifact
capture, create-transaction journaling, rollback). It **must not** call `ValidateOrThrow` —
`AppendAsync` validates first — and **must not** handle ephemeral — `AppendAsync` guarantees a
persistent orphan here.

**5. Replace `StoreAsyncCore` with `StorePersistentOrEphemeralAsync`**

```csharp
private async Task<ArtifactEntry> StorePersistentOrEphemeralAsync(
    MemoryWrite write, CancellationToken ct, Action<WholeMemoryResult>? transactionResult = null)
{
    ArgumentNullException.ThrowIfNull(write);
    write.ValidateOrThrow(_writeValidationOptions);   // plain validation — the appendOperationId
                                                      // annotation catch (was :2378–2382) is dead
                                                      // and removed.
    if (write.Address.Kind == MemoryAddressKind.Ephemeral)
        return await PersistEphemeralAsync(write, ct);
    return await StorePersistentAsync(write, ct, transactionResult);
}
```

**6. Rewire the four callers**

| Caller | Old | New |
|---|---|---|
| `StoreAsync` (:2357) | `StoreAsyncCore(write, ct, appendOperationId: null)` | `=> await StorePersistentOrEphemeralAsync(write, ct);` |
| `StoreWithTransactionResultAsync` (:2360) | `StoreAsyncCore(write, ct, null, result => transaction = result)` | `entry = await StorePersistentOrEphemeralAsync(write, ct, result => transaction = result);` |
| `AppendAsync` ephemeral-create (:2786) | `StoreAsyncCore(write, ct, operationId)` | `PersistEphemeralAsync(write, ct)` (guard `GetEphemeral(...) is null` retained) |
| `AppendAsync` persistent-orphan-create (:2812) | `StoreAsyncCore(write, ct, operationId)` | `CreateForAppendAsync(write, operationId, ct)` (surrounding `catch (AppendPostCommitCleanupException …)` → `SuccessWithWarning` wrapper retained) |

**7. Delete `StoreAsyncCore`** entirely (dead after the four callers are rewired).

### Nested model-intrinsic forks — explicitly unchanged

The refactor does **not** touch: `RunUnderAppendLock`, `WriteArtifactAsync`'s append-fault-point
fork, `UpsertCore` / `Upsert` / `UpsertForAppend`, `BuildDerivedEntry`, `PersistEphemeral` (its
signature), `MemoryDerivation`, `AppendTransaction` / `WholeMemoryTransaction`, the recovery methods,
the index cache, or any non-write-path code. Those forks remain where they are; the split lives one
level up, at the entry-point boundary.

### Validation split (preserved)

- **Plain path** validates with `ValidateOrThrow(_writeValidationOptions)` (multi-chunk allowed).
- **Append path** validates with `ValidateOrThrow(_writeValidationOptions, isAppend: true)` (exactly
  one chunk) in `AppendAsync`. `CreateForAppendAsync` performs no validation — the appendOperationId
  annotation catch that lived in `StoreAsyncCore` was the only place the plain catch ran, and it is
  dead.

---

## Data flow

### Plain store (whole memory)

```
MemoryWrite
    │  StorePersistentOrEphemeralAsync
    │  ├─ ValidateOrThrow (multi-chunk allowed)   ← plain validation
    │  └─ Address.Kind != Ephemeral
    ▼
StorePersistentAsync(write, ct, transactionResult?)
    │  PrecomputeForStore(write, isEphemeral: false)
    │     ├─ MemoryDerivation.ForStore(..., isEphemeral: false)   ← D-01: raw keywords preserved
    │     ├─ Nmp2ChunkedEncoder.Encode / EncodeChunks             ← chunk rule preserved
    │     ├─ GetChunkCount(artifact)
    │     └─ UTF8.GetByteCount(concat)
    │  → PrecomputedStore { Derived, Artifact, ChunkCount, OriginalBytes }
    ▼
ResolveAddress → EnsureMaterialized → WithExclusiveLock
    ├─ RecoverScopeUnsafe / RecoverAppendUnsafeAsync("store-recovery")
    ├─ LoadIndexUnderExclusiveAppendLock (recovery-read-barrier)
    ├─ IsReadOnly gate (refuse before side effect)
    ├─ BuildDerivedEntry
    ├─ CaptureWholeMemberAsync / WholeMemoryFaultPoint
    ├─ WholeMemoryTransaction + ArchivePairPlan
    ├─ _wholeMemoryTransactions.ExecuteAsync  (archive → live → verify → prune)
    └─ transactionResult?.Invoke(result)   (StoreWithTransactionResultAsync only)
```

### Append create (orphan)

```
AppendAsync
    │  ValidateOrThrow(isAppend: true)   ← exactly one chunk (append validation)
    │  RecoverScopeUnsafe / RecoverAppendUnsafeAsync(scope, operationId)
    │  LoadIndexUnderExclusiveAppendLock  (live, lock-free)
    │  File.Exists check → orphan
    ▼
CreateForAppendAsync(write, operationId, ct)
    │  (no ValidateOrThrow — AppendAsync already validated)
    │  PrecomputeForStore(write, isEphemeral: false)
    ▼
ResolveAddress → case-fold subject → orphan-is-create gate
    ├─ IsReadOnly gate (refuse BEFORE any side effect)
    ├─ previousArtifact captured (copy-on-write divergence backstop)
    ├─ BuildDerivedEntry
    ├─ AppendTransaction + ArchivePlan (create) → PersistAppendJournal(Prepared)
    ├─ WriteArtifactAsync / UpsertForAppend / VerifyAppendMemberAsync
    ├─ PersistAppendJournal(Committed) → DeleteAppendJournal
    └─ rollback: create-transaction restore/delete (:2531–2583)   ← comments preserved verbatim
```

### Ephemeral create (either surface)

```
StorePersistentOrEphemeralAsync OR AppendAsync
    │  Address.Kind == Ephemeral
    ▼
PersistEphemeralAsync(write, ct)          ← plain surface
     │  existing = GetEphemeral(StripEphemeralPrefix(name))
    │  PrecomputeForStore(write, isEphemeral: true)
    └─> PersistEphemeral(name, artifact, originalBytes, chunkCount, derived,
           createdAt: existing?.CreatedAt ?? now,
           updatedAt: existing is null ? null : now)

AppendAsync ephemeral-create              ← append surface (guard: GetEphemeral(...) is null)
    └─> PersistEphemeralAsync(write, ct) → AppendStoreResult.Created
```

---

## Error handling

- **Append annotation** — the plain-path append-operation-id annotation (:2378–2382) dies with
  `StoreAsyncCore`. `AppendAsync` keeps its own annotation at :2763–2766, which is the single place
  append errors are now annotated. `CreateForAppendAsync` never sees a validation failure.
- **Refuse-before-any-side-effect** — preserved verbatim in `CreateForAppendAsync` (:2447–2454) and
  in `StorePersistentAsync`'s `IsReadOnly` gate (:2604–2606). Both refuse before depositing an
  archive entry or a live member.
- **Rollback: restore, not delete** — preserved verbatim in `CreateForAppendAsync` (:2531–2583). An
  existing entry is *restored*; a brand-new orphan is *deleted*. The comment explains why a
  destructive unconditional delete (what the server's own store path used to do) was removed.
- **`DurableWriteException` shape** — unchanged. The refactor does not touch `DurableWriteException`,
  `AppendStoreException`, `AppendPostCommitCleanupException`, or `RecoveryRequired`.

---

## Testing matrix

The existing suite is the regression net. P2 adds no new tests unless a behavior gap is found.

| Test family | Pins |
|---|---|
| `FileMemoryStoreWriteSurfaceTests` | The write surface is unchanged in behavior; only the method layout differs. |
| `AppendRecoveryTests`, `AppendRollbackTests`, `AppendDurabilityTests` | `CreateForAppendAsync` preserves the create/journal/rollback ordering byte-for-byte. |
| `WholeMemory` / `StorePersistentAsync` families | `StorePersistentAsync` takes raw write, precomputes identically. |
| `EphemeralLifecycleTests` / ephemeral-append tests | `PersistEphemeralAsync` preserves create-vs-update `createdAt`/`updatedAt`. |
| `CaseSensitive*` | No change to case-folding; kept green. |
| **Full gate** | `dotnet test Scrinia.sln` — 3750+ tests. Any failure is a behavior regression; do not sweep it under the rug. |

Every new test (if any) uses `TestHelpers.StoreScope`.

---

## Frozen constraints (carry from P1)

- **NMP/2 unchanged** — the encoding rule and the D-01 raw-keyword comment move verbatim into
  `PrecomputeForStore`; the format itself is untouched.
- **`.scrinia/` layout unchanged** — the two paths still land memories in the same scopes; only the
  dispatch method differs.
- **CLI single-file surface** — no new public entry point; the four new methods are `private`.
- **MCP 12 + 11 disjoint** — `guide()`, the nine memory verbs, `facts`, and the eleven shared tools
  are unaffected; the write-path re-layout is invisible to the MCP surface.
- **Source-generated JSON only** — no reflection; `PrecomputedStore` is a private record, never serialized.
- **Recovery bypass untouched** — `DiscoverMemoriesRecursive` / `ParseScopeLabel` are not in scope.
- **No new public API** — `PrecomputeForStore` and `PersistEphemeralAsync` are private helpers;
  `StorePersistentAsync` / `CreateForAppendAsync` are `private`.
- **`DurableWriteException` shape unchanged.**
- **Every new test uses `TestHelpers.StoreScope`.**

---

## Out of scope

- `RunUnderAppendLock` and its lock-free vs exclusive distinction.
- `WriteArtifactAsync`'s append-fault-point fork.
- `UpsertCore` / `Upsert` / `UpsertForAppend`.
- `BuildDerivedEntry`, `PersistEphemeral` (signature), `MemoryDerivation`.
- `AppendTransaction` / `WholeMemoryTransaction` / `ArchivePlan` / `ArchivePairPlan`.
- Recovery methods (`RecoverScopeUnsafe`, `RecoverAppendUnsafeAsync`), the index cache.
- Any non-write-path code.
- Append ordering: `PublishLiveAppendAsync` (`FileMemoryStore.cs:3360`) is untouched; `CreateForAppendAsync`
  mirrors the existing create-before-journal ordering of the current non-null branch.
- No new MCP tool, CLI command, or public method.

---

## Execution

See the companion plan
`docs/superpowers/plans/2026-09-24-p2-write-path-unification-plan.md` (gitignored) for the single
clean execution pass: extract shared helpers → split the dispatch → verify + commit. This spec is
the design record; the plan is the work order.
