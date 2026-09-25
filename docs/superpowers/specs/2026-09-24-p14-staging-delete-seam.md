# P14 — Import-Staging Best-Effort-Delete Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P13 complete; `DurableWriter` owns five durable leaves: sidecar write (P9), artifact-body write (P10), copy+flush (P11), artifact-delete (P12), orphan-sweep delete (P13)).
**Phase:** P14 (fourteenth seam extraction; sixth write-path pass).
**Goal:** move the import-staging best-effort delete leaf (`TryDeleteStagingFile`) off the store into `DurableWriter`, leaving the store's `SweepStaleStagingUnsafe` enumeration wrapper intact.

## Why this phase

`ImportTopicEntriesCore` (under the scope's exclusive lock) calls `SweepStaleStagingUnsafe`, which enumerates abandoned `*.staging` files and deletes each via `TryDeleteStagingFile`. `TryDeleteStagingFile` is a pure, lock-free, fault-free best-effort `File.Delete` leaf — the last clean durable leaf not yet owned by `DurableWriter`. The remaining durable writes (`PersistAppendJournal`, `WriteArchiveMember`, `TryDeleteAppendJournal`) are all entangled with the append fault-injection (`InjectAppendFault`) and recovery-verification machinery and are deferred.

## The seam

`TryDeleteStagingFile` (current body, `FileMemoryStore.cs` :4531–:4535):
```csharp
private static void TryDeleteStagingFile(string path)
{
    try { File.Delete(path); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
}
```

Move the body verbatim into `DurableWriter.TryDeleteStagingFile` (pure static, **zero injected hooks** — it touches only `System.IO.File`).

### Update `DurableWriter` (src/Scrinia.Core/DurableWriter.cs)

Append:
```csharp
    /// <summary>Best-effort delete of an abandoned import-staging file: swallow I/O and
    /// access-denial failures. Import holds the scope's exclusive lock, so a present staging file
    /// is abandoned by definition; this never rethrows.</summary>
    internal static void TryDeleteStagingFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
```

### Store wiring (FileMemoryStore.cs)

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the `TryDeleteStagingFile` body (:4531–:4535) with a one-line forwarder:

```csharp
private static void TryDeleteStagingFile(string path) => DurableWriter.TryDeleteStagingFile(path);
```

Leave `SweepStaleStagingUnsafe` (:4546 — the `Directory.EnumerateFiles` + `.ToList()` enumeration wrapper that calls `TryDeleteStagingFile`) untouched, and leave `ImportTopicEntriesCore` (:4097, its caller) untouched.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier (P4), `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`RecoveryEvidenceVerifier`, `ImportStagingSuffix` (:4045 — a store const, referenced by the retained `SweepStaleStagingUnsafe`), and the read-side + durable-write seam owners (`ListingService`, `SearchService`, `DurableWriter`).

## Acceptance

- `DurableWriter` owns the import-staging best-effort delete; `FileMemoryStore.TryDeleteStagingFile` is a one-line forwarder.
- No new public API; `FileStoreJsonContext`/`DurableFile`/`FileRetry` untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TryDeleteStagingFile|FullyQualifiedName~SweepStaleStagingUnsafe|FullyQualifiedName~DurableWriter|FullyQualifiedName~Import"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
