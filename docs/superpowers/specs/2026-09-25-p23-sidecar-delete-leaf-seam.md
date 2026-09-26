# P23 — Sidecar Delete Leaf Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P22 complete: composition/search seam family P4–P8; durable-write/delete leaves P9–P16 in `DurableWriter`; archive-version leaf P17 in `ArchiveService`; entry-identity/normalization P18 in `ArtifactIdentityMapper`; append-journal durable write/delete P19–P20 in `AppendJournalWriter`; verification predicates + committed-hash proof P21–P22 in `AppendRecoveryVerifier`).
**Phase:** P23 (twenty-third seam extraction; durable-leaf phase, last surviving inline delete).
**Goal:** move the terminal sidecar `File.Delete` leaf off the store's `Remove` method into the existing `DurableWriter` owner, leaving the case-insensitive resolution and COW cache logic resident.

## Why this phase

`Remove(string name, string scope = "local")` (`FileMemoryStore.cs` :1878–:1954) is the user-facing delete path. Its durable operations split cleanly:

- **Already extracted:** the artifact-body delete in `DeleteAsync` forwards to `DurableWriter.DeleteArtifact` (P10); the case-insensitive sidecar *resolution* (`ResolveSidecarPath`, :1331) is a shared helper used by both `Remove` (:1887) and the update path (:2009), so it stays resident.
- **This phase:** the terminal `File.Delete(metaPath)` at :1892 — the last surviving inline durable delete in a mixed method. It is the counterpart of `DurableWriter.DeleteArtifact` (P10) but is strictly simpler: `metaPath` is already case-resolved, so this is a plain terminal delete with no fallback enumeration, no retry, no fault injection.

The remainder of `Remove` (:1894–:1951) is in-memory orchestration that is correctly NOT part of a durable-leaf phase:

- The copy-on-write cache replacement (:1914–:1934) consumes only the local `metaPath` string (`Path.GetFileName(metaPath)`); the durable leaf neither reads nor writes anything the cache needs beyond that frozen path.
- The topic-discovery invalidation (:1947–:1949) and `InvalidateEntityFacts` (:1951) are independent in-memory side effects.

`Remove` never calls `InjectAppendFault`/`InjectWholeFault` (no fault injection), is not part of any recovery/journal state machine (no recovery orchestration), and the delete statement uses only local `metaPath`/`storeDir` strings (no durable instance state consumed by the delete itself). The leaf therefore isolates cleanly into a `DurableWriter` owner following the exact P10 pattern: an `internal static` method that takes a resolved path and performs the single `File.Delete`.

## The seam

Add one method to `DurableWriter` (`src/Scrinia.Core/DurableWriter.cs`, P9 owner) and repoint one call site.

### Extend `DurableWriter` (`src/Scrinia.Core/DurableWriter.cs`)

Append the leaf:

```csharp
    /// <summary>Terminal delete of an already-resolved sidecar file. The caller (<see cref="FileMemoryStore.Remove"/>)
    /// performs the case-insensitive resolution via <see cref="FileMemoryStore.ResolveSidecarPath"/>; this owns only
    /// the plain <c>File.Delete</c>, matching the role of <see cref="DeleteArtifact"/> for the artifact body. Pure durable-delete
    /// leaf: no fault injection, no recovery orchestration, no case fallback.</summary>
    internal static void DeleteSidecar(string sidecarPath)
    {
        File.Delete(sidecarPath);
    }
```

### Store wiring (`FileMemoryStore.cs`)

One edit: at :1892, repoint the terminal delete to the owner, preserving the existing `// Delete the sidecar file` comment:

```csharp
            // Delete the sidecar file
            DurableWriter.DeleteSidecar(metaPath);
```

Confirm via grep that no remaining bare `File.Delete(metaPath)` call site remains inside `Remove` after the edit.

### Do NOT touch the dead instance `DeleteSidecar` (:1626–:1631)

The instance method `DeleteSidecar(string name, string storeDir)` at :1626 is dead code — its only reference is the `<see cref>` doc-comment at :463. Leave it in place: deleting it would break that cref and is out of scope for a durable-leaf phase. The new owner method is `DurableWriter.DeleteSidecar(string sidecarPath)` (different type, different signature) — no collision.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`InjectWholeFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter`, the `DurableWriter` durable-write leaves (P9–P16 — do NOT edit `WriteSidecar`/`WriteArtifact`/`AtomicFileCopy`/`DeleteArtifact`/`SweepOrphanSidecars`/`TryDeleteStagingFile`/`WriteDefaultGitIgnore`/`TryDeleteBounded`; only ADD `DeleteSidecar`), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20), `AppendRecoveryVerifier` (P21/P22), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `HasCommittedHash`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`, `RecoveryRequired`, `DeleteAppendJournal`, `PruneCommittedArchives`).

## Acceptance

- `DurableWriter` owns the terminal sidecar delete; `FileMemoryStore.Remove` retains only the case-insensitive resolution and the in-memory COW/invalidation orchestration.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state consumed by `DeleteSidecar`.
- The dead instance `DeleteSidecar` (:1626) is untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~DurableWriter|FullyQualifiedName~DeleteSidecar"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
