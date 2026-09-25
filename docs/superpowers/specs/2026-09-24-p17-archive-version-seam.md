# P17 — Archive-Version Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P16 complete; `DurableWriter` owns nine durable-write/delete leaves; read-side and cache/service owners `ListingService`, `SearchService`, `TopicDiscoveryService`, `ScopeLockManager`, `ScopeIndexCache`, `MaintenancePageServiceCache` already extracted).
**Phase:** P17 (seventeenth seam extraction; ninth write-path pass).
**Goal:** move the single-memory version-archive leaf (`ArchiveVersionUnsafe`) off the store into a dedicated `ArchiveService` owner, leaving the store's `ArchiveVersion` public/internal methods (the exclusive-lock boundary + frozen `IMemoryStore` surface) intact.

## Why this phase

`ArchiveVersionUnsafe` (`FileMemoryStore.cs` :3907–:3958) archives the current version of one memory's artifact and its sidecar into `versions/`, stamping to the millisecond with collision-avoidance, then prunes stale bodies/sidecars beyond the 10-most-recent retention. It is a cohesive, self-contained unit of work with **zero** fault injection, no recovery verification, no exclusive locking, and no cache copy-on-write inside its body. The body references only already-frozen primitives (`ArtifactPath`, `GetStoreDirForScope`, `SanitizeName`, `MetaPath`) and the `DurableWriter.AtomicFileCopy` leaf (P11). This is the last cohesive write-path responsibility still resident in the god store.

The `// ── Archive ──` region (:3900) is cleanly separated from the transactional append/archive logic above it (CRUD / write surface, :626–:3660) and the recovery/restore logic below it (:3960+).

## The seam

Move `ArchiveVersionUnsafe` (:3907–:3958) verbatim into a new owner `ArchiveService`. The body is unchanged except the store's `AtomicFileCopy` forwarder becomes a direct `DurableWriter.AtomicFileCopy(...)` call (the leaf already lives in `DurableWriter` from P11).

```csharp
public void ArchiveVersionUnsafe(string subject, string scope)
{
    string currentPath = _artifactPath(subject, scope);
    if (!File.Exists(currentPath))
        return;

    string storeDir = _storeDirForScope(scope);
    string versionsDir = Path.Combine(storeDir, "versions");
    Directory.CreateDirectory(versionsDir);

    // Stamped to the MILLISECOND, and never overwriting. A second-granularity stamp copied
    // with overwrite:true meant two archives of one subject inside the same second silently
    // replaced each other — losing a prior body from the only directory backing the promise
    // that a write never destroys what was already there. Two overwrites in a row is the
    // ordinary shape of an agent correcting itself mid-turn, not a contrived race.
    // Milliseconds still collide under a tight loop, so the counter is a floor, not a
    // flourish: the invariant is that this method never replaces an existing archive.
    string safe = _sanitizeName(subject);
    string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
    string baseName = $"{safe}_{stamp}";
    string archivePath = Path.Combine(versionsDir, $"{baseName}.nmp2");
    for (int n = 2; File.Exists(archivePath); n++)
        archivePath = Path.Combine(versionsDir, $"{baseName}-{n}.nmp2");

    DurableWriter.AtomicFileCopy(currentPath, archivePath, overwrite: false);

    // The sidecar travels WITH the body. Archiving the artifact alone made the documented
    // recovery in docs/server-admin.md corrupting rather than restoring: copying a body back
    // left the .meta.json describing the newer content, and StoreAsync's own comment records
    // where that lands — a later append reads the stale ChunkEntries and numbers the appended
    // chunk from the real count, yielding non-contiguous indices. Best-effort: a missing or
    // unreadable sidecar must not fail the write this archive is protecting.
    string metaPath = _metaPath(subject, scope);
    if (File.Exists(metaPath))
    {
        try { DurableWriter.AtomicFileCopy(metaPath, Path.ChangeExtension(archivePath, null) + ".meta.json", overwrite: false); }
        catch (IOException) { /* body is archived; metadata is a best-effort companion */ }
        catch (UnauthorizedAccessException) { }
    }

    // Prune old versions — keep only the 10 most recent, and take each body's sidecar with it
    // so pruning cannot leave a .meta.json describing an artifact that is gone.
    var stale = Directory.GetFiles(versionsDir, $"{safe}_*.nmp2")
        .OrderByDescending(f => f, StringComparer.Ordinal)
        .Skip(10)
        .ToList();
    foreach (var old in stale)
    {
        try { File.Delete(old); } catch { /* best-effort cleanup */ }
        try { File.Delete(Path.ChangeExtension(old, null) + ".meta.json"); } catch { }
    }
}
```

