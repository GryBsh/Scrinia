# P14 — Import-Staging Best-Effort-Delete Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p14-staging-delete-seam.md`
**Phase:** P14 (fourteenth god-store seam extraction; sixth write-path pass).
**Shape:** one pure-static method added to `DurableWriter` + one internal one-line forwarder in the store. Single wave, sequential.

## Workstreams

- W1 — add `DurableWriter.TryDeleteStagingFile` (append to the DurableWriter file).
- W2 — `FileMemoryStore.TryDeleteStagingFile` → internal forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add `DurableWriter.TryDeleteStagingFile`

Edit `src/Scrinia.Core/DurableWriter.cs` (created P9, extended P10–P13).

Append the pure-static best-effort delete leaf (verbatim body from store `TryDeleteStagingFile` :4531–:4535):

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

No injected hooks and no new usings required (`System.IO` is already imported from P9–P13).

## W2 — Wire into `FileMemoryStore`

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the `TryDeleteStagingFile` body (:4531–:4535) with a one-line forwarder:

```csharp
private static void TryDeleteStagingFile(string path) => DurableWriter.TryDeleteStagingFile(path);
```

To locate the exact current body, read `FileMemoryStore.cs` lines 4531–4535.

**Leave untouched:** `SweepStaleStagingUnsafe` (:4546 — the `Directory.EnumerateFiles` + `.ToList()` enumeration wrapper that calls `TryDeleteStagingFile`), `ImportTopicEntriesCore` (:4097, its caller), `ImportStagingSuffix` (:4045 const).

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TryDeleteStagingFile|FullyQualifiedName~SweepStaleStagingUnsafe|FullyQualifiedName~DurableWriter|FullyQualifiedName~Import"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns the import-staging best-effort delete; `FileMemoryStore.TryDeleteStagingFile` is a one-line forwarder.
- No new public API; `FileStoreJsonContext`/`DurableFile`/`FileRetry` untouched.
- Full suite green.
