# P16 — Bounded-Maintenance-Delete Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p16-bounded-delete-seam.md`
**Phase:** P16 (sixteenth god-store seam extraction; eighth write-path pass).
**Shape:** one pure static method added to `DurableWriter` + one internal one-line forwarder in the store. Single wave, sequential.

## Workstreams

- W1 — add `DurableWriter.TryDeleteBounded` (append to the DurableWriter file).
- W2 — `FileMemoryStore.TryDeleteBounded` → internal forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add `DurableWriter.TryDeleteBounded`

Edit `src/Scrinia.Core/DurableWriter.cs` (created P9, extended P10–P15).

Append the bounded best-effort delete leaf (verbatim body from store `TryDeleteBounded` :870–:899):

```csharp
    /// <summary>Bounded best-effort delete for the maintenance sweep: up to three attempts with a
    /// short backoff between <c>IOException</c> retries, terminal on <c>UnauthorizedAccessException</c>
    /// / <c>DirectoryNotFoundException</c>, and cancellation-aware before each attempt. Never leaks a
    /// non-terminal exception to the caller's retention accounting.</summary>
    internal static bool TryDeleteBounded(string fullPath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(fullPath);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Task.Delay(10 * attempt, cancellationToken).GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                return false;
            }
        }
        return false;
    }
```

No injected hooks and no new usings required (`System.Threading` for `CancellationToken`/`Task` is already imported from prior phases; `System.IO.File` likewise).

## W2 — Wire into `FileMemoryStore`

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Locate the exact current `TryDeleteBounded` body (the `private static bool TryDeleteBounded(string fullPath, CancellationToken cancellationToken)` method) and replace its body with a one-line forwarder:

```csharp
private static bool TryDeleteBounded(string fullPath, CancellationToken cancellationToken) =>
    DurableWriter.TryDeleteBounded(fullPath, cancellationToken);
```

**Leave untouched:** the maintenance sweep caller (:787 — `if (!TryDeleteBounded(Path.GetFullPath(Path.Combine(storeDir, candidate.RelativePath)), cancellationToken))`), the dead private methods `AtomicWriteAllTextAsync` (:1637) and `DeleteSidecar` (:1647), `Remove`/`ArchiveVersionUnsafe` (high-level ops), and the append-path durable writes.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TryDeleteBounded|FullyQualifiedName~DurableWriter|FullyQualifiedName~Maintenance|FullyQualifiedName~Delete"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns the bounded maintenance delete; `FileMemoryStore.TryDeleteBounded` is a one-line forwarder.
- No new public API; `DurableFile`/`FileStoreJsonContext` untouched.
- Full suite green.