### New owner: `ArchiveService` (`src/Scrinia.Core/ArchiveService.cs`)

A sealed owner class holding only the injected hooks (no `FileMemoryStore` instance). Each hook is a delegate matching the frozen primitive's signature:

| Hook                       | Frozen primitive in `FileMemoryStore` | Delegate form          |
| -------------------------- | ------------------------------------- | ---------------------- |
| `_artifactPath`            | `ArtifactPath(name, scope)` (:609)    | `Func<string,string,string>` |
| `_storeDirForScope`        | `GetStoreDirForScope(scope)` (:601)   | `Func<string,string>`        |
| `_sanitizeName`            | `SanitizeName(name)` (:421)           | `Func<string,string>`        |
| `_metaPath`                | `MetaPath(name, scope)` (:612)        | `Func<string,string,string>` |

`ArchiveService` calls `DurableWriter.AtomicFileCopy` directly (existing P11 leaf).

### Store wiring (`FileMemoryStore.cs`)

Three edits:

1. **Field + construction.** Add a private field and construct it in the internal constructor (next to the other service fields, e.g. after `_search` at :308–:311), following the exact hook-injection pattern of `ListingService`/`SearchService`:

```csharp
private readonly ArchiveService _archiveService;
...
_archiveService = new ArchiveService(
    (name, scope) => ArtifactPath(name, scope),
    scope => GetStoreDirForScope(scope),
    name => SanitizeName(name),
    (name, scope) => MetaPath(name, scope));
```

2. **Forwarder.** Replace the `ArchiveVersionUnsafe` body (:3907–:3958) with a one-line forwarder:

```csharp
private void ArchiveVersionUnsafe(string subject, string scope) =>
    _archiveService.ArchiveVersionUnsafe(subject, scope);
```

3. **Lock boundary.** In `ArchiveVersion(string subject, string scope = "local")` (:3902–:3905), the `WithExclusiveLock` call stays in the store; only the inner delegate target changes:

```csharp
internal void ArchiveVersion(string subject, string scope = "local")
{
    WithExclusiveLock(scope, () => _archiveService.ArchiveVersionUnsafe(subject, scope));
}
```

The public `ArchiveVersion(MemoryAddress address)` (:4569 — frozen on `IMemoryStore`) and its delegate to the internal `ArchiveVersion(subject, scope)` are left untouched.

4. **Remove the now-dead forwarder.** The store's private `AtomicFileCopy` forwarder (:1616 — `private static void AtomicFileCopy(...) => DurableWriter.AtomicFileCopy(...)`) is used ONLY by `ArchiveVersionUnsafe`. With the leaf moved, this forwarder has no caller. Remove it. Confirm with a grep that no remaining caller references the store-level `AtomicFileCopy` before deleting.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`, `DurableWriter` durable-write leaves (P9–P16), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures (including `ArchiveVersion(MemoryAddress)` at :4569 and `GetStoreDirForScope` at :601, `ArtifactPath` at :609, `MetaPath` at :612), the read-side seam owners (`ListingService`, `SearchService`), and the existing `ArchiveVersion` tests in `tests/Scrinia.Tests/FileMemoryStoreTests.cs` (:985–:1059) which pin the behavior through the public API.

## Acceptance

- `ArchiveService` owns the version-archive leaf; `FileMemoryStore.ArchiveVersionUnsafe` is a one-line forwarder; the store keeps `ArchiveVersion` as the exclusive-lock boundary.
- No new public API; `IMemoryStore` surface unchanged.
- `DurableWriter.AtomicFileCopy` (P11) is the sole durable primitive used.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ArchiveVersion|FullyQualifiedName~ArchiveService|FullyQualifiedName~DurableWriter"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
