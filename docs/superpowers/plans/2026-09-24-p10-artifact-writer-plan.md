# P10 — Artifact-Body Durable-Write Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p10-artifact-writer-seam.md`
**Phase:** P10 (tenth god-store seam extraction; second write-path pass).
**Shape:** one method added to `DurableWriter` (P9) + one internal forwarder swap in the store. Single wave, sequential.

## Workstreams

- W1 — add `DurableWriter.WriteArtifact` (append to the P9 file).
- W2 — `FileMemoryStore.WriteArtifactAsync` → internal forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add `DurableWriter.WriteArtifact`

Edit `src/Scrinia.Core/DurableWriter.cs` (created in P9).

1. **Add usings** at the top of the file (after the existing `using Scrinia.Core.Formats;` / `using Scrinia.Core.Models;`):
   ```csharp
   using System;
   using System.IO;
   using System.Text;
   using System.Threading;
   ```
2. **Append** the `WriteArtifact` method to the `internal static class DurableWriter` body (after `WriteSidecar`):
   ```csharp
   internal static async Task WriteArtifact(string subject, string scope, string artifactText,
       string? operationId, Func<string, string, string> artifactPath, Action<string> invalidateArtifactCache,
       Action<string, string?, Action> runUnderAppendLock, CancellationToken ct = default)
   {
       ct.ThrowIfCancellationRequested();
       runUnderAppendLock(scope, operationId, () =>
       {
           string path = artifactPath(subject, scope);
           if (operationId is null)
               DurableFile.AtomicWriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(artifactText));
           else
               DurableFile.AtomicWriteAllBytesForAppend(
                   path, System.Text.Encoding.UTF8.GetBytes(artifactText), operationId,
                   AppendFaultPoint.LiveArtifactWrite,
                   AppendFaultPoint.LiveArtifactFlush);
           invalidateArtifactCache($"{scope}|{subject}|");
       });
       await Task.CompletedTask;
   }
   ```
   Copy the body verbatim from the current private `WriteArtifactAsync` in `FileMemoryStore.cs` (:3736–:3748), applying only the three re-targets (`ArtifactPath` → `artifactPath`, `RunUnderAppendLock` → `runUnderAppendLock`, `_artifactCache.Invalidate` → `invalidateArtifactCache`).

Confirm `AppendFaultPoint` ∈ `Scrinia.Core` (same namespace — resolves), `DurableFile` ∈ `Scrinia.Core` (same namespace — resolves), `ArtifactEntry`/`ArtifactSidecarPolicy`/`ScopeReadOnlyException`/`DurableFormats` ∈ `Scrinia.Core.Formats`/`Scrinia.Core.Models` (already imported).

## W2 — Wire into `FileMemoryStore`

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. **Replace** the current private `WriteArtifactAsync` body (lines ~3736–3748) with an internal one-line forwarder:

```csharp
internal Task WriteArtifactAsync(string subject, string scope, string artifactText,
    string? operationId, CancellationToken ct = default) =>
    DurableWriter.WriteArtifact(subject, scope, artifactText, operationId, ArtifactPath,
        key => _artifactCache.Invalidate(key), RunUnderAppendLock, ct);
```

Change the method's visibility from `private` to `internal`. To locate the exact current body, read `FileMemoryStore.cs` lines 3730–3750.

**Leave untouched:** the no-operationId internal forwarder (:3732–:3734), the public `WriteArtifactAsync(MemoryAddress address, ...)` (IPluginStore surface), `_artifactCache` (the ArtifactLruCache at :249 — only hooked, not modified), `ArtifactPath` (:633), `RunUnderAppendLock` (:1029), `InvalidateEntityFacts`/:2309/:2375/:2453/:3162 callers.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteArtifact|FullyQualifiedName~DurableWriter|FullyQualifiedName~Append|FullyQualifiedName~ArtifactFileIO|FullyQualifiedName~Sidecar"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns both content-persistence leaves (`WriteSidecar` from P9, `WriteArtifact` from P10); `FileMemoryStore` has zero artifact/sidecar body-write logic (only forwarders + hooks).
- No new public API; `FileStoreJsonContext`/`DurableFile`/`AppendFaultPoint` untouched.
- Full suite green.
