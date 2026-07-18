# Code Review Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the confirmed findings from the full-codebase review across five tiers, verify-first and test-driven, on one branch delivered as a single PR.

**Architecture:** Each fix is an atomic commit preceded by a failing regression test (TDD). Verified findings (Tiers 1, 2, 5 and the confirmed Tier-3/4 items) carry full test+fix code. Not-yet-confirmed Tier-3/4 items begin with a concrete VERIFY step that either confirms the failure (proceed to TDD) or records a dismissal.

**Tech Stack:** .NET 10 (C#), xUnit, React + Vite + TypeScript (web), git.

## Global Constraints

- Branch: `fix/code-review-remediation` (already created off `main`). Do NOT branch again.
- Keep `dotnet build Scrinia.sln` green and `dotnet test Scrinia.sln` passing.
- `Scrinia.sln` restore fails on a pre-existing `Scrinia.AppHost` NuGetAudit (MessagePack) error — build/test with `-p:NuGetAudit=false`, or target the in-scope project directly (e.g. `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false`).
- New JSON-serialized types → register in the matching source-gen context (`ScriniaMcpJsonContext`, `ServerJsonContext`, `CliJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`).
- New helpers on MCP tool types stay `internal`/`private` (never a bare `public` method on a `WithTools<T>` type).
- One atomic commit per task. Commit message trailer:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g`
- Verify-first items: if a VERIFY step shows the finding is a false positive or intentional design, DO NOT edit code. Record the disposition in the findings ledger (Task 25) and move on.

---

## Phase 1 — Verified high-severity (Tier 1)

### Task 1: Merge driver — real 3-way merge, no field loss, no NRE

**Files:**
- Modify: `src/Scrinia.Merge/MetaJsonMerger.cs`
- Test: `tests/Scrinia.Merge.Tests/MetaJsonMerger3WayTests.cs` (create)

**Interfaces:**
- Consumes: `MetaJsonMerger.Merge(string ancestorPath, string oursPath, string theirsPath, MergeConfig config)`, `MetaJsonMerger.MergeResult`, `MergeConfig.JaccardThreshold`.
- Produces: unchanged public signature; adds private `TryMergeField<T>`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Xunit;

namespace Scrinia.Merge.Tests;

public class MetaJsonMerger3WayTests
{
    private static string WriteMeta(string dir, string file, object meta)
    {
        string path = Path.Combine(dir, file);
        File.WriteAllText(path, JsonSerializer.Serialize(meta));
        return path;
    }

    [Fact]
    public void TheirsOnlyDescriptionChange_IsPreserved_NotDropped()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        var baseMeta = new { Name = "n", Uri = (string?)null, OriginalBytes = 10, ChunkCount = 1,
            CreatedAt = "2026-01-01T00:00:00Z", Description = "old", Keywords = new[] { "a", "b" }, UpdatedAt = "2026-01-01T00:00:00Z" };
        string ancestor = WriteMeta(dir, "base.json", baseMeta);
        string ours = WriteMeta(dir, "ours.json", baseMeta); // ours == ancestor (unchanged)
        string theirs = WriteMeta(dir, "theirs.json", baseMeta with { });
        // theirs changes only Description
        File.WriteAllText(theirs, JsonSerializer.Serialize(new { baseMeta.Name, baseMeta.Uri, baseMeta.OriginalBytes,
            baseMeta.ChunkCount, baseMeta.CreatedAt, Description = "new-from-theirs", baseMeta.Keywords, UpdatedAt = "2026-02-01T00:00:00Z" }));

        var result = MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig());

        Assert.Equal(MetaJsonMerger.MergeResult.Resolved, result);
        var merged = JsonSerializer.Deserialize<MetaEntry>(File.ReadAllText(ours))!;
        Assert.Equal("new-from-theirs", merged.Description); // BUG: currently keeps "old"
    }

    [Fact]
    public void BothSidesChangeDescriptionDifferently_IsConflict()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        object mk(string desc, string upd) => new { Name = "n", Uri = (string?)null, OriginalBytes = 10, ChunkCount = 1,
            CreatedAt = "2026-01-01T00:00:00Z", Description = desc, Keywords = new[] { "a", "b" }, UpdatedAt = upd };
        string ancestor = WriteMeta(dir, "base.json", mk("old", "2026-01-01T00:00:00Z"));
        string ours = WriteMeta(dir, "ours.json", mk("ours-desc", "2026-02-01T00:00:00Z"));
        string theirs = WriteMeta(dir, "theirs.json", mk("theirs-desc", "2026-03-01T00:00:00Z"));

        var result = MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig());

        Assert.Equal(MetaJsonMerger.MergeResult.Conflict, result);
    }

    [Fact]
    public void NullOursDeserialization_ReturnsConflict_NoThrow()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string ancestor = WriteMeta(dir, "base.json", new { Name = "n", Uri = (string?)null, OriginalBytes = 1, ChunkCount = 1,
            CreatedAt = (string?)null, Description = (string?)null, Keywords = new[] { "a" }, UpdatedAt = (string?)null });
        string ours = Path.Combine(dir, "ours.json"); File.WriteAllText(ours, "null");
        string theirs = WriteMeta(dir, "theirs.json", new { Name = "n", Uri = (string?)null, OriginalBytes = 1, ChunkCount = 1,
            CreatedAt = (string?)null, Description = "x", Keywords = new[] { "a" }, UpdatedAt = (string?)null });

        var ex = Record.Exception(() => MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig()));

        Assert.Null(ex); // BUG: currently NREs on `ours!`
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false --filter MetaJsonMerger3WayTests`
Expected: FAIL — `TheirsOnlyDescriptionChange` asserts "new-from-theirs" but gets "old"; `NullOursDeserialization` throws NRE.

- [ ] **Step 3: Rewrite `Merge` for a true 3-way merge**

Replace the body of `Merge` (currently `MetaJsonMerger.cs:24-61`) with:

```csharp
public static MergeResult Merge(string ancestorPath, string oursPath, string theirsPath, MergeConfig config)
{
    var ours = JsonSerializer.Deserialize(File.ReadAllText(oursPath), MetaEntryJsonContext.Default.MetaEntry);
    var theirs = JsonSerializer.Deserialize(File.ReadAllText(theirsPath), MetaEntryJsonContext.Default.MetaEntry);
    var ancestor = File.Exists(ancestorPath)
        ? JsonSerializer.Deserialize(File.ReadAllText(ancestorPath), MetaEntryJsonContext.Default.MetaEntry)
        : null;

    // A missing/invalid side can't be auto-merged safely — let git surface the conflict.
    if (ours is null || theirs is null)
        return MergeResult.Conflict;

    double jaccard = KeywordJaccard.Compute(ours.Keywords, theirs.Keywords);
    if (jaccard < config.JaccardThreshold)
        return MergeResult.Conflict;

    // Per-field 3-way merge of scalar metadata: if only one side changed a field, take that
    // side; if both changed it differently, it's a genuine conflict.
    if (!TryMergeField(ancestor?.Name, ours.Name, theirs.Name, out var name)) return MergeResult.Conflict;
    if (!TryMergeField(ancestor?.Uri, ours.Uri, theirs.Uri, out var uri)) return MergeResult.Conflict;
    if (!TryMergeField(ancestor?.Description, ours.Description, theirs.Description, out var description)) return MergeResult.Conflict;
    if (!TryMergeField(ancestor?.OriginalBytes, ours.OriginalBytes, theirs.OriginalBytes, out var originalBytes)) return MergeResult.Conflict;
    if (!TryMergeField(ancestor?.ChunkCount, ours.ChunkCount, theirs.ChunkCount, out var chunkCount)) return MergeResult.Conflict;

    // Union keywords (already known similar by Jaccard).
    var keywords = new HashSet<string>(ours.Keywords ?? [], StringComparer.OrdinalIgnoreCase);
    foreach (var kw in theirs.Keywords ?? [])
        keywords.Add(kw);

    string? updatedAt = CompareTimestamps(ours.UpdatedAt, theirs.UpdatedAt) > 0 ? ours.UpdatedAt : theirs.UpdatedAt;
    string? createdAt = CompareTimestamps(ours.CreatedAt, theirs.CreatedAt) < 0 ? ours.CreatedAt : theirs.CreatedAt;

    var merged = ours with
    {
        Name = name,
        Uri = uri,
        Description = description,
        OriginalBytes = originalBytes,
        ChunkCount = chunkCount,
        Keywords = keywords.Order().ToArray(),
        UpdatedAt = updatedAt,
        CreatedAt = createdAt,
    };

    File.WriteAllText(oursPath, JsonSerializer.Serialize(merged, MetaEntryJsonContext.Default.MetaEntry));
    return MergeResult.Resolved;
}

// Returns false when both sides changed the field to different values (real conflict).
private static bool TryMergeField<T>(T ancestor, T ours, T theirs, out T result)
{
    var cmp = EqualityComparer<T>.Default;
    if (cmp.Equals(ours, theirs)) { result = ours; return true; }        // same on both sides
    if (cmp.Equals(ours, ancestor)) { result = theirs; return true; }    // only theirs changed
    if (cmp.Equals(theirs, ancestor)) { result = ours; return true; }    // only ours changed
    result = ours; return false;                                          // both changed differently
}
```

Note: `OriginalBytes`/`ChunkCount` are `int`; `ancestor?.OriginalBytes` is `int?`, so those two calls resolve `T = int?` and `out var` is `int?`. Assigning `int?` into the `int` record field requires a value — since a resolved merge always has a non-null result, change those two `TryMergeField` calls to unwrap:

```csharp
    if (!TryMergeField(ancestor?.OriginalBytes, (int?)ours.OriginalBytes, theirs.OriginalBytes, out var originalBytesN)) return MergeResult.Conflict;
    if (!TryMergeField(ancestor?.ChunkCount, (int?)ours.ChunkCount, theirs.ChunkCount, out var chunkCountN)) return MergeResult.Conflict;
    int originalBytes = originalBytesN ?? ours.OriginalBytes;
    int chunkCount = chunkCountN ?? ours.ChunkCount;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false --filter MetaJsonMerger3WayTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Merge/MetaJsonMerger.cs tests/Scrinia.Merge.Tests/MetaJsonMerger3WayTests.cs
git commit -m "fix(merge): true 3-way merge for .meta.json; stop dropping theirs' fields and NRE on null ours"
```

---

### Task 2: Bundle import — close the zip-slip on the category key

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs:344-376`
- Test: `tests/Scrinia.Tests/BundleZipSlipTests.cs` (create)

**Interfaces:**
- Consumes: `BundleFormatService` public import entry point (the method that eventually calls `ImportFileEntities`). Locate it by reading the file; the test drives import through the public API used elsewhere in `Scrinia.Tests` for bundles.
- Produces: no signature change; adds a private `IsWithin(baseDir, candidate)` helper (or reuses the shared path helper from Task 8 if Task 8 lands first — see note).

- [ ] **Step 1: Write the failing test**

Craft a bundle whose manifest `fileEntities` key contains `..` and assert nothing is written outside the store base dir. Read `BundleFormatService` first to reuse its existing bundle-writing test helper; if the test project already has a bundle round-trip helper, extend it. Skeleton:

```csharp
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Scrinia.Tests;

public class BundleZipSlipTests
{
    [Fact]
    public void ImportBundle_WithTraversalCategoryKey_WritesNothingOutsideStore()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string storeDir = Path.Combine(root, "workspace");
        Directory.CreateDirectory(storeDir);
        string sentinelDir = Path.Combine(root, "outside");
        Directory.CreateDirectory(sentinelDir);

        string bundlePath = Path.Combine(root, "evil.scrinia-bundle");
        using (var zip = ZipFile.Open(bundlePath, ZipArchiveMode.Create))
        {
            // manifest with a traversal category key
            var manifest = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(manifest.Open()))
                w.Write("{\"version\":2,\"fileEntities\":{\"../outside\":[\"pwned.md\"]}}");
            var payload = zip.CreateEntry("files/../outside/pwned.md");
            using (var w = new StreamWriter(payload.Open()))
                w.Write("owned");
        }

        // ACT: import via the same public path other bundle tests use (adjust to real API).
        TryImportBundle(storeDir, bundlePath);

        Assert.False(File.Exists(Path.Combine(sentinelDir, "pwned.md")),
            "zip-slip: file written outside the store base dir");
    }

    // Replace with the real import call discovered by reading BundleFormatService + existing bundle tests.
    private static void TryImportBundle(string storeDir, string bundlePath) { /* wire to public import API */ }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter BundleZipSlipTests`
Expected: FAIL — `pwned.md` exists in the sentinel dir.

- [ ] **Step 3: Validate the category key against the base dir**

In `ImportFileEntities`, after `string categoryDir = Path.Combine(baseDir, category);` (line 350) and before `Directory.CreateDirectory(categoryDir)`, add a containment check against `baseDir` (not against the already-escaped `categoryDir`):

```csharp
string categoryDir = Path.Combine(baseDir, category);
string fullBase = Path.GetFullPath(baseDir);
string fullCategoryDir = Path.GetFullPath(categoryDir);
if (!IsWithin(fullBase, fullCategoryDir))
    continue; // reject traversal in the manifest category key
