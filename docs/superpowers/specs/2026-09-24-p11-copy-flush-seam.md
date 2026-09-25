# P11 — Copy + Best-Effort-Flush Durable-Write Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P10 complete; `DurableWriter` owns the sidecar write (P9) and artifact-body write (P10) leaves).
**Phase:** P11 (eleventh seam extraction; third write-path pass).
**Goal:** move the two lock-free durable file operations — the atomic `AtomicFileCopy` and the best-effort `TryWriteAllText` facts flush — off the store into `DurableWriter`, leaving two internal one-line forwarders. Both are pure static leaf ops with no lock/recovery/cache gate, so this is a clean, low-risk seam.

## Why this phase

`AtomicFileCopy` backs archive-version writes (`ArchiveVersionUnsafe`); `TryWriteAllText` is the derived-`facts.json` flush delegate handed to `EntityFactMaterializer` at construction. Both are durable-file leaves that share `DurableWriter`'s "touch disk directly" concern without the append-lock / recovery-gate entanglement of the content writers.

## The seam

### Update `DurableWriter` (src/Scrinia.Core/DurableWriter.cs)

Append these two methods to the existing `internal static class DurableWriter`. Ensure the file's usings include `System`, `System.IO`, `System.Threading`, `Scrinia.Core.Formats`, `Scrinia.Core.Models`, and **`using Scrinia.Core.Resilience;`** (for `FileRetry`).

```csharp
    /// <summary>Atomically copy <paramref name="sourcePath"/> to <paramref name="destPath"/> via a
    /// unique temp file + <see cref="FileRetry.Run"/> (survives NTFS sharing violations). Used by
    /// the archive-version path.</summary>
    internal static void AtomicFileCopy(string sourcePath, string destPath, bool overwrite = true)
    {
        string tmp = $"{destPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileRetry.Run(() => File.Copy(sourcePath, tmp, overwrite: true));
            FileRetry.Run(() => File.Move(tmp, destPath, overwrite: overwrite));
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Best-effort, non-atomic flush of derived <c>facts.json</c>: a bare
    /// <c>File.WriteAllBytes</c> (no temp/move/fsync — the derived facts cache is rebuildable),
    /// swallowing any failure. <paramref name="writeObserver"/> is the store's optional test hook.</summary>
    internal static (bool Written, byte[] Bytes) TryWriteAllText(string path, string content,
        Action<string, byte[]>? writeObserver)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        try
        {
            File.WriteAllBytes(path, bytes);
            writeObserver?.Invoke(path, bytes);
            return (true, bytes);
        }
        catch
        {
            return (false, []);
        }
    }
```

Copy both bodies verbatim from `FileMemoryStore.cs` (`AtomicFileCopy` at :1668, `TryWriteAllText` at :3993). For `TryWriteAllText`, only `WriteObserver?.Invoke(...)` → `writeObserver?.Invoke(...)` changes. Keep `System.Text.Encoding.UTF8` FULLY QUALIFIED (the project's `Scrinia.Core.Encoding` namespace makes the bare `Encoding` identifier ambiguous here); do not rely on `using System.Text;` to resolve it.

### Store wiring (FileMemoryStore.cs)

All edits in `src/Scrinia.Core/FileMemoryStore.cs`. Replace both method bodies with one-line forwarders:

```csharp
    private static void AtomicFileCopy(string sourcePath, string destPath, bool overwrite = true) =>
        DurableWriter.AtomicFileCopy(sourcePath, destPath, overwrite);
```

```csharp
    private static (bool Written, byte[] Bytes) TryWriteAllText(string path, string content) =>
        DurableWriter.TryWriteAllText(path, content, WriteObserver);
```

Leave `ArchiveVersionUnsafe` (:4066/:4077 — calls `AtomicFileCopy`, now the forwarder) and the `EntityFactMaterializer` ctor (:313–:317 — passes `TryWriteAllText` as a method group, now the forwarder) UNCHANGED. Leave `WriteObserver`/:126 and `WriteObserverGate`/:123 untouched.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier (P4), `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `_artifactCache` (ArtifactLruCache), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, `DurableFile`/`FileRetry`/`AppendFaultPoint`, and the read-side seam owners (`ListingService`, `SearchService`, `DurableWriter`).

## Acceptance

- `DurableWriter` owns the copy + best-effort-flush durable leaves; `FileMemoryStore` has zero direct `AtomicFileCopy`/`TryWriteAllText` bodies (only one-line forwarders).
- No new public API; `FileStoreJsonContext`/`DurableFile`/`FileRetry` untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AtomicFileCopy|FullyQualifiedName~TryWriteAllText|FullyQualifiedName~DurableWriter|FullyQualifiedName~ArchiveVersion|FullyQualifiedName~FactMaterializer"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
