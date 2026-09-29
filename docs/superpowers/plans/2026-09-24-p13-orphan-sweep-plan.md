# P13 — Orphan-Sidecar Delete Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p13-orphan-sweep-seam.md`
**Phase:** P13 (thirteenth god-store seam extraction; fifth write-path pass).
**Shape:** one method added to `DurableWriter` (P9–P12) + one call-site edit in `SaveIndexUnsafe`. Single wave, sequential.

## Workstreams

- W1 — add `DurableWriter.SweepOrphanSidecars` (append to the DurableWriter file).
- W2 — `SaveIndexUnsafe` orphan-sweep block → `DurableWriter.SweepOrphanSidecars(...)` call.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add `DurableWriter.SweepOrphanSidecars`

Edit `src/Scrinia.Core/DurableWriter.cs` (created P9, extended P10–P12).

Append the `SweepOrphanSidecars` method (verbatim from `SaveIndexUnsafe` :1778–:1783). The store passes already-sanitized stems, so the method only appends the `.meta.json` suffix and builds the case-insensitive allow-list:

```csharp
    /// <summary>Delete any <c>*.meta.json</c> in <paramref name="storeDir"/> whose stem is not in
    /// <paramref name="keptStems"/> — the orphan sidecar sweep run by <c>SaveIndex</c>.
    /// <paramref name="keptStems"/> must already be sanitized (the caller owns sanitizing, so the
    /// allow-list matches the physical leaf names the read-only gate and canonicalization produced).
    /// The enumeration is snapshotted with <c>.ToList()</c> before any delete so a sidecar written
    /// by the subsequent publish pass is never re-encountered; membership is case-insensitive to
    /// match the case-insensitive read side.</summary>
    internal static void SweepOrphanSidecars(string storeDir, IEnumerable<string> keptStems)
    {
        var expected = new HashSet<string>(
            keptStems.Select(s => s + ".meta.json"),
            StringComparer.OrdinalIgnoreCase);
        foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json").ToList())
            if (!expected.Contains(Path.GetFileName(metaFile)))
                File.Delete(metaFile);
    }
```

Confirm the file's usings include `System` (for `IEnumerable<T>`, `Select`, `HashSet`, `StringComparer`) and `System.IO` (for `Directory`/`Path`/`File`). Both are already imported from P9–P12.

## W2 — Wire into `FileMemoryStore`

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the orphan-sweep block (:1778–:1783) inside `SaveIndexUnsafe` with a single call:

```csharp
        DurableWriter.SweepOrphanSidecars(storeDir, entries.Select(e => SanitizeName(e.Name)));
```

To locate the exact current block, read `FileMemoryStore.cs` lines 1778–1783.

**Leave untouched:** `GetStoreDirForScope` (:1752), `CanonicalizeEntryForScope` (:1760), the read-only gate (:1773–:1776 — must still precede the sweep), `PublishIndexUnsafe` (:1785), `SanitizeName` (retained store method), `SaveIndex` (public :1745), and every other store member.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SweepOrphanSidecars|FullyQualifiedName~DurableWriter|FullyQualifiedName~SaveIndex|FullyQualifiedName~OrphanSidecar"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns the orphan-sidecar delete; `SaveIndexUnsafe` calls `DurableWriter.SweepOrphanSidecars` with sanitized stems.
- The `.ToList()` snapshot + case-insensitive allow-list + delete predicate are byte-identical (both pinned by `SaveIndexOrphanSidecarTests` / `TransactionalRestoreTests`).
- No new public API; `FileStoreJsonContext`/`DurableFile`/`FileRetry` untouched.
- Full suite green.