Directory.CreateDirectory(categoryDir);
```

Add the helper (private static) at the bottom of the class:

```csharp
private static bool IsWithin(string baseFull, string candidateFull)
{
    string withSep = baseFull.EndsWith(Path.DirectorySeparatorChar)
        ? baseFull : baseFull + Path.DirectorySeparatorChar;
    return candidateFull.StartsWith(withSep, StringComparison.OrdinalIgnoreCase)
        || string.Equals(candidateFull, baseFull, StringComparison.OrdinalIgnoreCase);
}
```

Also change the existing per-file check at line 371 to baseline against `fullBase` too:

```csharp
if (!IsWithin(fullBase, fullDest))
    continue;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter BundleZipSlipTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/BundleZipSlipTests.cs
git commit -m "fix(bundles): validate manifest category key against store base dir (zip-slip)"
```

---

### Task 3: VectorStore.GetVectors — return a snapshot, not the live list

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/VectorStore.cs:75-97`
- Test: `tests/Scrinia.Tests/VectorStoreConcurrencyTests.cs` (create)

**Interfaces:**
- Consumes: `VectorStore(string baseDir, string? expectedSignature = null)`, `GetVectors(string scope) : IReadOnlyList<VectorEntry>`, `UpsertAsync(scope, name, chunkIndex, float[], ct)`.
- Produces: `GetVectors` now returns a defensive copy; callers already treat it read-only.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests;

