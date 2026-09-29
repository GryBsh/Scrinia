# P11 — Copy + Best-Effort-Flush Durable-Write Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p11-copy-flush-seam.md`
**Phase:** P11 (eleventh god-store seam extraction; third write-path pass).
**Shape:** two methods added to `DurableWriter` (P9/P10) + two internal one-line forwarders in the store. Single wave, sequential.

## Workstreams

- W1 — add `DurableWriter.AtomicFileCopy` + `DurableWriter.TryWriteAllText` (append to the P9/P10 file).
- W2 — `FileMemoryStore.AtomicFileCopy` + `TryWriteAllText` → internal forwarders.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add two methods to `DurableWriter`

Edit `src/Scrinia.Core/DurableWriter.cs` (created P9, extended P10).

1. **Ensure the file's usings** are: `using System;`, `using System.IO;`, `using System.Text;`, `using System.Threading;`, `using Scrinia.Core.Formats;`, `using Scrinia.Core.Models;`, and **`using Scrinia.Core.Resilience;`** (for `FileRetry`). If `using Scrinia.Core.Resilience;` is missing, add it.
2. **Append** both methods to the `internal static class DurableWriter` body (after `WriteArtifact` from P10):
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
   Copy both bodies verbatim from `FileMemoryStore.cs` (`AtomicFileCopy` at :1668, `TryWriteAllText` at :3993). For `TryWriteAllText`, only `WriteObserver?.Invoke(...)` → `writeObserver?.Invoke(...)` changes. **Keep `System.Text.Encoding.UTF8` FULLY QUALIFIED** (the project's `Scrinia.Core.Encoding` namespace makes bare `Encoding` ambiguous).

## W2 — Wire into `FileMemoryStore`

All edits in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the two method bodies with one-line forwarders.

- `AtomicFileCopy` (current :1668):
  ```csharp
  private static void AtomicFileCopy(string sourcePath, string destPath, bool overwrite = true) =>
      DurableWriter.AtomicFileCopy(sourcePath, destPath, overwrite);
  ```
- `TryWriteAllText` (current :3993):
  ```csharp
  private static (bool Written, byte[] Bytes) TryWriteAllText(string path, string content) =>
      DurableWriter.TryWriteAllText(path, content, WriteObserver);
  ```

To locate the exact current bodies, read `FileMemoryStore.cs` around :1668 (`AtomicFileCopy`) and :3993 (`TryWriteAllText`).

**Leave untouched:** `ArchiveVersionUnsafe` (calls `AtomicFileCopy` → now the forwarder), the `EntityFactMaterializer` ctor (:313–:317, passes `TryWriteAllText` method group → now the forwarder), `WriteObserver`/:126, `WriteObserverGate`/:123.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AtomicFileCopy|FullyQualifiedName~TryWriteAllText|FullyQualifiedName~DurableWriter|FullyQualifiedName~ArchiveVersion|FullyQualifiedName~FactMaterializer"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns the copy + best-effort-flush durable leaves; `FileMemoryStore` has zero direct `AtomicFileCopy`/`TryWriteAllText` bodies (only two one-line forwarders).
- No new public API; `FileStoreJsonContext`/`DurableFile`/`FileRetry` untouched.
- Full suite green.
