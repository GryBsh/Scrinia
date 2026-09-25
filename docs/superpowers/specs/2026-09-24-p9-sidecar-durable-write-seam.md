# P9 — Sidecar Durable-Write Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P8 complete — ScopeLockManager, ScopeIndexCache, MaintenancePageServiceCache, TopicDiscoveryService, ListingService, SearchService).
**Phase:** P9 (ninth seam extraction; first write-path pass).
**Goal:** move the sidecar durable-write leaf (`WriteSidecarCore`) off the store into a new `DurableWriter` owning type, leaving the store a one-line forwarder. The store keeps `SanitizeNameCore` and `SerializeSidecar` (they have their own callers and reference the private-nested source-gen context `FileStoreJsonContext`). This is a low-risk, single-leaf write-path seam.

## Why this phase

The write path is layered (public gate → admission/locking → transaction management → derivation → durable leaf writers → derived-state invalidation). P4–P8 extracted the read-side composition family. P9 starts the write-side extraction with the cleanest, most cohesive leaf: the single gate that atomically writes one sidecar to disk. All other durable leaf writers (artifact body, append journal, deletes, archive copies) share `DurableFile` and call sites that are entangled with locking (`RunUnderAppendLock`/`WithExclusiveLock`) and cache invalidation (`_artifactCache`/`_scopeIndexCache`) — those are deferred to later phases.

## The seam

`WriteSidecarCore` (current body, `FileMemoryStore.cs` ~1600–1619) is the ONLY member that atomically writes a sidecar (`DurableFile.AtomicWriteAllText` / `...ForAppend`). It is `private static`. Its two dependencies — `SanitizeNameCore` (a store static delegating to the frozen `MemoryNaming.SanitizeName`) and `SerializeSidecar` (a store static using the private-nested `FileStoreJsonContext`) — stay in the store as injected hooks. Every existing caller keeps working unchanged: `WriteSidecar` (instance forwarder), `ExpandLegacyIndexEntries` (migration-facing static), and the whole-list gate `WriteSidecars`.

### New type: `DurableWriter`

New file `src/Scrinia.Core/DurableWriter.cs`, namespace `Scrinia.Core`, `internal static class DurableWriter`.

```csharp
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// Durable leaf writer for a single memory sidecar. Extracted from <see cref="FileMemoryStore.WriteSidecarCore"/>
/// (P9). Holds the atomic temp+move+dir-fsync write and the read-only backstop gate; the
/// sanitize and serialize steps stay on the store as injected hooks because they own their
/// own callers and reference store-private types.
/// </summary>
internal static class DurableWriter
{
    /// <summary>Atomically write <paramref name="entry"/>'s normalized sidecar JSON to
    /// <paramref name="storeDir"/>, refusing a read-only scope before touching disk.
    /// <paramref name="operationId"/> routes through the append fault-point variant when set.</summary>
    internal static void WriteSidecar(
        ArtifactEntry entry, string storeDir, string? operationId,
        Func<string, string> sanitizeName, Func<ArtifactEntry, string> serialize)
    {
        // Unconditional backstop, deliberately below every caller. ...
        if (ArtifactSidecarPolicy.IsReadOnly(entry))
            throw new ScopeReadOnlyException(
                storeDir, entry.Name, entry.SchemaVersion ?? 1, DurableFormats.ArtifactSidecar.Current);

        string metaPath = Path.Combine(storeDir, sanitizeName(entry.Name) + ".meta.json");
        string json = serialize(entry);
        if (operationId is null)
            DurableFile.AtomicWriteAllText(metaPath, json);
        else
            DurableFile.AtomicWriteAllTextForAppend(
                metaPath, json, operationId,
                AppendFaultPoint.LiveSidecarWrite,
                AppendFaultPoint.LiveSidecarFlush);
    }
}
```

Copy the `WriteSidecarCore` doc-comment paragraph about the "Unconditional backstop" verbatim into the moved method's inline comment (preserve the Critical/defect-class prose so the recovery barrier's class doc stays accurate).

### Store wiring (FileMemoryStore.cs)

All edits in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the current `WriteSidecarCore` body (lines ~1600–1619) with a one-line forwarder that injects the two store statics as hooks:

```csharp
private static void WriteSidecarCore(ArtifactEntry entry, string storeDir, string? operationId = null) =>
    DurableWriter.WriteSidecar(entry, storeDir, operationId, SanitizeNameCore, SerializeSidecar);
```

Leave `SanitizeNameCore` (482), `SerializeSidecar` (1621), `WriteSidecar` (1589), `WriteSidecars` (1553), `PersistMigratedEntry` (1541), `ExpandLegacyIndexEntries` (1658), `DeleteSidecar` (1701), and the three direct `SerializeSidecar` call sites (~2312, ~2438, ~2777) — and the migration callers `StoreLayoutMigration.AtomicWriteAllText`/`StoreLayoutMigration.ExpandLegacyIndexEntries` — completely untouched.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + `DiscoverMemoriesRecursive`/`ParseScopeLabel` recovery barrier (P4), `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName` (instance, public) and `SanitizeNameCore` (retained static), `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName` (recovery barrier), facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested source-gen context — do NOT change its visibility), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, `DurableFile` (atomic primitive family), and the read-side seam owners (`ListingService`, `SearchService`).

## Acceptance

- `DurableWriter` owns the sidecar durable-write leaf; `FileMemoryStore.WriteSidecarCore` is a one-line forwarder injecting `SanitizeNameCore` + `SerializeSidecar`.
- No new public API; `FileStoreJsonContext` visibility/namespace untouched; `SanitizeNameCore`/`SerializeSidecar` bodies untouched (only their call sites for `WriteSidecarCore` route through `DurableWriter`).
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteSidecar|FullyQualifiedName~DurableWriter|FullyQualifiedName~Sidecar|FullyQualifiedName~PersistMigratedEntry|FullyQualifiedName~ExpandLegacyIndexEntries|FullyQualifiedName~SaveIndex|FullyQualifiedName~Upsert"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln`. Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin. Run the full suite single-node (`/m:1`) if parallel MSBuild hangs on Windows.