public class VectorStoreConcurrencyTests
{
    [Fact]
    public async Task GetVectors_WhileUpserting_DoesNotThrowOrTear()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        var store = new VectorStore(dir);
        const string scope = "ephemeral"; // ephemeral avoids disk persistence noise
        for (int i = 0; i < 50; i++)
            await store.UpsertAsync(scope, $"m{i}", null, new float[] { i, i, i });

        var writer = Task.Run(async () =>
        {
            for (int i = 50; i < 500; i++)
                await store.UpsertAsync(scope, $"m{i}", null, new float[] { i, i, i });
        });

        var ex = await Record.ExceptionAsync(async () =>
        {
            for (int r = 0; r < 500; r++)
            {
                foreach (var v in store.GetVectors(scope)) { _ = v.Vector.Length; }
                await Task.Yield();
            }
        });
        await writer;

        Assert.Null(ex); // BUG: throws InvalidOperationException (Collection was modified)
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter VectorStoreConcurrencyTests`
Expected: FAIL — `InvalidOperationException: Collection was modified` (may be flaky; re-run to confirm the race exists).

- [ ] **Step 3: Return a copy under the scope lock**

In `GetVectors` (VectorStore.cs:75), change both return sites to hand back a snapshot. Replace the method body:

```csharp
public IReadOnlyList<VectorEntry> GetVectors(string scope)
{
    if (_scopeVectors.TryGetValue(scope, out var cached))
        return cached.ToArray(); // snapshot: callers must not observe concurrent mutation

    var lk = GetLock(scope);
    if (!lk.Wait(TimeSpan.FromSeconds(30)))
        throw new TimeoutException("VectorStore lock acquisition timed out after 30 seconds.");
    try
    {
        if (_scopeVectors.TryGetValue(scope, out cached))
            return cached.ToArray();

        var loaded = LoadFromDisk(scope);
        _scopeVectors[scope] = loaded;
        return loaded.ToArray();
    }
    finally
    {
        lk.Release();
    }
}
```

(The fast-path `ToArray()` at line 78 is the important one — it runs without the lock, so it must copy. `List<T>.ToArray()` can itself tear during a concurrent structural mutation on some runtimes; if the concurrency test still flakes, wrap the fast path to also take `GetLock(scope)` before copying.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter VectorStoreConcurrencyTests` (run 3×)
Expected: PASS consistently.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Embeddings/VectorStore.cs tests/Scrinia.Tests/VectorStoreConcurrencyTests.cs
git commit -m "fix(embeddings): VectorStore.GetVectors returns a snapshot to stop concurrent-modification races"
```

---

### Task 4: Embeddings pipeline logger must not write to stdout in `serve`

**Files:**
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:58`
- Test: manual/integration note (MCP stdio framing is not unit-testable here)

**Interfaces:**
- Consumes: `LoadPluginsAsync(CancellationToken)`.
- Produces: the embeddings logger writes to stderr, never stdout.

- [ ] **Step 1: Verify the failure**

Confirm at `WorkspaceSetup.cs:58` the factory is `LoggerFactory.Create(b => b.AddConsole()...)` and that `logger` flows into the embeddings handlers that call `LogWarning` at runtime (`CoreEmbeddingEventHandler.cs:74,92`). `AddConsole()` writes to stdout, which is the JSON-RPC transport under `scri serve`.

- [ ] **Step 2: Redirect console logs to stderr**

Replace line 58:

```csharp
var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
    .SetMinimumLevel(LogLevel.Warning));
```

`LogToStandardErrorThreshold = LogLevel.Trace` routes every level to stderr, keeping stdout clean for the MCP frame.

- [ ] **Step 3: Verify build + smoke test**

Run: `dotnet build src/Scrinia/Scrinia.csproj -p:NuGetAudit=false`
Expected: build succeeds. Then confirm no `AddConsole()` without the stderr threshold remains in the serve path:
Run: `git grep -n "AddConsole" -- src/Scrinia`
Expected: only the modified call, now with `LogToStandardErrorThreshold`.

- [ ] **Step 4: Commit**

```bash
git add src/Scrinia/Commands/WorkspaceSetup.cs
git commit -m "fix(cli): route embeddings logger to stderr so it can't corrupt the MCP stdio frame"
```

---

### Task 5: Web login probes an endpoint the key is authorized for

**Files:**
- Modify: `web/src/pages/LoginPage.tsx:18-25`
- Test: manual note (see Step 4); add a Vitest case only if `web/` already has a test harness (check `web/package.json`).

**Interfaces:**
- Consumes: the API client's auth header convention (`Authorization: Bearer <key>`), an endpoint any authenticated key can hit.
- Produces: login validity check no longer depends on the `ManageKeys` policy.

- [ ] **Step 1: Verify the failure**

Confirm `/api/v1/keys` is gated by `.RequireAuthorization("ManageKeys")` (`KeyEndpoints.cs:12-13`) and that `LoginPage` validates against it. Choose the replacement probe: a memory-list endpoint that only requires a valid authenticated key (read the server's memory endpoints group policy to confirm the least-privilege authenticated route — e.g. `GET /api/v1/stores/default/memories?limit=1`). If no non-admin authenticated GET exists, use `/health/details` if it requires auth, else keep `/health/ready` but treat any non-401 as valid.

- [ ] **Step 2: Point the probe at the authenticated read route**

Replace the fetch URL and status handling (LoginPage.tsx:18-25) with the confirmed least-privilege route, e.g.:

```tsx
const resp = await fetch('/api/v1/stores/default/memories?limit=1', {
  headers: { Authorization: `Bearer ${key.trim()}` },
})
if (resp.status === 401) {
  setError('Invalid API key.')
  localStorage.removeItem('scrinia-api-key')
  return
}
// 403 no longer means "invalid key" — a valid non-admin key may lack some scopes but can log in.
```

(Use the exact route confirmed in Step 1; drop `403` from the invalid-key branch.)

- [ ] **Step 3: Build the web app**

Run: `cd web && npm run build`
Expected: build succeeds, no type errors.

- [ ] **Step 4: Manual verification note**

Record in the PR: "Verified a read/write-scoped key without `manage_keys` can now log in (previously 403 → rejected)." If a Vitest harness exists, add a test mocking `fetch` to return 403 and asserting the user is NOT logged out.

- [ ] **Step 5: Commit**

```bash
git add web/src/pages/LoginPage.tsx
git commit -m "fix(web): validate login against an authenticated read route, not the ManageKeys-gated keys endpoint"
```

---

### Task 6: EmbeddingsPlugin.EnsureInitialized — guard against concurrent double-init

**Files:**
- Modify: `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs:70` (and the `_disposed` interaction, ~line 38)
- Test: `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginInitTests.cs` (create) — only if it can run without a GPU; otherwise a code-level review note.

**Interfaces:**
- Consumes: `EnsureInitialized()` and the fields it assigns (`_provider`, `_vectorStore`, `_disposed`).
- Produces: `EnsureInitialized` is idempotent under concurrency.

- [ ] **Step 1: Verify the failure**

Read `EmbeddingsPlugin.cs` around 30-80. Confirm `EnsureInitialized` is `if (_vectorStore is not null) return;` then unsynchronized creation, callable concurrently, and that `Dispose` sets `_disposed` without `EnsureInitialized` checking it.

- [ ] **Step 2: Add a lock (or `Lazy`) and a disposed check**

Introduce a gate. Add field `private readonly object _initLock = new();` and rewrite `EnsureInitialized`:

```csharp
private void EnsureInitialized()
{
    if (_vectorStore is not null) return;
    lock (_initLock)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EmbeddingsPlugin));
        if (_vectorStore is not null) return;
        var provider = VulkanEmbeddingProvider.Create(/* existing args */);
        var vectorStore = new VectorStore(/* existing args */);
        _provider = provider;
        _vectorStore = vectorStore; // assign LAST: it's the initialized sentinel
    }
}
```

(Preserve the exact constructor argument lists already present in the file. Assign `_vectorStore` last so the double-checked `is not null` guard is only true after full init.)

Ensure `Dispose` takes the same lock when tearing down:

```csharp
public void Dispose()
{
    lock (_initLock)
    {
        if (_disposed) return;
        _disposed = true;
        _provider?.Dispose();
        _vectorStore = null;
    }
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj -p:NuGetAudit=false`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs
git commit -m "fix(plugin): lock EmbeddingsPlugin init to prevent concurrent double GPU model load"
```

---

## Phase 2 — Systemic path-prefix helper (Tier 2)

### Task 7: Introduce a shared `PathContainment` helper

**Files:**
- Create: `src/Scrinia.Core/PathContainment.cs`
- Test: `tests/Scrinia.Tests/PathContainmentTests.cs` (create)

**Interfaces:**
- Produces: `public static class PathContainment { public static bool IsWithin(string baseDir, string candidate); }` returning true iff the fully-resolved `candidate` is `baseDir` or nested under it, using a trailing-separator-safe prefix check.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class PathContainmentTests
{
    [Theory]
    [InlineData("/root/.scrinia", "/root/.scrinia/a.md", true)]
    [InlineData("/root/.scrinia", "/root/.scrinia", true)]
    [InlineData("/root/.scrinia", "/root/.scrinia-backup/x", false)] // sibling sharing the prefix
    [InlineData("/root/.scrinia", "/root/other/x", false)]
    public void IsWithin_RespectsDirectoryBoundary(string baseDir, string candidate, bool expected)
    {
        Assert.Equal(expected, PathContainment.IsWithin(baseDir, candidate));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter PathContainmentTests`
Expected: FAIL — `PathContainment` does not exist.

- [ ] **Step 3: Implement the helper**

```csharp
namespace Scrinia.Core;

/// <summary>Trailing-separator-safe path containment check for filesystem sandboxing.</summary>
public static class PathContainment
{
    /// <summary>True iff <paramref name="candidate"/> resolves to <paramref name="baseDir"/> or a path nested under it.</summary>
    public static bool IsWithin(string baseDir, string candidate)
    {
        string baseFull = Path.GetFullPath(baseDir);
        string candFull = Path.GetFullPath(candidate);
        if (string.Equals(baseFull, candFull, StringComparison.OrdinalIgnoreCase))
            return true;
        string withSep = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;
        return candFull.StartsWith(withSep, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter PathContainmentTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/PathContainment.cs tests/Scrinia.Tests/PathContainmentTests.cs
git commit -m "feat(core): add trailing-separator-safe PathContainment.IsWithin helper"
```

---

### Task 8: Apply `PathContainment.IsWithin` at the three sandbox sites

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1251`
- Modify: `src/Scrinia.Mcp/MemoryTools.Bundling.cs:90`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:1593`
- Test: extend `tests/Scrinia.Tests/PathContainmentTests.cs` is not enough; add targeted tests where each call site is reachable (see Step 1).

**Interfaces:**
- Consumes: `Scrinia.Core.PathContainment.IsWithin` (Task 7).
- Produces: each site now rejects sibling-prefix escapes.

- [ ] **Step 1: Verify each site + write a failing test where reachable**

For each of the three locations, read ±10 lines and confirm the current check is a `StartsWith(root)` without a trailing separator. Add a regression test for the one with the simplest public surface first — `FileMemoryStore.cs:1251` (`file://` sandbox in `ResolveArtifactAsync`): assert that resolving `file://{workspace}/.scrinia-backup/secret` is rejected while `file://{workspace}/.scrinia/ok` resolves. Read `FileMemoryStore` to find the exact method/signature and mirror an existing `Scrinia.Tests` FileMemoryStore test for setup.

- [ ] **Step 2: Replace each check**

At each site, replace the ad-hoc `fullPath.StartsWith(root, ...)` with:

```csharp
if (!Scrinia.Core.PathContainment.IsWithin(root, fullPath))
    // ... existing reject/continue/return branch unchanged ...
```

Use the existing `root`/`fullPath` variable names at each site (they differ: `scriniDir`/`filePath` at FileMemoryStore.cs:1251; `workspaceRoot`/resolved bundle path at MemoryTools.Bundling.cs:90; `workspaceRoot`/`fullPath` at MemoryTools.Core.cs:1593). Preserve each site's existing reject action (return null / continue / throw).

- [ ] **Step 3: Run tests + build**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter FileMemoryStore`
Then: `dotnet build src/Scrinia.Mcp/Scrinia.Mcp.csproj -p:NuGetAudit=false`
Expected: new test PASS; build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Mcp/MemoryTools.Bundling.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/
git commit -m "fix(security): use trailing-separator-safe containment at all path-sandbox sites"
```

---

## Phase 3 — Confirmed Tier-3/4 (already verified by review reads)

### Task 9: TransientDetector — treat HttpClient timeout as transient

**Files:**
- Modify: `src/Scrinia.Core/Resilience/TransientDetector.cs:23-32`
- Test: `tests/Scrinia.Tests/TransientDetectorTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class TransientDetectorTests
{
    [Fact]
    public void HttpClientTimeout_TaskCanceledWithTimeoutInner_IsTransient()
    {
        var ex = new TaskCanceledException("timed out", new TimeoutException());
        Assert.True(TransientDetector.IsTransient(ex)); // BUG: currently false
    }

    [Fact]
    public void CallerCancellation_TaskCanceledWithoutTimeoutInner_IsNotTransient()
    {
        var ex = new TaskCanceledException("cancelled");
        Assert.False(TransientDetector.IsTransient(ex));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter TransientDetectorTests`
Expected: FAIL — first test returns false.

- [ ] **Step 3: Add the timeout arm**

Add before the `_ => false` arm in `IsTransient(Exception?)`:

```csharp
        OperationCanceledException oce when oce.InnerException is TimeoutException => true,
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter TransientDetectorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Resilience/TransientDetector.cs tests/Scrinia.Tests/TransientDetectorTests.cs
git commit -m "fix(resilience): classify HttpClient timeout (TaskCanceled+TimeoutException) as transient"
```

---

### Task 10: ProcessRunner — decouple stream reads from the caller's token

**Files:**
- Modify: `src/Scrinia.Core/Process/ProcessRunner.cs:41-42`
- Test: `tests/Scrinia.Tests/ProcessRunnerCancellationTests.cs` (create) — guard with an OS/executable check.

- [ ] **Step 1: Write the failing test**

Cancellation should yield `TimedOut = true`, not throw. Use a portable long-running executable.

```csharp
using Scrinia.Core.Process;
using Xunit;

namespace Scrinia.Tests;

public class ProcessRunnerCancellationTests
{
    [Fact]
    public async Task Cancellation_ReturnsTimedOut_NotThrow()
    {
        var runner = new ProcessRunner();
        (string exe, IReadOnlyList<string> args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping -n 20 127.0.0.1 >NUL" })
            : ("sh", new[] { "-c", "sleep 20" });
        if (runner.ResolveExecutable(exe) is null) return; // skip if unavailable

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ex = await Record.ExceptionAsync(async () =>
        {
            var result = await runner.RunAsync(exe, args, stdin: null, cts.Token);
            Assert.True(result.TimedOut);
        });
        Assert.Null(ex); // BUG: currently throws OperationCanceledException from the read tasks
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter ProcessRunnerCancellationTests`
Expected: FAIL — throws OCE.

- [ ] **Step 3: Bind reads to None**

Change lines 41-42 so the read tasks themselves are not cancellable (the `WaitAsync(None)` at 59-60 already handles waiting):

```csharp
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter ProcessRunnerCancellationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Process/ProcessRunner.cs tests/Scrinia.Tests/ProcessRunnerCancellationTests.cs
git commit -m "fix(process): read child stdout/stderr with None token so cancellation yields TimedOut, not throw"
```

---

### Task 11: Nmp2ConflictHandler — insert `conflicted` before exactly one closing brace

**Files:**
- Modify: `src/Scrinia.Merge/Nmp2ConflictHandler.cs:82-85`
- Test: `tests/Scrinia.Merge.Tests/MarkMetaConflictedTests.cs` (create)

- [ ] **Step 1: Write the failing test**

`MarkMetaConflicted` is private. Test through the public conflict-marking path, or make the brace-insertion logic a testable internal helper. Simplest: extract a pure helper `internal static string InsertConflictedFlag(string metaJson)` and test it directly.

```csharp
using System.Text.Json;
using Xunit;

namespace Scrinia.Merge.Tests;

public class MarkMetaConflictedTests
{
    [Fact]
    public void NestedTrailingObject_StaysValidJson_WithConflictedTrue()
    {
        string meta = "{\n  \"name\": \"x\",\n  \"nested\": { \"a\": 1 }\n}";
        string result = Nmp2ConflictHandler.InsertConflictedFlag(meta);
        using var doc = JsonDocument.Parse(result); // BUG: current TrimEnd('}') makes this throw
        Assert.True(doc.RootElement.GetProperty("conflicted").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("nested").GetProperty("a").GetInt32());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false --filter MarkMetaConflictedTests`
Expected: FAIL — parse throws (both trailing braces stripped).

- [ ] **Step 3: Replace with brace-count-safe insertion**

Extract and fix. Replace lines 82-85 of `MarkMetaConflicted`:

```csharp
        string meta = File.ReadAllText(metaPath);
        File.WriteAllText(metaPath, InsertConflictedFlag(meta));
```

Add the internal helper:

```csharp
    internal static string InsertConflictedFlag(string metaJson)
    {
        string trimmed = metaJson.TrimEnd();
        int lastBrace = trimmed.LastIndexOf('}');
        if (lastBrace < 0)
            return metaJson; // not an object; leave untouched
        string head = trimmed[..lastBrace].TrimEnd();
        string sep = head.EndsWith('{') ? "" : ","; // empty object → no leading comma
        return head + sep + "\n  \"conflicted\": true\n}";
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false --filter MarkMetaConflictedTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Merge/Nmp2ConflictHandler.cs tests/Scrinia.Merge.Tests/MarkMetaConflictedTests.cs
git commit -m "fix(merge): mark meta.json conflicted without corrupting nested trailing objects"
```

---

### Task 12: Chat providers — don't count client-cancellation as a breaker failure

**Files:**
- Modify: `src/Scrinia.Server/Chat/Providers/OpenAiChatProvider.cs:69-73`
- Modify: `src/Scrinia.Server/Chat/Providers/AnthropicChatProvider.cs` (same `catch (Exception)` block)
- Modify: `src/Scrinia.Server/Chat/Providers/GeminiChatProvider.cs` (same block)
- Test: `tests/Scrinia.Server.Tests/ChatProviderCancellationTests.cs` (create) if a provider can be exercised with a fake `HttpMessageHandler`; otherwise a shared-helper unit test (see Step 1).

- [ ] **Step 1: Verify + choose test seam**

Confirm all three providers wrap the initial send in `catch (Exception) { _circuitBreaker.RecordFailure(); throw; }` which also catches `OperationCanceledException`. If the providers accept an injectable `HttpClient`, test with a handler that throws `TaskCanceledException`; assert the breaker's failure count is unchanged. If not injectable, keep the fix and cover it via a note.

- [ ] **Step 2: Add a cancellation passthrough before the generic catch**

In each provider, immediately before `catch (Exception)`:

```csharp
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // client disconnect / caller cancel — not a provider health signal
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
```

- [ ] **Step 3: Build + run any added test**

Run: `dotnet build src/Scrinia.Server/Scrinia.Server.csproj -p:NuGetAudit=false`
Expected: build succeeds (and added test PASS).

- [ ] **Step 4: Commit**

```bash
git add src/Scrinia.Server/Chat/Providers/
git commit -m "fix(chat): don't trip the circuit breaker on client-cancelled requests"
```

---

### Task 13: ResilientEmbeddingProvider — dispose the response, don't swallow cancellation

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs:60-104`
- Test: `tests/Scrinia.Tests/ResilientEmbeddingProviderTests.cs` (create) if the provider takes an injectable handler; else build-only.

- [ ] **Step 1: Verify**

Confirm the success `response` (line 60/82) is never disposed and the outer `catch (Exception ex)` (line 96) also catches `OperationCanceledException`, returning null.

- [ ] **Step 2: `using` the response + rethrow caller cancellation**

Wrap the response in `using` and add a cancellation passthrough. Change the success block to dispose:

```csharp
            using var response = await RetryPolicy.ExecuteAsync(
                async () => await SendEmbedRequestAsync(text, ct),
                resp => TransientDetector.IsTransient(resp),
                _retryOptions, Logger, ct);
```

Add before `catch (CircuitBreakerOpenException)`:

```csharp
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation must propagate, not masquerade as an embed failure
        }
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Scrinia.Core/Scrinia.Core.csproj -p:NuGetAudit=false`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs
git commit -m "fix(embeddings): dispose embed HttpResponseMessage and propagate caller cancellation"
```

---

### Task 14: Server request-size limit reaches the 50 MB import ceiling

**Files:**
- Modify: `src/Scrinia.Server/Program.cs:23-30` and the import endpoint registration (`Endpoints/MemoryEndpoints.cs`)
- Test: manual note / integration.

- [ ] **Step 1: Verify**

Confirm Kestrel `MaxRequestBodySize = 10 MB` (Program.cs:25) is below `MultipartBodyLengthLimit = 50 MB` (Program.cs:29), so 10–50 MB imports 413 before the multipart limit applies. Read `MemoryEndpoints.cs` to find the import endpoint.

- [ ] **Step 2: Raise the limit only on the import endpoint**

Keep the global 10 MB default; override just the import route. On the import endpoint's `MapPost(...)` chain add:

```csharp
    .WithMetadata(new Microsoft.AspNetCore.Http.Metadata.RequestSizeLimitMetadata(50 * 1024 * 1024));
```

If that metadata type is unavailable in this framework version, instead set the global Kestrel limit to `50 * 1024 * 1024` at Program.cs:25 and add a code comment that import is the ceiling driver. Prefer the per-endpoint override.

- [ ] **Step 3: Build**

Run: `dotnet build src/Scrinia.Server/Scrinia.Server.csproj -p:NuGetAudit=false`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Scrinia.Server/Program.cs src/Scrinia.Server/Endpoints/MemoryEndpoints.cs
git commit -m "fix(server): allow the import endpoint to accept the intended 50 MB bundle size"
```

---

### Task 15: CircuitBreaker — single-flight the half-open trial

**Files:**
- Modify: `src/Scrinia.Core/Resilience/CircuitBreaker.cs`
- Test: `tests/Scrinia.Tests/CircuitBreakerHalfOpenTests.cs` (create)

- [ ] **Step 1: Write the failing test**

After cooldown, only ONE caller should be admitted to probe; the rest see Open until the probe resolves.

```csharp
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class CircuitBreakerHalfOpenTests
{
    [Fact]
    public void AfterCooldown_OnlyOneTrialRequestAdmitted()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 1, CooldownSeconds: 0));
        cb.RecordFailure(); // opens immediately (threshold 1)
        // cooldown 0 => immediately half-open-eligible
        int admitted = 0;
        for (int i = 0; i < 10; i++)
            if (cb.TryEnterHalfOpen()) admitted++;
        Assert.Equal(1, admitted); // BUG today: no single-flight gate exists
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter CircuitBreakerHalfOpenTests`
Expected: FAIL — `TryEnterHalfOpen` does not exist.

- [ ] **Step 3: Add a single-flight half-open gate**

Add a field `private int _trialInFlight; // 0 = free, 1 = a trial is probing` and method:

