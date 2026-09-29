# P12 — Artifact-Body Durable-Delete Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p12-artifact-delete-seam.md`
**Phase:** P12 (twelfth god-store seam extraction; fourth write-path pass).
**Shape:** one method added to `DurableWriter` (P9/P10/P11) + one internal one-line forwarder in the store. Single wave, sequential.

## Workstreams

- W1 — add `DurableWriter.DeleteArtifact` (append to the DurableWriter file).
- W2 — `FileMemoryStore.DeleteArtifactUnsafe` → internal forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add `DurableWriter.DeleteArtifact`

Edit `src/Scrinia.Core/DurableWriter.cs` (created P9, extended P10/P11).

Append the `DeleteArtifact` method (verbatim body from store `DeleteArtifactUnsafe` :3754–:3809) with the three re-targets (`ArtifactPath(subject, scope)` → `artifactPath(subject, scope)`, `_artifactCache.Invalidate(...)` → `invalidateArtifactCache(...)`), and the two injected hooks:
- `Func<string, string, string> artifactPath`
- `Action<string> invalidateArtifactCache`

Keep the full leading `<summary>` doc comment (the one that documents the RULING/forget-pairing/CaseSensitiveFileStoreTests pin) verbatim — the guard test depends on this logic being behavior-identical. Keep `File.Delete`, `File.Exists`, `Directory.EnumerateFiles`, `Path.GetDirectoryName`, `Path.GetFileName`, `StringComparer.Ordinal`, `StringComparison.OrdinalIgnoreCase` calls as-is.

Ensure the file's usings cover `System` (StringComparer, Func, Action), `System.IO` (File, Directory, Path), `System.Threading` (not needed here), and that `System.Text` is present only if P11's `TryWriteAllText` needs it (it does — for the fully-qualified `System.Text.Encoding` usage there). No new using is strictly required for `DeleteArtifact` beyond what P9–P11 already added (`System`, `System.IO`), but confirm the file still compiles (the `Action`/`Func` types need `using System;`).

## W2 — Wire into `FileMemoryStore`

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the `DeleteArtifactUnsafe` body (lines :3754–:3809) with a one-line forwarder:

```csharp
private bool DeleteArtifactUnsafe(string subject, string scope) =>
    DurableWriter.DeleteArtifact(subject, scope, ArtifactPath, key => _artifactCache.Invalidate(key));
```

To locate the exact current body, read `FileMemoryStore.cs` lines 3754–3809.

**Leave untouched:** `DeleteArtifact` (public, :3749–:3751 — the lock wrapper), its two callers (:460, :2365), `ArtifactPath` (:633), `_artifactCache` (:249), `Remove` (the sibling delete that owns a different case-insensitive fallback), and all other store members.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~DeleteArtifact|FullyQualifiedName~DurableWriter|FullyQualifiedName~CaseSensitive|FullyQualifiedName~Remove"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns the artifact-body delete leaf; `FileMemoryStore.DeleteArtifactUnsafe` is a one-line forwarder injecting `ArtifactPath` + `_artifactCache.Invalidate`.
- The case-insensitive fallback + ordinal tie-break are byte-identical (comment block preserved verbatim).
- No new public API; `FileStoreJsonContext`/`DurableFile`/`FileRetry`/`ArtifactLruCache` untouched.
- Full suite green.
