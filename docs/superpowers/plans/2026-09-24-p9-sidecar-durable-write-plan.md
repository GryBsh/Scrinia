# P9 — Sidecar Durable-Write Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p9-sidecar-durable-write-seam.md`
**Phase:** P9 (ninth god-store seam extraction; first write-path pass).
**Shape:** one new internal owner type (`DurableWriter`) + store wiring (replace 1 method body with a 1-line forwarder). Single wave, sequential.

## Workstreams

- W1 — new `DurableWriter` (`src/Scrinia.Core/DurableWriter.cs`).
- W2 — `FileMemoryStore.WriteSidecarCore` → one-line forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `DurableWriter`

New file `src/Scrinia.Core/DurableWriter.cs`, namespace `Scrinia.Core`, `internal static class DurableWriter`.

```csharp
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// Durable leaf writer for a single memory sidecar. Extracted from <see cref="FileMemoryStore.WriteSidecarCore"/>
/// (P9). Holds the atomic temp+move+dir-fsync write and the read-only backstop gate; the
/// sanitize and serialize steps stay on the store as injected hooks because they own their
/// own callers and reference store-private types.
/// </summary>
internal static class DurableWriter
{
    /// <summary>Atomically write <paramref name="entry"/>'s normalized sidecar JSON to
    /// <paramref name="storeDir"/>, refusing a read-only scope before touching disk.
    /// <paramref name="operationId"/> routes through the append fault-point variant when set.</summary>
    internal static void WriteSidecar(
        ArtifactEntry entry, string storeDir, string? operationId,
        Func<string, string> sanitizeName, Func<ArtifactEntry, string> serialize)
    {
        // Unconditional backstop, deliberately below every caller. After Task 3's forward-fill
        // skip and this task's two gates it should be unreachable from any path we enumerated --
        // and the previous Critical is precisely what happens when a trigger is closed and the
        // mechanism behind it is left reachable by a route nobody listed.
        if (ArtifactSidecarPolicy.IsReadOnly(entry))
            throw new ScopeReadOnlyException(
                storeDir, entry.Name, entry.SchemaVersion ?? 1, DurableFormats.ArtifactSidecar.Current);

        string metaPath = Path.Combine(storeDir, sanitizeName(entry.Name) + ".meta.json");
        string json = serialize(entry);
        if (operationId is null)
            DurableFile.AtomicWriteAllText(metaPath, json);
        else
            DurableFile.AtomicWriteAllTextForAppend(
                metaPath, json, operationId,
                AppendFaultPoint.LiveSidecarWrite,
                AppendFaultPoint.LiveSidecarFlush);
    }
}
```

Confirm the namespaces/types resolve: `ArtifactEntry` ∈ `Scrinia.Core.Models`, `ArtifactSidecarPolicy`/`ScopeReadOnlyException`/`DurableFormats` ∈ `Scrinia.Core.Formats`, `DurableFile`/`AppendFaultPoint` ∈ `Scrinia.Core` (same namespace), `Path` ∈ `System.IO` (add `using System.IO;`), `Func<>` ∈ `System` (add `using System;`).

## W2 — Wire into `FileMemoryStore`

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the current `WriteSidecarCore` method body (currently lines ~1600–1619) with a one-line forwarder that injects the two store statics as hooks:

```csharp
private static void WriteSidecarCore(ArtifactEntry entry, string storeDir, string? operationId = null) =>
    DurableWriter.WriteSidecar(entry, storeDir, operationId, SanitizeNameCore, SerializeSidecar);
```

Delete the old body (the `if (ArtifactSidecarPolicy.IsReadOnly...)` guard, the `metaPath`/`json` construction, and the `DurableFile.AtomicWriteAllText` / `AtomicWriteAllTextForAppend` branch). Leave EVERYTHING ELSE untouched: `SanitizeNameCore` (482), `SerializeSidecar` (1621), `WriteSidecar` (1589), `WriteSidecars` (1553), `PersistMigratedEntry` (1541), `ExpandLegacyIndexEntries` (1658), `DeleteSidecar` (1701), the three direct `SerializeSidecar` call sites (~2312, ~2438, ~2777), and the migration callers `StoreLayoutMigration.AtomicWriteAllText`/`StoreLayoutMigration.ExpandLegacyIndexEntries`.

To locate the exact current body, read `FileMemoryStore.cs` lines 1592–1620. Confirm `SanitizeNameCore` (482) and `SerializeSidecar` (1621) signatures match the injected hook types `Func<string,string>` and `Func<ArtifactEntry,string>` respectively.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteSidecar|FullyQualifiedName~DurableWriter|FullyQualifiedName~PersistMigratedEntry|FullyQualifiedName~ExpandLegacyIndexEntries|FullyQualifiedName~SaveIndex|FullyQualifiedName~Upsert"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns the sidecar durable-write leaf; `FileMemoryStore.WriteSidecarCore` is a one-line forwarder injecting `SanitizeNameCore` + `SerializeSidecar`.
- No new public API; `FileStoreJsonContext` (private nested source-gen context) untouched; `SanitizeNameCore`/`SerializeSidecar` bodies untouched; `DurableFile` atomic family untouched.
- Full suite green.
