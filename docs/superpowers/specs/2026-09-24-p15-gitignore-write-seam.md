# P15 — Default-.gitignore Durable-Write Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P14 complete; `DurableWriter` owns six durable leaves: sidecar write (P9), artifact-body write (P10), copy+flush (P11), artifact-delete (P12), orphan-sweep delete (P13), import-staging best-effort delete (P14)).
**Phase:** P15 (fifteenth seam extraction; seventh write-path pass).
**Goal:** move the default `.scrinia/.gitignore` durable-write leaf (`WriteDefaultGitIgnore`) off the store into `DurableWriter`, leaving `EnsureGitIgnore`'s workspace-bootstrap wrapper (existence check + benign first-write race handling) intact.

## Why this phase

`EnsureGitIgnore` (workspace bootstrap, `FileMemoryStore.cs` :320) delegates the actual file write to `WriteDefaultGitIgnore` (:348). `WriteDefaultGitIgnore` is a pure durable-write leaf: it builds a fixed `.gitignore` content constant (with indentation cleanup) and writes it atomically through the store's `AtomicWriteAllText` forwarder. It has no fault injection, no recovery verification, and no instance state — the last clean durable-write leaf not yet owned by `DurableWriter`.

The append-path durable writes (`PersistAppendJournal` :3341, `WriteArchiveMember` :3020, `TryDeleteAppendJournal` :3385) are deliberately NOT part of this phase: they all delegate their durable primitive to `DurableFile.AtomicWriteAllBytesForAppend` and are pervaded by the store-level `InjectAppendFault`/`B0FaultSeam` append-fault-injection + recovery orchestration, a distinct seam from durable-write leaf ownership.

## The seam

`WriteDefaultGitIgnore` (current body, `FileMemoryStore.cs` :348–:373):
```csharp
private static void WriteDefaultGitIgnore(string gitIgnorePath)
{
    AtomicWriteAllText(gitIgnorePath, """
        # Lock files (runtime artifacts from cross-process file locking)
        **/*.lock

        # Export bundles (generated, can be re-exported)
        exports/

        # Archived versions (auto-pruned, not source-controlled)
        **/versions/

        # Temporary files from interrupted writes
        **/*.tmp

        # Interrupted topic restore journals (recovered on the next access)
        **/.restore-journal.json

        # Vector indexes (derived from topic content; rebuild with `scri reindex`)
        embeddings/

        # Consolidation bookkeeping (rebuilt from sidecar state on next run)
        .last-consolidation
        .consolidate-progress.json
        """.Replace("            ", ""));
}
```

Move the body verbatim into `DurableWriter.WriteDefaultGitIgnore`, replacing the store's `AtomicWriteAllText` forwarder with a direct `DurableFile.AtomicWriteAllText` call (the durable primitive already lives in `DurableFile`, owned from earlier phases).

### Update `DurableWriter` (src/Scrinia.Core/DurableWriter.cs)

Append:
```csharp
    /// <summary>Writes the default <c>.scrinia/.gitignore</c> content atomically. Pure durable-write
    /// leaf: fixed content constant, no fault injection, no recovery orchestration. Caller must have
    /// already ensured the parent directory exists.</summary>
    internal static void WriteDefaultGitIgnore(string gitIgnorePath)
    {
        string content = """
            # Lock files (runtime artifacts from cross-process file locking)
            **/*.lock

            # Export bundles (generated, can be re-exported)
            exports/

            # Archived versions (auto-pruned, not source-controlled)
            **/versions/

            # Temporary files from interrupted writes
            **/*.tmp

            # Interrupted topic restore journals (recovered on the next access)
            **/.restore-journal.json

            # Vector indexes (derived from topic content; rebuild with `scri reindex`)
            embeddings/

            # Consolidation bookkeeping (rebuilt from sidecar state on next run)
            .last-consolidation
            .consolidate-progress.json
            """.Replace("            ", "");
        DurableFile.AtomicWriteAllText(gitIgnorePath, content);
    }
```

### Store wiring (FileMemoryStore.cs)

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the `WriteDefaultGitIgnore` body (:348–:373) with a one-line forwarder:

```csharp
private static void WriteDefaultGitIgnore(string gitIgnorePath) =>
    DurableWriter.WriteDefaultGitIgnore(gitIgnorePath);
```

Leave `EnsureGitIgnore` (:320 — the existence check + benign first-write race wrapper) untouched, and leave `AtomicWriteAllText` (:1658 — the forwarder still used by `StoreLayoutMigration`) untouched.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`, `AtomicWriteAllText` forwarder (:1658 — retained for `StoreLayoutMigration`), `EnsureGitIgnore` (:320 — workspace-bootstrap wrapper), `StoreLayoutMigration` scrinia-marker block, and the read-side + durable-write seam owners (`ListingService`, `SearchService`, `DurableWriter`).

## Acceptance

- `DurableWriter` owns the default-.gitignore durable write; `FileMemoryStore.WriteDefaultGitIgnore` is a one-line forwarder.
- No new public API; `DurableFile`/`FileStoreJsonContext` untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteDefaultGitIgnore|FullyQualifiedName~EnsureGitIgnore|FullyQualifiedName~DurableWriter|FullyQualifiedName~GitIgnore"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
