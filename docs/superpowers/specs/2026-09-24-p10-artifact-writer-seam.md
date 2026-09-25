# P10 — Artifact-Body Durable-Write Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P9 complete). P9 extracted the sidecar durable-write leaf (`WriteSidecarCore` → `DurableWriter`).
**Phase:** P10 (tenth seam extraction; second write-path pass).
**Goal:** move the artifact-body durable writer (`WriteArtifactAsync`, the `.nmp2` body write) off the store into `DurableWriter`, leaving two internal one-line forwarders. This is the sibling of P9's sidecar writer: both persist memory content to disk through `DurableFile`, honoring the append fault points and the append lock.

## Why this phase

`WriteSidecarCore` (P9) writes the memory's `.meta.json` sidecar; `WriteArtifactAsync` writes the memory's `.nmp2` body. Both are the content-persistence leaves of the write path and share `DurableFile` + `AppendFaultPoint` + `RunUnderAppendLock`. P10 extracts the body writer so `DurableWriter` owns both content-persistence leaves.

## The seam

`WriteArtifactAsync` has two internal overloads that forward to a single private writer (the operationId-capable one):
- `internal async Task WriteArtifactAsync(subject, scope, artifactText, ct = default)` — the no-operationId entry (used by the public `MemoryAddress` surface and bundle/import sinks).
- `private async Task WriteArtifactAsync(subject, scope, artifactText, operationId, ct = default)` — the real writer (used internally by the create/re-store/whole-memory/append publish paths, each passing a real `operationId`).

Move the private writer's body into `DurableWriter.WriteArtifact` (static async). Inject three hooks:
- `Func<string, string, string> artifactPath` — the store's `ArtifactPath` (public-ish internal at :633).
- `Action<string> invalidateArtifactCache` — `key => _artifactCache.Invalidate(key)` (the `ArtifactLruCache.Invalidate` leaf, :230, `void`).
- `Action<string, string?, Action> runUnderAppendLock` — the store's `RunUnderAppendLock` (:1029, `void`).

### Update `DurableWriter` (src/Scrinia.Core/DurableWriter.cs)

Append this method to the existing `internal static class DurableWriter` (P9). Add the missing usings (`System`, `System.IO`, `System.Text`, `System.Threading`) to the top of the file.

```csharp
    /// <summary>Atomically write <paramref name="subject"/>'s <c>.nmp2</c> artifact body to
    /// <see cref="ArtifactPath"/> under the append lock, refusing cancellation first and
    /// invalidating the artifact LRU cache after the write. <paramref name="operationId"/> routes
    /// through the append fault-point variant when set.</summary>
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

Copy the body verbatim from the current private `WriteArtifactAsync` (:3736–:3748) — only `ArtifactPath(subject, scope)` → `artifactPath(subject, scope)`, `RunUnderAppendLock(scope, operationId, () => ...)` → `runUnderAppendLock(scope, operationId, () => ...)`, and `_artifactCache.Invalidate(...)` → `invalidateArtifactCache(...)` change.

### Store wiring (FileMemoryStore.cs)

All edits in `src/Scrinia.Core/FileMemoryStore.cs`.

1. **Replace** the current private `WriteArtifactAsync` body (:3736–:3748) with an internal one-line forwarder:
   ```csharp
   internal Task WriteArtifactAsync(string subject, string scope, string artifactText,
       string? operationId, CancellationToken ct = default) =>
       DurableWriter.WriteArtifact(subject, scope, artifactText, operationId, ArtifactPath,
           key => _artifactCache.Invalidate(key), RunUnderAppendLock, ct);
   ```
   Change its visibility from `private` to `internal` (the create/re-store/whole-memory/append publish callers at :2309, :2375, :2453, :3162 call it with a real `operationId`; making it internal is behavior-neutral — it was already reachable internally and the forwarders keep the same call sites resolving).
2. **Leave untouched** the no-operationId internal forwarder (:3732–:3734) — it still forwards to the operationId-capable forwarder with `operationId: null`.
3. **Leave untouched** the public `WriteArtifactAsync(MemoryAddress address, string artifactText, ...)` (IPluginStore surface) and the `// ── Artifact file I/O ──` section header.

Confirm the four internal callers (:2309, :2375, :2453, :3162) call `WriteArtifactAsync(subject, scope, artifact, <operationId>, ct)` — they resolve to the new internal forwarder unchanged.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier (P4), `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore` (retained), `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4) — `_artifactCache` is a DIFFERENT cache (the artifact-text LRU at :249, not the scope index cache; it is hooked, not modified), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures (the internal `WriteArtifactAsync` overloads are NOT on any interface), `DurableFile` (atomic primitive family), `AppendFaultPoint`, and the read-side seam owners (`ListingService`, `SearchService`, `DurableWriter`).

## Acceptance

- `DurableWriter` owns both content-persistence leaves (`WriteSidecar` from P9, `WriteArtifact` from P10); `FileMemoryStore` has zero artifact/sidecar body-write logic (only forwarders + hooks).
- No new public API; `FileStoreJsonContext` untouched; `DurableFile`/`AppendFaultPoint` bodies untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteArtifact|FullyQualifiedName~DurableWriter|FullyQualifiedName~Append|FullyQualifiedName~ArtifactFileIO|FullyQualifiedName~Sidecar"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows; redirect to a log and read the tail). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
