# P16 — Bounded-Maintenance-Delete Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P15 complete; `DurableWriter` owns seven durable leaves: sidecar write (P9), artifact-body write (P10), copy+flush (P11), artifact-delete (P12), orphan-sweep delete (P13), import-staging best-effort delete (P14), default-.gitignore write (P15)).
**Phase:** P16 (sixteenth seam extraction; eighth write-path pass).
**Goal:** move the bounded maintenance-delete leaf (`TryDeleteBounded`) off the store into `DurableWriter`, leaving the maintenance sweep's candidate-resolution, retention accounting, and warning logic intact.

## Why this phase

The maintenance/discovery sweep (`FileMemoryStore.cs` :787) deletes each recovery-evidence candidate via `TryDeleteBounded`, a bounded retry loop (3 attempts, `IOException` backoff, `UnauthorizedAccessException`/`DirectoryNotFoundException` terminal). It is a pure durable-delete leaf: no fault injection, no recovery verification, no cache mutation, no locking. It is the last live durable-write/delete leaf the store still owns a *body* for.

Two other store-owned durable methods were considered and correctly excluded: `AtomicWriteAllTextAsync` (:1637) and `DeleteSidecar` (:1647) are **dead private methods** with no callers anywhere in `src/` or tests — extracting dead code carries no value and risks regression. `Remove` (:1899) and `ArchiveVersionUnsafe` (:3935) are high-level operations with exclusive-locking, cache copy-on-write, atomic-copy, and pruning mixed into a single body — not leaves. The append-path durable writes (`PersistAppendJournal` :3317, `WriteArchiveMember` :2996, `TryDeleteAppendJournal` :3361, `PruneCommittedArchives`, `ReconcileArchiveMember`, `RestoreAppendMember`) all delegate their durable primitive to `DurableFile.AtomicWriteAllBytesForAppend` and are pervaded by the store-level `InjectAppendFault`/`B0FaultSeam` append-fault-injection + recovery orchestration — a distinct seam, not a durable-write leaf.

## The seam

`TryDeleteBounded` (current body, `FileMemoryStore.cs` :870–:899):
```csharp
private static bool TryDeleteBounded(string fullPath, CancellationToken cancellationToken)
{
    const int maxAttempts = 3;
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Delete(fullPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException) when (attempt < maxAttempts)
        {
            Task.Delay(10 * attempt, cancellationToken).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            return false;
        }
    }
    return false;
}
```

Move the body verbatim into `DurableWriter.TryDeleteBounded` (pure static, **zero injected hooks** — it touches only `System.IO.File` and `System.Threading`). The `Task.Delay`/`CancellationToken` usage is already supported by `DurableWriter` (its `WriteArtifact` leaf carries a `CancellationToken`).

### Update `DurableWriter` (src/Scrinia.Core/DurableWriter.cs)

Append:
```csharp
    /// <summary>Bounded best-effort delete for the maintenance sweep: up to three attempts with a
    /// short backoff between <c>IOException</c> retries, terminal on <c>UnauthorizedAccessException</c>
    /// / <c>DirectoryNotFoundException</c>, and cancellation-aware before each attempt. Never leaks a
    /// non-terminal exception to the caller's retention accounting.</summary>
    internal static bool TryDeleteBounded(string fullPath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(fullPath);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Task.Delay(10 * attempt, cancellationToken).GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                return false;
            }
        }
        return false;
    }
```

### Store wiring (FileMemoryStore.cs)

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the `TryDeleteBounded` body (:870–:899) with a one-line forwarder:

```csharp
private static bool TryDeleteBounded(string fullPath, CancellationToken cancellationToken) =>
    DurableWriter.TryDeleteBounded(fullPath, cancellationToken);
```

Leave the maintenance sweep caller (:787 — the `Path.GetFullPath(Path.Combine(storeDir, candidate.RelativePath))` argument construction) untouched.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`, the append-path durable writes (`PersistAppendJournal`, `WriteArchiveMember`, `TryDeleteAppendJournal`, `PruneCommittedArchives`, `ReconcileArchiveMember`, `RestoreAppendMember`), the dead private methods `AtomicWriteAllTextAsync`/`DeleteSidecar`, `Remove`/`ArchiveVersionUnsafe` (high-level ops), and the read-side + durable-write seam owners (`ListingService`, `SearchService`, `DurableWriter`).

## Acceptance

- `DurableWriter` owns the bounded maintenance delete; `FileMemoryStore.TryDeleteBounded` is a one-line forwarder.
- No new public API; `DurableFile`/`FileStoreJsonContext` untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TryDeleteBounded|FullyQualifiedName~DurableWriter|FullyQualifiedName~Maintenance|FullyQualifiedName~Delete"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
