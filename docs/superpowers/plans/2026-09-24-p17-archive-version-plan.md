# P17 — Archive-Version Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p17-archive-version-seam.md`
**Phase:** P17 (seventeenth god-store seam extraction; ninth write-path pass).
**Shape:** one new owner class (`ArchiveService`) + three store edits. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/ArchiveService.cs` with the moved `ArchiveVersionUnsafe` leaf.
- W2 — store wiring: field + construction, forwarder, lock-boundary delegate, remove dead `AtomicFileCopy` forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `ArchiveService`

Create `src/Scrinia.Core/ArchiveService.cs` (new file). The sealed owner holds only the four injected hooks and the verbatim `ArchiveVersionUnsafe` leaf, calling `DurableWriter.AtomicFileCopy` (P11 leaf) directly instead of the store's `AtomicFileCopy` forwarder:

```csharp
using System.Globalization;

namespace Scrinia.Core;

/// <summary>
/// Owns the single-memory version-archive leaf moved off <see cref="FileMemoryStore"/> in P17.
/// Holds no <see cref="FileMemoryStore"/> instance — only the injected path/name hooks the
/// store exposes as frozen primitives. Runs under the caller's exclusive lock.
/// </summary>
internal sealed class ArchiveService
{
    private readonly Func<string, string, string> _artifactPath;
    private readonly Func<string, string> _storeDirForScope;
    private readonly Func<string, string> _sanitizeName;
    private readonly Func<string, string, string> _metaPath;

    public ArchiveService(
        Func<string, string, string> artifactPath,
        Func<string, string> storeDirForScope,
        Func<string, string> sanitizeName,
        Func<string, string, string> metaPath)
    {
        _artifactPath = artifactPath;
        _storeDirForScope = storeDirForScope;
        _sanitizeName = sanitizeName;
        _metaPath = metaPath;
    }

    /// <summary>Archive the current version of one memory's artifact (and best-effort its sidecar)
    /// into <c>versions/</c> under a millisecond-stamped, never-overwriting name, then prune stale
    /// bodies/sidecars beyond the 10-most-recent retention.</summary>
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
}
```

## W2 — Store wiring (`FileMemoryStore.cs`)

Four edits:

1. **Field.** Add `private readonly ArchiveService _archiveService;` near the other service fields (e.g. next to `_artifactCache` at :249 or the other service fields).

2. **Construction.** In the internal constructor, add after the `_search` construction (:308–:311), matching the `ListingService`/`SearchService` hook-injection pattern:

```csharp
_archiveService = new ArchiveService(
    (name, scope) => ArtifactPath(name, scope),
    scope => GetStoreDirForScope(scope),
    name => SanitizeName(name),
    (name, scope) => MetaPath(name, scope));
```

3. **Forwarder.** Replace the `ArchiveVersionUnsafe` body (:3907–:3958) with:

```csharp
private void ArchiveVersionUnsafe(string subject, string scope) =>
    _archiveService.ArchiveVersionUnsafe(subject, scope);
```

4. **Lock boundary.** Change the inner delegate of `ArchiveVersion(string subject, string scope = "local")` (:3902–:3905) from `ArchiveVersionUnsafe(subject, scope)` to `_archiveService.ArchiveVersionUnsafe(subject, scope)`.

5. **Remove dead forwarder.** Delete the now-unused private `AtomicFileCopy` forwarder (:1616 — `private static void AtomicFileCopy(string sourcePath, string destPath, bool overwrite = true) => DurableWriter.AtomicFileCopy(sourcePath, destPath, overwrite);`). Confirm via grep that no remaining caller references the store-level `AtomicFileCopy` before deleting.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ArchiveVersion|FullyQualifiedName~ArchiveService|FullyQualifiedName~DurableWriter"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `ArchiveService` owns the version-archive leaf; `FileMemoryStore.ArchiveVersionUnsafe` is a one-line forwarder; the store keeps `ArchiveVersion` as the exclusive-lock boundary and its `IMemoryStore` public surface is unchanged.
- No new public API; `DurableFile`/`FileStoreJsonContext` untouched.
- Full suite green.
