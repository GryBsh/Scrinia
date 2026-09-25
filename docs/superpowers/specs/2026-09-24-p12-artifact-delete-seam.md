# P12 — Artifact-Body Durable-Delete Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P11 complete; `DurableWriter` owns the sidecar write (P9), artifact-body write (P10), and copy+flush (P11) leaves).
**Phase:** P12 (twelfth seam extraction; fourth write-path pass).
**Goal:** move the artifact-body delete leaf (`DeleteArtifactUnsafe`) off the store into `DurableWriter`, leaving the store a one-line forwarder. This is the counterpart to P10's artifact-body write.

## Why this phase

`DeleteArtifactUnsafe` is the lock-free leaf that deletes a memory's `.nmp2` body (the write lock lives in its public caller `DeleteArtifact`). It pairs with P10's `DurableWriter.WriteArtifact`: content is written by `WriteArtifact` and deleted by `DeleteArtifact`, both owned by `DurableWriter`. The case-insensitive fallback (an NTFS case-sensitivity tie-break pinned by `CaseSensitiveFileStoreTests`) is pure logic and moves verbatim.

## The seam

`DeleteArtifactUnsafe` (current body, `FileMemoryStore.cs` :3754–:3809) is called ONLY by `DeleteArtifact` (:3751, which wraps it in `WithExclusiveLock`). It has exactly two cross-seam dependencies:
- `ArtifactPath(subject, scope)` — the store's path helper (:633).
- `_artifactCache.Invalidate(...)` — the artifact-text LRU cache (:249).

Everything else (the `File.Exists`/`File.Delete`/`Directory.EnumerateFiles` fallback, the ordinal tie-break) is pure and moves verbatim.

### Update `DurableWriter` (src/Scrinia.Core/DurableWriter.cs)

Append this method to the existing `internal static class DurableWriter`.

```csharp
    /// <summary>Delete the <c>.nmp2</c> artifact body for <paramref name="subject"/> in
    /// <paramref name="scope"/>, returning whether a file was removed. Refuses nothing: when the
    /// exact path is absent, it falls back to a case-insensitive directory enumeration with an
    /// ordinal tie-break (see below). <paramref name="artifactPath"/> and <paramref name="invalidateArtifactCache"/>
    /// are injected because they touch the store's path helper and artifact-text LRU cache.</summary>
    internal static bool DeleteArtifact(string subject, string scope,
        Func<string, string, string> artifactPath, Action<string> invalidateArtifactCache)
    {
        string path = artifactPath(subject, scope);
        if (!File.Exists(path))
        {
            // RULING (remote-mode-leftovers, final fixes, Finding 2): this fallback is the exact
            // counterpart of the one in Remove above.
            //
            // forget() calls Remove and DeleteArtifact as a pair (MemoryTools.Core.cs). Remove's
            // name match is case-insensitive; this one was not. On a case-sensitive filesystem
            // forget("mixedcase") against a memory stored as "MixedCase" therefore returned
            // removed=true, deleted=false — the caller reported "Forgot: mixedcase." and the tool
            // answered success, while MixedCase.nmp2 stayed on
            // disk with its sidecar gone: orphaned, unreachable through list() or search(), and
            // invisible to the next forget(). Before the sidecar lookup became case-insensitive both
            // halves returned false and the user got a clean "no artifact found"; fixing one half
            // and not the other turned a correct no-op into a partial delete reported as success.
            //
            // Fixed here rather than at the three forget() call sites: DeleteArtifact is a public
            // store primitive, the pairing with Remove is what has to agree, and the forget path
            // path (MemoryTools.Core.cs) sweeps every read scope with a name taken from an address and
            // has no entry in hand to canonicalize from. Aligning the primitive fixes all callers,
            // including any added later; canonicalizing at call sites fixes only the ones edited.
            //
            // Pinned by CaseSensitiveFileStoreTests.DeleteArtifact_DeletesCaseMismatchedName in
            // tests/Scrinia.Tests, which runs against a directory carrying NTFS's per-directory
            // case-sensitivity flag — so this is exercised on Windows, not only on a Linux CI leg.
            // The test skips (never silently passes) on a volume that cannot provide the flag. NTFS
            // folds case by default, so on an ordinary Windows volume the probe above always
            // succeeds and this branch is never entered; the guard test's directory opts out of
            // that default, which is what exercises it.
            //
            // Tie-break: identical to Remove's, and it matters for the same reason — this is a
            // delete, a case-sensitive filesystem holding pre-branch data can hold both
            // "Note.nmp2" and "note.nmp2", and this branch is only entered when the caller asked
            // for neither exactly. Directory enumeration order is not guaranteed, so candidates are
            // sorted ordinally and the first is taken. Ordinal sort puts uppercase first, so
            // "Note.nmp2" wins over "note.nmp2" — the same coin, decided the same way, as Remove's.
            string dir = Path.GetDirectoryName(path)!;
            string wanted = Path.GetFileName(path);
            string? actual = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.nmp2")
                    .Where(p => Path.GetFileName(p).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
            if (actual is null) return false;
            path = actual;
        }

        File.Delete(path);
        // Invalidate covers the differently-cased key too: ArtifactLruCache.Invalidate matches its
        // prefix with OrdinalIgnoreCase, and read keys are "{scope}|{subject}|{ticks}".
        invalidateArtifactCache($"{scope}|{subject}|");
        return true;
    }
```

Copy the body verbatim from `FileMemoryStore.cs` :3754–:3809; only `ArtifactPath(subject, scope)` → `artifactPath(subject, scope)` and `_artifactCache.Invalidate(...)` → `invalidateArtifactCache(...)` change. Preserve the full comment block verbatim (the `CaseSensitiveFileStoreTests` pin depends on this logic being behavior-identical).

### Store wiring (FileMemoryStore.cs)

All edits in `src/Scrinia.Core/FileMemoryStore.cs`. Replace the `DeleteArtifactUnsafe` body (:3754–:3809) with a one-line forwarder:

```csharp
private bool DeleteArtifactUnsafe(string subject, string scope) =>
    DurableWriter.DeleteArtifact(subject, scope, ArtifactPath, key => _artifactCache.Invalidate(key));
```

Leave `DeleteArtifact` (:3749–:3751 — the lock wrapper, unchanged) and its two callers (:460, :2365) untouched.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier (P4), `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `_artifactCache` (ArtifactLruCache — only hooked, not modified), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures (`DeleteArtifactUnsafe` is private; `DeleteArtifact` stays a store method), `DurableFile`/`FileRetry`/`AppendFaultPoint`, `WriteSidecarCore`/`WriteArtifact`/`AtomicFileCopy`/`TryWriteAllText` (P9–P11), and the read-side seam owners (`ListingService`, `SearchService`).

## Acceptance

- `DurableWriter` owns the artifact-body delete leaf; `FileMemoryStore.DeleteArtifactUnsafe` is a one-line forwarder injecting `ArtifactPath` + `_artifactCache.Invalidate`.
- The case-insensitive fallback + ordinal tie-break are byte-identical (comment block preserved verbatim).
- No new public API; `FileStoreJsonContext`/`DurableFile`/`FileRetry` untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~DeleteArtifact|FullyQualifiedName~DurableWriter|FullyQualifiedName~CaseSensitive|FullyQualifiedName~Remove"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