```csharp
    /// <summary>
    /// Attempts to claim the single half-open trial slot. Returns true for exactly one caller
    /// after cooldown expiry; others get false until <see cref="RecordSuccess"/> or
    /// <see cref="RecordFailure"/> resolves the trial.
    /// </summary>
    public bool TryEnterHalfOpen()
    {
        if (State != CircuitState.HalfOpen) return false;
        return Interlocked.CompareExchange(ref _trialInFlight, 1, 0) == 0;
    }
```

Reset the slot in `RecordSuccess`, `RecordFailure`, and `Reset`:

```csharp
        Interlocked.Exchange(ref _trialInFlight, 0);
```

(Add that line to each of those three methods. Existing `AllowRequest`/`EnsureClosed` behavior is unchanged; callers that want single-flight opt in via `TryEnterHalfOpen`.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter CircuitBreakerHalfOpenTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Resilience/CircuitBreaker.cs tests/Scrinia.Tests/CircuitBreakerHalfOpenTests.cs
git commit -m "fix(resilience): single-flight the half-open trial to avoid recovery thundering herd"
```

---

### Task 16: WeightedFieldScorer — don't zero out a unique BM25-only match

**Files:**
- Modify: `src/Scrinia.Core/Search/WeightedFieldScorer.cs:197,211`
- Test: `tests/Scrinia.Tests/WeightedFieldScorerBm25NormTests.cs` (create)

- [ ] **Step 1: Verify + write the failing test**

Read WeightedFieldScorer.cs:150-260 to confirm: `bm25Range` is `max-min` forced to ≥1, and `normalizedBm25 = (entryBm25[i]-minBm25)/bm25Range*100`. When a single doc matches, `entryBm25[i]==minBm25==maxBm25` → normalized 0 → if it's the only signal, `relevance<=0` drops it. Confirm the drop happens (TryInsert gate at line 180). Then write a test that searches a corpus where exactly one entry matches a term present only in its indexed body (not name/keywords/preview), BM25-only mode, and asserts it appears in results. Mirror an existing WeightedFieldScorer/search test in `Scrinia.Tests` for corpus setup.

- [ ] **Step 2: Switch entry/chunk BM25 normalization to max-norm**

Match the convention used by `EntityGraphScoreContributor`/`MmrReranker`. Replace line 197:

```csharp
                double normalizedBm25 = maxBm25 > 0 ? entryBm25[i] / maxBm25 * 100.0 : 0;
```

and line 211:

```csharp
                double normalizedChunkBm25 = maxBm25 > 0 ? rawBm25 / maxBm25 * 100.0 : 0;
```

(Confirm `maxBm25` is already in scope near line 197; if the current code only computes `minBm25`/`bm25Range`, add `double maxBm25 = entryBm25.Count > 0 ? entryBm25.Max() : 0;` alongside the existing min/range computation. Keep `bm25Range`/`minBm25` if the RRF path still uses them; otherwise remove the now-dead locals.)

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter WeightedFieldScorerBm25NormTests`
Then the full search suite to catch ranking regressions:
Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter Search`
Expected: new test PASS; no pre-existing search test regresses (if a ranking test asserts exact min-norm scores, update it deliberately and note it).

- [ ] **Step 4: Commit**

```bash
git add src/Scrinia.Core/Search/WeightedFieldScorer.cs tests/Scrinia.Tests/WeightedFieldScorerBm25NormTests.cs
git commit -m "fix(search): max-normalize BM25 so a unique keyword-only match isn't dropped"
```

---

## Phase 4 — Verify-first Tier-3/4 (confirm intent, then fix or dismiss)

Each task below starts with a mandatory VERIFY step. If the finding proves intentional or a false positive, record it in Task 25's ledger and skip the fix.

### Task 17: `reconcile()` no-arg must not mutate disk during "scan and report"

**Files:** Modify `src/Scrinia.Mcp/MemoryTools.Bundling.cs:183-196,410` · Test `tests/Scrinia.Tests/ReconcileScanTests.cs` (create)

- [ ] **Step 1: VERIFY** — Read `MemoryTools.Bundling.cs:180-420`. Confirm the no-arg `reconcile()` → `ScanForConflicts` → `TryAutoResolveMetaJson` writes resolved JSON (line ~410) during a scan the docstring calls read-only. Decision gate: if the write only happens for an explicit `apply` argument, this is a false positive — record and stop.
- [ ] **Step 2 (if confirmed): Failing test** — Seed a `.scrinia/` with an auto-resolvable `.meta.json` conflict, call the scan path, assert the file bytes are unchanged and the report still lists the conflict.
- [ ] **Step 3: Fix** — Split scan from apply: `ScanForConflicts` becomes read-only (report the resolvable conflict without calling `TryAutoResolveMetaJson`); move the write behind the existing apply/reconcile-with-args path. Preserve the reported shape.
- [ ] **Step 4: Run** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter ReconcileScanTests` → PASS.
- [ ] **Step 5: Commit** — `git commit -m "fix(mcp): make no-arg reconcile() a read-only scan (no silent disk writes)"`

---

### Task 18: `excludeTopics` must filter before the limit when embeddings are active

**Files:** Modify `src/Scrinia.Mcp/MemoryTools.Core.cs:820-840` · Test `tests/Scrinia.Tests/SearchExcludeTopicsTests.cs` (create)

- [ ] **Step 1: VERIFY** — Read `MemoryTools.Core.cs:800-860`. Confirm the supplemental (embeddings) branch calls `SearchAll` WITHOUT `excludeTopics` and post-filters after the limit, while the non-supplemental branch passes `excludeTopics` into `SearchAll`. Decision gate: if both branches pass `excludeTopics`, false positive — record and stop.
- [ ] **Step 2 (if confirmed): Failing test** — With embeddings enabled and a corpus where the top-`limit` matches are in an excluded topic, assert the result count equals `limit` of non-excluded matches (not fewer).
- [ ] **Step 3: Fix** — Pass `excludeTopics` into the supplemental `SearchAll` overload (or apply the exclusion before the top-K cut) so excluded hits never consume result slots. Reuse the non-supplemental branch's approach.
- [ ] **Step 4: Run** — `--filter SearchExcludeTopicsTests` → PASS.
- [ ] **Step 5: Commit** — `git commit -m "fix(mcp): apply excludeTopics before the result limit in the embeddings search path"`

---

### Task 19: ChatProviderCache / StoreManager — no double-construct on concurrent first access

**Files:** Modify `src/Scrinia.Server/Chat/ChatProviderCache.cs:29-34`, `src/Scrinia.Server/Services/StoreManager.cs:~32` · Test `tests/Scrinia.Server.Tests/ProviderCacheSingleConstructionTests.cs` (create)

- [ ] **Step 1: VERIFY** — Read both `GetOrAdd` sites. Confirm the factory is expensive/side-effecting (creates `HttpClient` + registers a breaker; `StoreManager` opens a filesystem store). Concurrent first-access can run the factory more than once, discarding losers undisposed. (Confirmed for ChatProviderCache during review.)
- [ ] **Step 2: Failing/च characterization test** — Hammer `GetOrCreate("openai")` from N concurrent tasks with a factory that increments a static counter; assert exactly one construction. (Inject the factory or a test seam; if not injectable, cover StoreManager which is easier to instantiate.)
- [ ] **Step 3: Fix** — Wrap the value in `Lazy<T>`: store `ConcurrentDictionary<string, Lazy<IChatProvider?>>` and `GetOrAdd(name, n => new Lazy<...>(() => Factory.Create(...), LazyThreadSafetyMode.ExecutionAndPublication))`, returning `.Value`. Apply the same pattern to `StoreManager`. Keep `Dispose` iterating constructed values only (`.IsValueCreated`).
- [ ] **Step 4: Run** — `--filter ProviderCacheSingleConstructionTests` → PASS; `dotnet build src/Scrinia.Server` succeeds.
- [ ] **Step 5: Commit** — `git commit -m "fix(server): Lazy-cache chat providers and stores to guarantee single construction"`

---

### Task 20: VectorStore — atomic append framing (or fail-soft to last good)

**Files:** Modify `src/Scrinia.Core/Embeddings/VectorStore.cs` (append path + `LoadFromDisk` catch at :274) · Test `tests/Scrinia.Tests/VectorStoreTornAppendTests.cs` (create)

- [ ] **Step 1: VERIFY** — Read the append method (`AppendAddOpAsync`) and `ReadEntryOps`/`LoadFromDisk`. Confirm a truncated trailing record makes `ReadEntryOps` throw and the blanket `catch { return []; }` (line 274) drops ALL vectors for the scope. Decision gate: if appends are already length-prefixed/checksummed and the loader skips a bad tail, false positive.
- [ ] **Step 2: Failing test** — Write several records via the public upsert+persist path, then truncate the file by a few bytes, reload, and assert the intact records survive (not an empty list).
- [ ] **Step 3: Fix** — Make the loader tolerate a torn tail: catch the per-record read failure inside the record loop and return the records parsed so far (keeping the existing whole-file catch only for genuinely unreadable files). Minimal, behavior-preserving for the healthy case.
- [ ] **Step 4: Run** — `--filter VectorStoreTornAppendTests` → PASS.
- [ ] **Step 5: Commit** — `git commit -m "fix(embeddings): tolerate a torn trailing vector record instead of dropping the whole scope"`

---

### Task 21: VectorStore upsert dedup case-sensitivity matches load/remove

**Files:** Modify `src/Scrinia.Core/Embeddings/VectorStore.cs:109` · Test `tests/Scrinia.Tests/VectorStoreCaseTests.cs` (create)

- [ ] **Step 1: VERIFY** — Confirm `UpsertAsync` dedups with `v.Name == name` (ordinal) at line 109 while `ReadEntryOps` (:349) and `RemoveAsync` (:157) use `OrdinalIgnoreCase`. Decision gate: if names are guaranteed pre-normalized before upsert, false positive.
- [ ] **Step 2: Failing test** — Upsert `"Foo"` then `"foo"` (same chunkIndex) in ephemeral scope; assert only one entry remains (matching reload semantics).
- [ ] **Step 3: Fix** — Change line 109's comparison to case-insensitive: `RemoveAll(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase) && v.ChunkIndex == chunkIndex)`.
- [ ] **Step 4: Run** — `--filter VectorStoreCaseTests` → PASS.
- [ ] **Step 5: Commit** — `git commit -m "fix(embeddings): case-insensitive upsert dedup to match load/remove semantics"`

---

### Task 22: Web scope-filter tabs don't vanish after selecting a scope

**Files:** Modify `web/src/pages/MemoryBrowserPage.tsx:~72-90` · Test: Vitest only if harness exists; else manual note.

- [ ] **Step 1: VERIFY** — Read `MemoryBrowserPage.tsx`. Confirm `scopes` is derived from the currently-loaded (already-filtered) memories, and tabs render only when `scopes.length > 1`, so selecting a scope collapses the tab bar (including "All"). Decision gate: if scopes come from a separate unfiltered source, false positive.
- [ ] **Step 2: Fix** — Derive the scope list from an unfiltered source: either a dedicated scopes query, or keep a ref of all-seen scopes that isn't reset by the active filter. Always render the "All" tab. If a Vitest harness exists, add a test that selecting a scope keeps the tab bar mounted.
- [ ] **Step 3: Build** — `cd web && npm run build` → succeeds.
- [ ] **Step 4: Commit** — `git commit -m "fix(web): keep scope-filter tabs mounted after a scope is selected"`

---

### Task 23: Low-severity server/CLI batch (verify each, fix or dismiss)

Group of small items sharing one commit per fixed item. VERIFY each before changing.

**23a — Remote-mode `serve` should not wire a local embeddings pipeline.** `src/Scrinia/Commands/ScriniaCommands.Serve.cs:37-59`. VERIFY: remote mode sets `MemoryStoreContext.Current` to the HTTP store but still calls `LoadPluginsAsync()`, embedding remote memories into a local vector store + writing `.scrinia/` into cwd. FIX: skip `LoadPluginsAsync()` (and local event-sink wiring) when `--remote` is set; add a test if the serve setup is unit-reachable, else manual note. Commit: `fix(cli): skip local embeddings pipeline in remote serve mode`.

**23b — Rate-limit `/health/ready`.** `src/Scrinia.Server/Endpoints/HealthEndpoints.cs:17-18`. VERIFY: the readiness check opens SQLite + enumerates stores per call with no `RequireRateLimiting`. FIX: add `.RequireRateLimiting("api")` to the readiness/health group (keep `/health/live` cheap and unlimited). Commit: `fix(server): rate-limit /health/ready to prevent SQLite-open amplification`.

**23c — Stop logging memory names at Info.** `src/Scrinia.Server/Middleware/RequestTimingMiddleware.cs:12`. VERIFY: `Request.Path` (which embeds memory names) is logged at Information every request. Decision gate: if memory names are not considered sensitive in this product, DISMISS (record rationale). FIX (if kept): log method + route template or a redacted path at Info, full path at Debug. Commit: `fix(server): avoid logging memory names in request path at Info level`.

- [ ] **Step 1:** VERIFY 23a, 23b, 23c per above; record any dismissals in Task 25.
- [ ] **Step 2:** Apply confirmed fixes, one atomic commit each.
- [ ] **Step 3:** `dotnet build src/Scrinia.Server/Scrinia.Server.csproj -p:NuGetAudit=false` and `dotnet build src/Scrinia/Scrinia.csproj -p:NuGetAudit=false` → succeed.

---

### Task 24: `consolidate` re-embeds compacted entries; web token risk documented

**24a — `consolidate` leaves stale vectors.** `src/Scrinia/Commands/ScriniaCommands.Migration.cs:474-497`. VERIFY: `CompactEntryToSingleChunk` rewrites the `.nmp2` + upserts the sidecar but never notifies the embedding sink / re-embeds, so vectors keyed to the old chunk layout are orphaned. FIX: after compaction, invoke the same embedding event path used by store (or call `LoadPluginsAsync` + fire the store hook) so the merged chunk is re-embedded and stale chunk vectors are removed. If re-embedding at CLI time is out of scope, at minimum remove the orphaned vectors for that entry. Commit: `fix(cli): re-embed (or clear) vectors after consolidate compacts chunks`.

**24b — Web localStorage bearer token.** `web/src/api/client.ts:26`. VERIFY: token stored in `localStorage`; no active DOM-injection sink today (content renders as text). Decision: this is a latent risk, not an active bug. DISMISS with a documented rationale in Task 25 (and a follow-up note suggesting `sessionStorage` or a short-lived token if the threat model tightens). No code change unless the user asks.

- [ ] **Step 1:** VERIFY 24a; implement the confirmed fix with a test if the CLI consolidate path is unit-reachable (assert no orphaned vectors remain for the compacted entry), else manual note.
- [ ] **Step 2:** Record 24b dismissal rationale in Task 25.
- [ ] **Step 3:** Commit 24a.

---

## Phase 5 — Housekeeping + wrap-up

### Task 25: Findings ledger + `.gitignore` the model cache

**Files:**
- Modify: `.gitignore`
- Create: `docs/superpowers/plans/2026-07-17-remediation-ledger.md` (disposition of every finding)

- [ ] **Step 1: Ignore the downloaded model cache**

Append to `.gitignore`:

```gitignore
# Downloaded ML model caches used by retrieval benchmarks (large binaries, not source)
benchmarks/Scrinia.Retrieval.Benchmarks/tools/kompress/model-cache/
```

Verify it's now ignored:
Run: `git status --porcelain benchmarks/Scrinia.Retrieval.Benchmarks/tools/kompress/model-cache/ | head`
Expected: no output (untracked files now ignored).

- [ ] **Step 2: Write the findings ledger**

Create `docs/superpowers/plans/2026-07-17-remediation-ledger.md` with one row per finding: id, tier, disposition (`fixed` / `false-positive` / `dismissed-by-design` / `deferred`), the commit sha (for fixed) or a one-line rationale (for the rest). Fill it as tasks complete.

- [ ] **Step 3: Commit**

```bash
git add .gitignore docs/superpowers/plans/2026-07-17-remediation-ledger.md
git commit -m "chore: ignore benchmark model cache; add code-review remediation findings ledger"
```

---

### Task 26: Full build/test gate + open the PR

- [ ] **Step 1: Full suite**

Run: `dotnet test Scrinia.sln -p:NuGetAudit=false`
Expected: build green, all tests pass. If the AppHost audit still blocks restore, run each in-scope test project individually (`Scrinia.Tests`, `Scrinia.Server.Tests`, `Scrinia.Merge.Tests`, `Scrinia.Plugin.Embeddings.Tests`) and the web build (`cd web && npm run build`).

- [ ] **Step 2: Push + open PR**

```bash
git push -u origin fix/code-review-remediation
gh pr create --title "Fix code review findings (5 tiers)" --body "$(cat <<'EOF'
Remediates the full-codebase review findings, verify-first and test-driven.

## Summary
- Tier 1 (verified high): merge 3-way, zip-slip, VectorStore snapshot, MCP stdout logging, web login gate, embeddings init race.
- Tier 2: shared trailing-separator-safe path containment across 3 sandbox sites.
- Tier 3/4: transient-timeout classification, ProcessRunner cancellation, meta.json brace insertion, chat/embed cancellation handling, request-size ceiling, circuit-breaker half-open, BM25 max-norm, reconcile read-only, excludeTopics ordering, provider/store single-construction, torn-append tolerance, upsert case, web scope tabs, remote-serve, health rate-limit, path logging, consolidate re-embed.
- Tier 5: ignore benchmark model cache.

See docs/superpowers/plans/2026-07-17-remediation-ledger.md for per-finding disposition (fixed / false-positive / dismissed / deferred).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 3:** Paste the ledger summary of dismissed/deferred items into the PR description if not already included.

---

## Self-Review (author checklist — completed)

- **Spec coverage:** Every Tier 1–5 item in the spec maps to a task (Tier 1 → Tasks 1-6; Tier 2 → Tasks 7-8; Tier 3 confirmed → Tasks 9-16, 17-20, 22; Tier 4 → Tasks 15, 21, 23, 24; Tier 5 → Task 25). Build/verify constraints → Global Constraints + Task 26.
- **Placeholder scan:** Verify-first tasks (17-24) intentionally defer exact fix code to a confirmed VERIFY step per the spec's verify-first decision; each still names exact files, the failure to confirm, and the concrete fix mechanism. No "add error handling"-style vagueness.
- **Type consistency:** `PathContainment.IsWithin(baseDir, candidate)` (Task 7) is the exact name reused in Task 8; `TryMergeField`/`InsertConflictedFlag`/`TryEnterHalfOpen` are defined where first used.
