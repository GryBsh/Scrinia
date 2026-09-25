# P15 — Default-.gitignore Durable-Write Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p15-gitignore-write-seam.md`
**Phase:** P15 (fifteenth god-store seam extraction; seventh write-path pass).
**Shape:** one pure static method added to `DurableWriter` + one internal one-line forwarder in the store. Single wave, sequential.

## Workstreams

- W1 — add `DurableWriter.WriteDefaultGitIgnore` (append to the DurableWriter file).
- W2 — `FileMemoryStore.WriteDefaultGitIgnore` → internal forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add `DurableWriter.WriteDefaultGitIgnore`

Edit `src/Scrinia.Core/DurableWriter.cs` (created P9, extended P10–P14).

Append the durable-write leaf (verbatim body from store `WriteDefaultGitIgnore`, replacing the store's `AtomicWriteAllText` forwarder with a direct `DurableFile.AtomicWriteAllText` call):

```csharp
    /// <summary>Writes the default <c>.scrinia/.gitignore</c> content atomically. Pure durable-write
    /// leaf: fixed content constant, no fault injection, no recovery orchestration. Caller must have
    /// already ensured the parent directory exists.</summary>
    internal static void WriteDefaultGitIgnore(string gitIgnorePath)
    {
        string content = """
            # Lock files (runtime artifacts from cross-process file locking)
            **/*.lock

            # Export bundles (generated, can be re-exported)
            exports/

            # Archived versions (auto-pruned, not source-controlled)
            **/versions/

            # Temporary files from interrupted writes
            **/*.tmp

            # Interrupted topic restore journals (recovered on the next access)
            **/.restore-journal.json

            # Vector indexes (derived from topic content; rebuild with `scri reindex`)
            embeddings/

            # Consolidation bookkeeping (rebuilt from sidecar state on next run)
            .last-consolidation
            .consolidate-progress.json
            """.Replace("            ", "");
        DurableFile.AtomicWriteAllText(gitIgnorePath, content);
    }
```

No injected hooks and no new usings required (`System.IO` is already imported from prior phases; `DurableFile` is the same file).

## W2 — Wire into `FileMemoryStore`

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. Locate the exact current `WriteDefaultGitIgnore` body (the `private static void WriteDefaultGitIgnore(string gitIgnorePath)` method) and replace its body with a one-line forwarder:

```csharp
private static void WriteDefaultGitIgnore(string gitIgnorePath) =>
    DurableWriter.WriteDefaultGitIgnore(gitIgnorePath);
```

**Leave untouched:** `EnsureGitIgnore` (the existence check + benign first-write race wrapper), `AtomicWriteAllText` forwarder (retained for `StoreLayoutMigration`), `AtomicWriteAllTextAsync`, `AtomicFileCopy` forwarder.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteDefaultGitIgnore|FullyQualifiedName~EnsureGitIgnore|FullyQualifiedName~DurableWriter|FullyQualifiedName~GitIgnore"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `DurableWriter` owns the default-.gitignore durable write; `FileMemoryStore.WriteDefaultGitIgnore` is a one-line forwarder.
- No new public API; `DurableFile`/`FileStoreJsonContext` untouched.
- Full suite green.
