# P13 — Orphan-Sidecar Delete Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P12 complete; `DurableWriter` owns the sidecar write (P9), artifact-body write (P10), copy+flush (P11), and artifact-delete (P12) leaves).
**Phase:** P13 (thirteenth seam extraction; fifth write-path pass).
**Goal:** move the orphan-sidecar delete (the `*.meta.json` orphan sweep in `SaveIndexUnsafe`) off the store into `DurableWriter`, leaving the store a one-line call.

## Why this phase

`SaveIndexUnsafe` does two things: (1) sweep orphaned sidecars (delete `*.meta.json` not in the handed-in list), (2) publish (write sidecars + replace the scope-index cache + invalidate topic/fact caches). The sweep is a self-contained 6-line durable leaf — the same `Directory.EnumerateFiles` + `File.Delete` primitives `DurableWriter` already owns. The publish/cache/invalidation stay on the store. This is a clean, cohesive "delete unmatched durable files" seam.

**Blast radius is narrow:** the orphan sweep runs ONLY on the `SaveIndex` overwrite API. `SaveIndex` has no production caller (only tests/benchmarks warm it); `Upsert`/`UpsertCore`, `Remove`, `ImportTopicEntriesCore`, and transaction rollback never sweep. So this leaf is exercised by the orphan-sidecar test suite, not the hot write path.

## The seam

`SaveIndexUnsafe` (:1750–:1786) currently builds the orphan allow-list inline (:1778–:1783):
```csharp
var expected = new HashSet<string>(
    entries.Select(e => SanitizeName(e.Name) + ".meta.json"),
    StringComparer.OrdinalIgnoreCase);
foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json").ToList())
    if (!expected.Contains(Path.GetFileName(metaFile)))
        File.Delete(metaFile);
```

Move the allow-list build + delete loop into `DurableWriter.SweepOrphanSidecars`. The store passes `storeDir` (already resolved via `GetStoreDirForScope`) and the **already-sanitized** stems (`entries.Select(e => SanitizeName(e.Name))`), so `SanitizeName`/`GetStoreDirForScope` stay on the frozen naming + migration-gate side and no sanitize hook is needed.

### Update `DurableWriter` (src/Scrinia.Core/DurableWriter.cs)

Append this method to the existing `internal static class DurableWriter`.

```csharp
    /// <summary>Delete any <c>*.meta.json</c> in <paramref name="storeDir"/> whose stem is not in
    /// <paramref name="keptStems"/> — the orphan sidecar sweep run by <c>SaveIndex</c>.
    /// <paramref name="keptStems"/> must already be sanitized (the caller owns sanitizing, so the
    /// allow-list matches the physical leaf names the read-only gate and canonicalization produced).
    /// The enumeration is snapshotted with <c>.ToList()</c> before any delete so a sidecar written
    /// by the subsequent publish pass is never re-encountered; membership is case-insensitive to
    /// match the case-insensitive read side.</summary>
    internal static void SweepOrphanSidecars(string storeDir, IEnumerable<string> keptStems)
    {
        var expected = new HashSet<string>(
            keptStems.Select(s => s + ".meta.json"),
            StringComparer.OrdinalIgnoreCase);
        foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json").ToList())
            if (!expected.Contains(Path.GetFileName(metaFile)))
                File.Delete(metaFile);
    }
```

Copy the allow-list build + delete loop verbatim, moving only the suffix construction (`"+ ".meta.json"`) and the `StringComparer.OrdinalIgnoreCase` comparer in; the `.ToList()` snapshot and the `expected.Contains`/`File.Delete` predicate are byte-identical.

### Store wiring (FileMemoryStore.cs)

All edits in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the orphan-sweep block (:1778–:1783) in `SaveIndexUnsafe` with a single call:

```csharp
        DurableWriter.SweepOrphanSidecars(storeDir, entries.Select(e => SanitizeName(e.Name)));
```

Leave EVERYTHING ELSE in `SaveIndexUnsafe` untouched — `GetStoreDirForScope` (:1752), `CanonicalizeEntryForScope` (:1760), the read-only gate (:1773–:1776, which must still run BEFORE the sweep), and `PublishIndexUnsafe` (:1785). The ordering (gate → sweep → publish) is preserved; only the sweep's implementation moves.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier (P4), `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore` (retained — the store sanitizes, `DurableWriter` receives sanitized stems), `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4 — `_scopeIndexCache.Replace` stays in `PublishIndexUnsafe`), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures (`SaveIndex`/`SaveIndexUnsafe` are store-private/public but NOT on any interface), `DurableFile`/`FileRetry`/`AppendFaultPoint`, and the read-side + durable-write seam owners (`ListingService`, `SearchService`, `DurableWriter`).

## Acceptance

- `DurableWriter` owns the orphan-sidecar delete; `FileMemoryStore.SaveIndexUnsafe` calls `DurableWriter.SweepOrphanSidecars` with sanitized stems.
- The `.ToList()` snapshot + case-insensitive allow-list + delete predicate are byte-identical (both pinned by `SaveIndexOrphanSidecarTests` / `TransactionalRestoreTests`).
- No new public API; `FileStoreJsonContext`/`DurableFile`/`FileRetry` untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SweepOrphanSidecars|FullyQualifiedName~DurableWriter|FullyQualifiedName~SaveIndex|FullyQualifiedName~OrphanSidecar"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
