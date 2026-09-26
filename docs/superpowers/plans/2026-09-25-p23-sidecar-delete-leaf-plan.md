# P23 — Sidecar Delete Leaf Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p23-sidecar-delete-leaf-seam.md`
**Phase:** P23 (twenty-third god-store seam extraction; durable-leaf phase).
**Shape:** add one leaf to the existing `DurableWriter` owner (P9) + one store edit. Single wave, sequential.

## Workstreams

- W1 — add `DeleteSidecar` to `DurableWriter`.
- W2 — repoint the terminal delete in `Remove`.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Extend `DurableWriter`

Edit `src/Scrinia.Core/DurableWriter.cs` (P9 owner). Append the terminal sidecar-delete leaf (verbatim, no injected hooks):

```csharp
    /// <summary>Terminal delete of an already-resolved sidecar file. The caller (<see cref="FileMemoryStore.Remove"/>)
    /// performs the case-insensitive resolution via <see cref="FileMemoryStore.ResolveSidecarPath"/>; this owns only
    /// the plain <c>File.Delete</c>, matching the role of <see cref="DeleteArtifact"/> for the artifact body. Pure durable-delete
    /// leaf: no fault injection, no recovery orchestration, no case fallback.</summary>
    internal static void DeleteSidecar(string sidecarPath)
    {
        File.Delete(sidecarPath);
    }
```

## W2 — Repoint the terminal delete in `Remove`

Edit `src/Scrinia.Core/FileMemoryStore.cs`. At :1892, repoint the terminal delete to the owner, preserving the existing `// Delete the sidecar file` comment:

```csharp
            // Delete the sidecar file
            DurableWriter.DeleteSidecar(metaPath);
```

Leave the dead instance `DeleteSidecar` (:1626–:1631) untouched.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~DurableWriter|FullyQualifiedName~DeleteSidecar"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns the terminal sidecar delete; `FileMemoryStore.Remove` retains only the case-insensitive resolution and the in-memory COW/invalidation orchestration.
- No new public API; `IMemoryStore` surface unchanged.
- The dead instance `DeleteSidecar` (:1626) is untouched.
- Full suite green.
