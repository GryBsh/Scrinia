# Workspace Root Resolution and Lazy Store Creation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop unrelated projects sharing one memory store by bounding workspace-root resolution at the nearest git repository, and stop reads from creating stores at all.

**Architecture:** Resolution moves to a new pure function in `Scrinia.Core` that walks up for `.git` (file or directory) and stops at the first repo root, never consulting whether a store exists. Store creation moves off the read path: `FileLock.AcquireShared` returns a no-op lock when there is nothing to guard, `FileMemoryStore`'s constructor stops touching disk, and all writes route through a single materialising wrapper.

**Tech Stack:** .NET 10, C# 13, xUnit 2.9.3. Projects: `Scrinia.Core` (store, locks), `Scrinia` (CLI, assembly name `scri`), `Scrinia.Tests`.

**Spec:** `docs/superpowers/specs/2026-08-03-workspace-root-resolution-design.md` (commit `1ccf263`)

## Global Constraints

- Target framework is .NET 10; C# file-scoped namespaces and collection expressions (`[]`) are the house style — match surrounding code.
- Conventional commits: `fix(scope):`, `feat(scope):`, `test(scope):`, `docs(scope):`, `refactor(scope):`.
- Prefix every shell command with `rtk` (token-optimising proxy), including inside `&&` chains.
- **No unfounded product attribution in comments.** Do not attribute file-access failures to Synology Drive, antivirus, or any named product. Guards are fine; blaming a product in a comment is not.
- **No meta commentary in comments.** No narration of the change ("now we…", "previously this…"), no `Mirrors X` / `See Y` cross-references added for their own sake, no editorialising. Comments explain the code as it stands.
- Run the full suite before the final commit of each task. Baseline is **1638 passed / 0 failed** on `main` at `1ccf263`.
- Build must be clean: `rtk dotnet build Scrinia.sln` → 0 errors.

## Deviation from the spec

The spec states `PersistMigratedEntry` "needs a small signature reconciliation" to fit the wrapper. On closer reading it does not: it takes `storeDir` as a parameter and the wrapper closes over it, so the body is unchanged. Task 4 converts it like the other four. No signature change.

---

### Task 1: `WorkspaceRoot.Resolve`

**Files:**
- Create: `src/Scrinia.Core/WorkspaceRoot.cs`
- Test: `tests/Scrinia.Tests/WorkspaceRootTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class WorkspaceRoot` with `public static string Resolve(string startDir)` and `public static string Resolve(string? explicitRoot, string startDir)`. Both return an absolute path. Never throws for a missing directory; never touches disk beyond existence probes.

The two-argument overload exists so the "explicit root wins" rule is a pure, testable decision rather than a branch buried in `WorkspaceSetup.Configure`, which mutates global static state and cannot be tested safely in parallel.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/WorkspaceRootTests.cs`:

```csharp
using Scrinia.Core;

namespace Scrinia.Tests;

public class WorkspaceRootTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceRootTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_wsroot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Mkdir(params string[] segments)
    {
        string path = Path.Combine([_tempDir, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RepoRoot_ResolvesToItself_NotAnAncestor()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(repo, WorkspaceRoot.Resolve(repo));
    }

    [Fact]
    public void Subdirectory_ResolvesToEnclosingRepoRoot()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        string sub = Mkdir("repo", "src", "deep");

        Assert.Equal(repo, WorkspaceRoot.Resolve(sub));
    }

    [Fact]
    public void GitAsFile_IsTreatedAsRepoRoot()
    {
        // Worktrees and submodules write .git as a FILE containing a gitdir: pointer.
        string worktree = Mkdir("worktree");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /somewhere/.git/worktrees/wt\n");
        string sub = Mkdir("worktree", "src");

        Assert.Equal(worktree, WorkspaceRoot.Resolve(sub));
    }

    [Fact]
    public void NoGitAnywhere_ResolvesToStartDir_WithoutWalking()
    {
        string plain = Mkdir("container", "plain");

        Assert.Equal(plain, WorkspaceRoot.Resolve(plain));
    }

    [Fact]
    public void NestedRepos_EachResolveToThemselves()
    {
        string outer = Mkdir("outer");
        Directory.CreateDirectory(Path.Combine(outer, ".git"));
        string inner = Mkdir("outer", "inner");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        Assert.Equal(outer, WorkspaceRoot.Resolve(outer));
        Assert.Equal(inner, WorkspaceRoot.Resolve(inner));
    }

    [Fact]
    public void AncestorStoreAboveRepoBoundary_IsNeverReached()
    {
        // A .scrinia far above must not influence resolution at all.
        Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia"));
        string repo = Mkdir("container", "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(repo, WorkspaceRoot.Resolve(repo));
    }

    [Fact]
    public void ExplicitRoot_BypassesResolutionEntirely()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        string elsewhere = Mkdir("elsewhere");

        // Given an explicit root, the .git above the start directory is irrelevant.
        Assert.Equal(elsewhere, WorkspaceRoot.Resolve(elsewhere, Path.Combine(repo, "src")));
    }

    [Fact]
    public void NullOrBlankExplicitRoot_FallsBackToResolution()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(repo, WorkspaceRoot.Resolve(null, repo));
        Assert.Equal(repo, WorkspaceRoot.Resolve("   ", repo));
    }

    [Fact]
    public void SiblingProjectsUnderSharedParentStore_ResolveToDifferentRoots()
    {
        // Regression for the reported bug: a .scrinia in a container directory
        // must not capture the projects beneath it.
        Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia"));
        string a = Mkdir("bludon");
        Directory.CreateDirectory(Path.Combine(a, ".git"));
        string b = Mkdir("wslc-docker-socket");
        Directory.CreateDirectory(Path.Combine(b, ".git"));

        string rootA = WorkspaceRoot.Resolve(a);
        string rootB = WorkspaceRoot.Resolve(b);

        Assert.Equal(a, rootA);
        Assert.Equal(b, rootB);
        Assert.NotEqual(rootA, rootB);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WorkspaceRootTests"`
Expected: FAIL to compile — `WorkspaceRoot` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Scrinia.Core/WorkspaceRoot.cs`:

```csharp
namespace Scrinia.Core;

/// <summary>
/// Resolves the workspace root for a working directory.
///
/// <para>The root is the nearest enclosing git repository, identified by a <c>.git</c> entry.
/// Resolution never consults whether a store exists, so a repository with no store cannot be
/// bound to an ancestor's, and the walk stops at the first repository boundary, so it cannot
/// leave the project.</para>
/// </summary>
public static class WorkspaceRoot
{
    /// <summary>
    /// Returns the workspace root for <paramref name="startDir"/>: the nearest ancestor
    /// containing a <c>.git</c> entry, examining <paramref name="startDir"/> itself first.
    /// When no repository encloses it, returns <paramref name="startDir"/> unchanged.
    /// </summary>
    public static string Resolve(string startDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDir);

        string full = Path.GetFullPath(startDir);
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            if (IsRepositoryRoot(dir.FullName))
                return dir.FullName;
        }
        return full;
    }

    /// <summary>
    /// Returns <paramref name="explicitRoot"/> when the caller supplied one, otherwise resolves
    /// from <paramref name="startDir"/>. An explicit root is taken verbatim and no walk occurs.
    /// </summary>
    public static string Resolve(string? explicitRoot, string startDir) =>
        !string.IsNullOrWhiteSpace(explicitRoot)
            ? Path.GetFullPath(explicitRoot)
            : Resolve(startDir);

    /// <summary>
    /// True when <paramref name="dir"/> holds a <c>.git</c> entry. Worktrees and submodules
    /// write <c>.git</c> as a file containing a <c>gitdir:</c> pointer rather than a directory,
    /// so both forms count.
    /// </summary>
    private static bool IsRepositoryRoot(string dir)
    {
        string git = Path.Combine(dir, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WorkspaceRootTests"`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Core/WorkspaceRoot.cs tests/Scrinia.Tests/WorkspaceRootTests.cs
rtk git commit -m "feat(core): add git-bounded workspace root resolution"
```

---

### Task 2: `WorkspaceRoot.FindOrphanStores`

**Files:**
- Modify: `src/Scrinia.Core/WorkspaceRoot.cs`
- Test: `tests/Scrinia.Tests/WorkspaceRootTests.cs`

**Interfaces:**
- Consumes: `WorkspaceRoot.Resolve` from Task 1.
- Produces: `public static IReadOnlyList<string> FindOrphanStores(string root, string startDir)` — absolute paths of `.scrinia` directories that resolution deliberately did not use. Never includes `<root>/.scrinia`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Scrinia.Tests/WorkspaceRootTests.cs`, inside the class:

```csharp
    [Fact]
    public void FindOrphanStores_ReportsAncestorStoreAboveRoot()
    {
        string orphan = Path.Combine(_tempDir, ".scrinia");
        Directory.CreateDirectory(orphan);
        string repo = Mkdir("container", "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var found = WorkspaceRoot.FindOrphanStores(repo, repo);

        Assert.Contains(orphan, found);
    }

    [Fact]
    public void FindOrphanStores_NeverReportsTheResolvedRootsOwnStore()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, ".scrinia"));

        var found = WorkspaceRoot.FindOrphanStores(repo, repo);

        Assert.DoesNotContain(Path.Combine(repo, ".scrinia"), found);
    }

    [Fact]
    public void FindOrphanStores_IsEmptyForACleanNewProject()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Empty(WorkspaceRoot.FindOrphanStores(repo, repo));
    }

    [Fact]
    public void FindOrphanStores_ReportsCwdStoreWhenCwdIsBelowRoot()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        string sub = Mkdir("repo", "src");
        string cwdStore = Path.Combine(sub, ".scrinia");
        Directory.CreateDirectory(cwdStore);

        var found = WorkspaceRoot.FindOrphanStores(repo, sub);

        Assert.Contains(cwdStore, found);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WorkspaceRootTests"`
Expected: FAIL to compile — `FindOrphanStores` does not exist.

- [ ] **Step 3: Write the implementation**

Add to `src/Scrinia.Core/WorkspaceRoot.cs`, inside the class:

```csharp
    /// <summary>
    /// Returns <c>.scrinia</c> directories that resolution passed over: every ancestor strictly
    /// above <paramref name="root"/>, plus <paramref name="startDir"/> itself when it differs
    /// from the root. The root's own store is excluded — it is the one being used.
    /// </summary>
    public static IReadOnlyList<string> FindOrphanStores(string root, string startDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(startDir);

        string fullRoot = Path.GetFullPath(root);
        string fullStart = Path.GetFullPath(startDir);
        var found = new List<string>();

        if (!string.Equals(fullStart, fullRoot, StringComparison.OrdinalIgnoreCase))
            AddIfStore(fullStart, found);

        var above = new DirectoryInfo(fullRoot).Parent;
        for (var dir = above; dir is not null; dir = dir.Parent)
            AddIfStore(dir.FullName, found);

        return found;
    }

    private static void AddIfStore(string dir, List<string> into)
    {
        string store = Path.Combine(dir, ".scrinia");
        if (Directory.Exists(store) && !into.Contains(store, StringComparer.OrdinalIgnoreCase))
            into.Add(store);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WorkspaceRootTests"`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Core/WorkspaceRoot.cs tests/Scrinia.Tests/WorkspaceRootTests.cs
rtk git commit -m "feat(core): detect stores that root resolution passed over"
```

---

### Task 3: `FileLock.AcquireShared` no-ops when there is nothing to guard

**Files:**
- Modify: `src/Scrinia.Core/FileLock.cs:34-35` (`AcquireShared`), `src/Scrinia.Core/FileLock.cs:22` (add a parameterless private constructor)
- Test: `tests/Scrinia.Tests/FileLockLazyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `FileLock.AcquireShared(string, TimeSpan?, ILogger?)` returns a disposable that created nothing when the lock's parent directory is absent. `AcquireExclusive` is unchanged and still creates the parent directory.

**Why exclusive is left alone:** `FileLock` is shared by `FileMemoryStore`, `ScriniaArtifactStore`, and `VectorStore`, and `AttributeMigration.cs:163-166` documents dependence on the directory-creating side effect of the write path. Exclusive means write intent, where creating is correct.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/FileLockLazyTests.cs`:

```csharp
using Scrinia.Core;

namespace Scrinia.Tests;

public class FileLockLazyTests : IDisposable
{
    private readonly string _tempDir;

    public FileLockLazyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_locklazy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AcquireShared_OnMissingDirectory_CreatesNothing()
    {
        string missingDir = Path.Combine(_tempDir, "absent");
        string lockPath = Path.Combine(missingDir, ".lock");

        using (var _ = FileLock.AcquireShared(lockPath)) { }

        Assert.False(Directory.Exists(missingDir));
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void AcquireShared_OnExistingDirectory_StillLocks()
    {
        string dir = Path.Combine(_tempDir, "present");
        Directory.CreateDirectory(dir);
        string lockPath = Path.Combine(dir, ".lock");

        using (var _ = FileLock.AcquireShared(lockPath))
        {
            Assert.True(File.Exists(lockPath));
        }
    }

    [Fact]
    public void AcquireExclusive_StillCreatesItsParentDirectory()
    {
        // The migration path relies on this; it must not regress.
        string missingDir = Path.Combine(_tempDir, "made-by-exclusive");
        string lockPath = Path.Combine(missingDir, ".lock");

        using (var _ = FileLock.AcquireExclusive(lockPath))
        {
            Assert.True(Directory.Exists(missingDir));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify one fails**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileLockLazyTests"`
Expected: `AcquireShared_OnMissingDirectory_CreatesNothing` FAILS (the directory and lock file are created). The other two PASS.

- [ ] **Step 3: Write the implementation**

In `src/Scrinia.Core/FileLock.cs`, add a parameterless constructor next to the existing one (line 22):

```csharp
    private FileLock(FileStream stream) => _stream = stream;

    /// <summary>A lock over nothing. Disposing it is a no-op.</summary>
    private FileLock() { }
```

Replace `AcquireShared` (lines 31-35) with:

```csharp
    /// <summary>
    /// Acquires a shared lock for read operations. Multiple shared locks can coexist.
    ///
    /// <para>When the lock's directory does not exist there is nothing to read and nothing to
    /// guard, so this returns a lock over nothing rather than creating the directory.</para>
    /// </summary>
    public static FileLock AcquireShared(string lockPath, TimeSpan? timeout = null, ILogger? logger = null)
    {
        string? dir = Path.GetDirectoryName(lockPath);
        if (dir is not null && !Directory.Exists(dir))
            return new FileLock();

        return Acquire(lockPath, FileAccess.Read, FileShare.Read, timeout ?? TimeSpan.FromSeconds(5), logger);
    }
```

`Dispose` already reads `Interlocked.Exchange(ref _stream, null)` followed by `stream?.Dispose()`, so a null stream needs no change.

- [ ] **Step 4: Run tests to verify they pass**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileLockLazyTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Run the full suite**

Run: `rtk dotnet test Scrinia.sln`
Expected: 1638 passed, 0 failed.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/FileLock.cs tests/Scrinia.Tests/FileLockLazyTests.cs
rtk git commit -m "fix(core): shared lock no longer materializes the directory it guards"
```

---

### Task 4: Route writes through a materialising wrapper

This is a behaviour-preserving refactor. The suite must stay green without new tests; the behaviour flip happens in Task 5.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` — add `EnsureMaterialized` and `WithExclusiveLock`; convert `PersistMigratedEntry` (894-911), `SaveIndex` (1018-1031), `Upsert` (1058-…), `Remove` (1104-…), `TryUpdateEntry` (1163-…)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `private void EnsureMaterialized(string scope)` and the pair `private T WithExclusiveLock<T>(string scope, Func<T> body)` / `private void WithExclusiveLock(string scope, Action body)`, used by Task 5.

- [ ] **Step 1: Add the wrapper and materialiser**

In `src/Scrinia.Core/FileMemoryStore.cs`, add immediately after `GetLockPath` (line 708-709):

```csharp
    /// <summary>
    /// Creates the workspace store and the scope's directory. Called only from write paths, so
    /// a workspace that is never written to leaves no directory behind.
    /// </summary>
    private void EnsureMaterialized(string scope)
    {
        EnsureGitIgnore(_workspaceRoot);
        Directory.CreateDirectory(GetStoreDirForScope(scope));
    }

    /// <summary>
    /// Runs <paramref name="body"/> under the scope's cross-process file lock and in-process
    /// write lock, materialising the store first. Every write goes through here so that
    /// "writes materialize, reads do not" holds in one place.
    /// </summary>
    private T WithExclusiveLock<T>(string scope, Func<T> body)
    {
        EnsureMaterialized(scope);
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
            return body();
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }

    private void WithExclusiveLock(string scope, Action body) =>
        WithExclusiveLock(scope, () => { body(); return true; });
```

- [ ] **Step 2: Convert `PersistMigratedEntry`**

Replace the body of `PersistMigratedEntry` (currently lines 894-911) with:

```csharp
    internal void PersistMigratedEntry(ArtifactEntry entry, string storeDir, string scope)
    {
        WithExclusiveLock(scope, () =>
        {
            Directory.CreateDirectory(storeDir);
            WriteSidecar(entry, storeDir);
            _indexCache.TryRemove(scope, out _); // next read rebuilds from the now-upgraded sidecars
            if (scope.StartsWith("local:", StringComparison.Ordinal))
                _cachedTopics = null;
        });
    }
```

The explicit `storeDir` parameter is closed over, so no signature change is needed. Leave the XML doc comment above it untouched.

- [ ] **Step 3: Convert `SaveIndex`**

Replace `SaveIndex` (currently lines 1018-1031) with:

```csharp
    public void SaveIndex(List<ArtifactEntry> entries, string scope = "local")
    {
        WithExclusiveLock(scope, () => SaveIndexUnsafe(entries, scope));
    }
```

- [ ] **Step 4: Convert `Upsert`, `Remove`, and `TryUpdateEntry`**

For each of the three, replace the preamble

```csharp
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
```

and its matching

```csharp
        }
        finally
        {
            lk.ExitWriteLock();
        }
```

with a `WithExclusiveLock` call wrapping the same body. `Upsert` returns nothing, so it uses the `Action` overload:

```csharp
    public void Upsert(ArtifactEntry entry, string scope = "local")
    {
        WithExclusiveLock(scope, () =>
        {
            // ...existing body, unchanged...
        });
    }
```

`Remove` and `TryUpdateEntry` return `bool`, so they use the generic overload and keep their `return` statements verbatim:

```csharp
    public bool Remove(string name, string scope = "local")
    {
        return WithExclusiveLock(scope, () =>
        {
            // ...existing body, unchanged, including its early `return false;` paths...
        });
    }
```

Do not alter any statement inside the bodies — including the existing `Directory.CreateDirectory(storeDir)` calls, which are idempotent and remain correct.

- [ ] **Step 5: Build and run the full suite**

Run: `rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln`
Expected: 0 errors; 1638 passed, 0 failed. No behaviour changed, so any failure here is a conversion mistake.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/FileMemoryStore.cs
rtk git commit -m "refactor(core): route store writes through one materializing lock wrapper"
```

---

### Task 5: Reads stop creating the store

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:198-205` (constructor), `src/Scrinia.Core/FileMemoryStore.cs:711-724` (`LoadIndex`)
- Test: `tests/Scrinia.Tests/LazyStoreCreationTests.cs`

**Interfaces:**
- Consumes: `EnsureMaterialized` / `WithExclusiveLock` from Task 4.
- Produces: constructing a `FileMemoryStore` performs no disk I/O; `LoadIndex` on an absent store returns `[]` without locking.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/LazyStoreCreationTests.cs`:

```csharp
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public class LazyStoreCreationTests : IDisposable
{
    private readonly string _tempDir;

    public LazyStoreCreationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_lazy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string ScriniaDir => Path.Combine(_tempDir, ".scrinia");

    [Fact]
    public void Constructing_TheStore_TouchesNoDisk()
    {
        _ = new FileMemoryStore(_tempDir);

        Assert.False(Directory.Exists(ScriniaDir));
    }

    [Fact]
    public void LoadIndex_OnAbsentStore_ReturnsEmptyAndCreatesNothing()
    {
        var store = new FileMemoryStore(_tempDir);

        var entries = store.LoadIndex();

        Assert.Empty(entries);
        Assert.False(Directory.Exists(ScriniaDir));
    }

    [Fact]
    public void LoadIndex_OnAbsentTopicScope_ReturnsEmptyAndCreatesNothing()
    {
        var store = new FileMemoryStore(_tempDir);

        var entries = store.LoadIndex("local:findings");

        Assert.Empty(entries);
        Assert.False(Directory.Exists(ScriniaDir));
    }

    [Fact]
    public void FirstWrite_MaterializesTheStoreAndTheGitIgnore()
    {
        var store = new FileMemoryStore(_tempDir);

        store.Upsert(new ArtifactEntry { Name = "first", Uri = "file://first" });

        Assert.True(Directory.Exists(ScriniaDir));
        Assert.True(File.Exists(Path.Combine(ScriniaDir, ".gitignore")));
        Assert.True(Directory.Exists(Path.Combine(ScriniaDir, "store")));
    }

    [Fact]
    public void WrittenEntry_IsReadBack()
    {
        var store = new FileMemoryStore(_tempDir);
        store.Upsert(new ArtifactEntry { Name = "first", Uri = "file://first" });

        var entries = store.LoadIndex();

        Assert.Contains(entries, e => e.Name == "first");
    }
}
```

If `ArtifactEntry`'s required members differ from `Name`/`Uri`, adjust the two construction sites to satisfy the compiler — check `src/Scrinia.Core/Models/ArtifactEntry.cs` and mirror how `tests/Scrinia.Tests/VectorStoreScopeIsolationTests.cs` builds one.

- [ ] **Step 2: Run tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~LazyStoreCreationTests"`
Expected: `Constructing_TheStore_TouchesNoDisk` and both `LoadIndex_*` tests FAIL — the constructor creates `.scrinia`.

- [ ] **Step 3: Take `EnsureGitIgnore` out of the constructor**

In `src/Scrinia.Core/FileMemoryStore.cs`, change the constructor (lines 198-205) to:

```csharp
    public FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        RankerOptions = rankerOptions ?? RankerOptions.Default;
    }
```

Leave `EnsureGitIgnore` itself in place — `EnsureMaterialized` calls it.

- [ ] **Step 4: Make `LoadIndex` return before locking**

Replace `LoadIndex` (lines 711-724) with:

```csharp
    public List<ArtifactEntry> LoadIndex(string scope = "local")
    {
        if (!Directory.Exists(GetStoreDirForScope(scope)))
            return [];

        using var fileLock = FileLock.AcquireShared(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterReadLock();
        try
        {
            return LoadIndexUnsafe(scope);
        }
        finally
        {
            lk.ExitReadLock();
        }
    }
```

- [ ] **Step 5: Run the new tests**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~LazyStoreCreationTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Run the full suite**

Run: `rtk dotnet test Scrinia.sln`
Expected: 1638 passed plus the new tests, 0 failed.

Some existing tests may have relied on the constructor creating `.scrinia`. If any fail, fix the **test** by writing an entry first or creating the directory explicitly — do not restore eager creation. Report any test you change and why.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/LazyStoreCreationTests.cs
rtk git commit -m "fix(core): stop creating the store on construction and on reads"
```

---

### Task 6: Correct the `.gitignore` template

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:215-231` (the `EnsureGitIgnore` template literal)
- Test: `tests/Scrinia.Tests/LazyStoreCreationTests.cs`

**Interfaces:**
- Consumes: `EnsureMaterialized` from Task 4, first-write behaviour from Task 5.
- Produces: newly created stores ignore `*.lock` suffixed files and `embeddings/`.

`**/.lock` matches only files named exactly `.lock`. The vector store writes `<name>.bin.lock`, which was never covered — `vectors.bin.lock` ended up tracked in git as a result. This repo's checked-in copy was corrected in `f301f9f`; the template that generates every new store still carries the bug.

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Tests/LazyStoreCreationTests.cs`, inside the class:

```csharp
    [Fact]
    public void GeneratedGitIgnore_CoversSuffixedLocksAndVectorIndexes()
    {
        var store = new FileMemoryStore(_tempDir);
        store.Upsert(new ArtifactEntry { Name = "first", Uri = "file://first" });

        string[] lines = File.ReadAllLines(Path.Combine(ScriniaDir, ".gitignore"));

        Assert.Contains("**/*.lock", lines);
        Assert.Contains("embeddings/", lines);
        Assert.DoesNotContain("**/.lock", lines);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~GeneratedGitIgnore_CoversSuffixedLocksAndVectorIndexes"`
Expected: FAIL — the file contains `**/.lock` and no `embeddings/`.

- [ ] **Step 3: Fix the template**

In `EnsureGitIgnore`, change the literal so it reads:

```csharp
        AtomicWriteAllText(gitIgnorePath, """
            # Lock files (runtime artifacts from cross-process file locking)
            **/*.lock

            # Export bundles (generated, can be re-exported)
            exports/

            # Archived versions (auto-pruned, not source-controlled)
            **/versions/

            # Temporary files from interrupted writes
            **/*.tmp

            # Vector indexes (derived from topic content; rebuild with `scri reindex`)
            embeddings/

            # Consolidation bookkeeping (rebuilt from sidecar state on next run)
            .last-consolidation
            .consolidate-progress.json
            """.Replace("            ", ""));
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~LazyStoreCreationTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/LazyStoreCreationTests.cs
rtk git commit -m "fix(core): generated gitignore covers suffixed locks and vector indexes"
```

---

### Task 7: Wire resolution into `WorkspaceSetup.Configure`

**Files:**
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:16-39` (`Configure`), delete `FindWorkspaceRoot` at `src/Scrinia/Commands/WorkspaceSetup.cs:494-504`

**Interfaces:**
- Consumes: `WorkspaceRoot.Resolve(string?, string)` from Task 1.
- Produces: `WorkspaceSetup.Configure(string?)` resolves via `WorkspaceRoot`. All 22 call sites are unchanged.

No new tests: the resolution logic is covered by Task 1, and this task only redirects `Configure` to it. The behaviour is exercised end-to-end by the manual verification at the end of the plan.

- [ ] **Step 1: Replace the resolution block**

In `src/Scrinia/Commands/WorkspaceSetup.cs`, replace `Configure`'s root selection (lines 16-31) with:

```csharp
    internal static void Configure(string? workspaceRoot)
    {
        string root = WorkspaceRoot.Resolve(workspaceRoot, Directory.GetCurrentDirectory());

        ScriniaArtifactStore.Configure(root);
```

The explicit-root branch lives in `WorkspaceRoot.Resolve`, where Task 1 tested it.

Leave the rest of the method — the `RankerOptions.FromConfig` call and the `MemoryStoreContext.Current` assignment — untouched.

- [ ] **Step 2: Delete the old walk**

Delete `FindWorkspaceRoot` entirely (lines 494-504). Add `using Scrinia.Core;` if the file does not already have it.

- [ ] **Step 3: Build**

Run: `rtk dotnet build Scrinia.sln`
Expected: 0 errors. A "not used" warning for a leftover using is a signal you removed something you shouldn't have.

- [ ] **Step 4: Run the full suite**

Run: `rtk dotnet test Scrinia.sln`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia/Commands/WorkspaceSetup.cs
rtk git commit -m "fix(cli): bound workspace resolution at the nearest git repository"
```

---

### Task 8: Warn about stores that resolution passed over

**Files:**
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs` (add `WarnOnOrphanStores`)
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs` — call it from `Store` (338), `Forget` (441), `Append` (473), `Compact` (516), `Link` (538)
- Modify: `src/Scrinia/Commands/ScriniaCommands.Setup.cs:41`, `src/Scrinia/Commands/ScriniaCommands.Serve.cs:67`
- Test: `tests/Scrinia.Tests/WorkspaceRootTests.cs`

**Interfaces:**
- Consumes: `WorkspaceRoot.FindOrphanStores` from Task 2.
- Produces: `internal static void WarnOnOrphanStores()` writing to stderr. Also `internal static string FormatOrphanWarning(string orphan, string root, IReadOnlyList<string> topics)` so the message is testable without capturing console output.

**Do not call it from** `List` (33), `Search` (222), `Show` (400), `Facts`, `Hint`, or `bundle export`. The hint hook fires on every prompt; adding an upward scan there reintroduces per-prompt directory probes.

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Tests/WorkspaceRootTests.cs`, inside the class:

```csharp
    [Fact]
    public void OrphanWarning_NamesBothStoresAndAConcreteMigrationCommand()
    {
        string text = Scrinia.Commands.WorkspaceSetup.FormatOrphanWarning(
            orphan: @"C:\.scrinia",
            root: @"C:\repos\proj",
            topics: ["agent", "patterns"]);

        Assert.Contains(@"C:\.scrinia", text);
        Assert.Contains(@"C:\repos\proj", text);
        Assert.Contains("scri bundle export", text);
        Assert.Contains("scri bundle import", text);
        Assert.Contains("agent,patterns", text);
    }
```

`WorkspaceSetup` is `internal`, but `src/Scrinia/Scrinia.csproj:30` already declares
`<InternalsVisibleTo Include="Scrinia.Tests" />`, so no project change is needed.

- [ ] **Step 2: Run the test to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~OrphanWarning_NamesBothStores"`
Expected: FAIL to compile — `FormatOrphanWarning` does not exist.

- [ ] **Step 3: Implement the formatter and the warning**

Add to `src/Scrinia/Commands/WorkspaceSetup.cs`:

```csharp
    /// <summary>
    /// Builds the message shown when a store exists somewhere resolution deliberately did not
    /// look. Separated from the write so the wording is testable without capturing the console.
    /// </summary>
    internal static string FormatOrphanWarning(string orphan, string root, IReadOnlyList<string> topics)
    {
        string topicList = topics.Count > 0 ? string.Join(",", topics) : "<topics>";
        return $"""
            scrinia: ignoring store at {orphan}
                     using {root}

            to migrate its memories:
              scri bundle export --workspace-root "{Path.GetDirectoryName(orphan)}" --topics {topicList}
              scri bundle import --workspace-root "{root}" --path <file>
            """;
    }

    /// <summary>
    /// Reports stores that root resolution passed over. Called only from commands that already
    /// do real work — never from the per-prompt hint path.
    /// </summary>
    internal static void WarnOnOrphanStores()
    {
        string root = ScriniaArtifactStore.WorkspaceRootPath;
        string cwd = Directory.GetCurrentDirectory();
        foreach (string orphan in WorkspaceRoot.FindOrphanStores(root, cwd))
            Console.Error.WriteLine(FormatOrphanWarning(orphan, root, ListTopics(orphan)));
    }

    /// <summary>Topic directory names inside an orphaned store, for the migration command.</summary>
    private static IReadOnlyList<string> ListTopics(string storeDir)
    {
        string topicsDir = Path.Combine(storeDir, "topics");
        if (!Directory.Exists(topicsDir)) return [];
        return [.. Directory.EnumerateDirectories(topicsDir).Select(Path.GetFileName).OfType<string>().Order()];
    }
```

- [ ] **Step 4: Call it from the write commands**

Add `WorkspaceSetup.WarnOnOrphanStores();` on the line immediately after the existing `WorkspaceSetup.Configure(workspaceRoot);` in each of: `ScriniaCommands.Memory.cs` `Store`, `Forget`, `Append`, `Compact`, `Link`; `ScriniaCommands.Setup.cs:41`; `ScriniaCommands.Serve.cs:67`.

Do not add it to `List`, `Search`, or `Show` in the same file.

- [ ] **Step 5: Run the tests**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WorkspaceRootTests"`
Expected: PASS, 14 tests.

- [ ] **Step 6: Verify the hot path is untouched**

Run: `rtk proxy grep -n "WarnOnOrphanStores" src/Scrinia/Commands/ScriniaCommands.HintCommand.cs`
Expected: no matches.

- [ ] **Step 7: Run the full suite and commit**

```bash
rtk dotnet test Scrinia.sln
rtk git add src/Scrinia/Commands/WorkspaceSetup.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia/Commands/ScriniaCommands.Setup.cs src/Scrinia/Commands/ScriniaCommands.Serve.cs src/Scrinia/Scrinia.csproj
rtk git commit -m "feat(cli): warn when a store outside the resolved root is being ignored"
```

---

### Task 9: Stamp `--workspace-root` into project-scope hooks

**Files:**
- Modify: `src/Scrinia/Commands/Hooks/AgentHookSetup.cs:142-158` (the spec-stamping loop), add `StampWorkspaceRoot` next to `StampClient` (line 37)
- Test: `tests/Scrinia.Tests/AgentHookSetupTests.cs` (create if absent)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `internal static HookSpec StampWorkspaceRoot(HookSpec spec, string workspaceRoot)`.

**Critical:** stamp **only** when `scope == HookScope.Project`. `HookScope` is `{ User, Project }` (`IAgentHookInstaller.cs:21`); a user-scope hook is global across every project, so pinning it to one root would recreate the original bug in a new form — every project would then read one store by configuration rather than by accident.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/AgentHookSetupTests.cs`:

```csharp
using Scrinia.Commands.Hooks;

namespace Scrinia.Tests;

public class AgentHookSetupTests
{
    [Fact]
    public void StampWorkspaceRoot_AppendsQuotedRoot()
    {
        var spec = new HookSpec("UserPromptSubmit", "scri hint");

        var stamped = AgentHookSetup.StampWorkspaceRoot(spec, @"C:/repos/proj");

        Assert.Equal(@"scri hint --workspace-root ""C:/repos/proj""", stamped.Command);
        Assert.Equal("UserPromptSubmit", stamped.EventName);
    }

    [Fact]
    public void StampWorkspaceRoot_HandlesRootsContainingSpaces()
    {
        var spec = new HookSpec("SessionStart", "scri restore --hook");

        var stamped = AgentHookSetup.StampWorkspaceRoot(spec, @"C:/Program Files/proj");

        Assert.Contains(@"--workspace-root ""C:/Program Files/proj""", stamped.Command);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AgentHookSetupTests"`
Expected: FAIL to compile — `StampWorkspaceRoot` does not exist.

- [ ] **Step 3: Implement the stamp**

In `src/Scrinia/Commands/Hooks/AgentHookSetup.cs`, add after `StampClient` (line 38):

```csharp
    /// <summary>
    /// Pins a hook command to a workspace root so the hook does not depend on the directory the
    /// agent CLI happens to launch from. Project scope only — a user-scope hook applies to every
    /// project, and a fixed root there would point all of them at one store.
    /// </summary>
    internal static HookSpec StampWorkspaceRoot(HookSpec spec, string workspaceRoot) =>
        spec with { Command = $"{spec.Command} --workspace-root \"{workspaceRoot}\"" };
```

- [ ] **Step 4: Apply it in the install loop**

In `InstallAsync`, change the supported-spec branch (line 150) from

```csharp
                    supportedSpecs.Add(StampClient(spec, installer.ClientId));
```

to

```csharp
                    var stamped = StampClient(spec, installer.ClientId);
                    if (scope == HookScope.Project && !string.IsNullOrWhiteSpace(workspaceRoot))
                        stamped = StampWorkspaceRoot(stamped, workspaceRoot!);
                    supportedSpecs.Add(stamped);
```

- [ ] **Step 5: Run tests and the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AgentHookSetupTests"`
Expected: PASS, 2 tests.

Run: `rtk dotnet test Scrinia.sln`
Expected: all green. Existing hook-installer tests assert exact command strings; if any fail, update the expected string to include the stamped root **only** for project-scope cases, and report which you changed.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia/Commands/Hooks/AgentHookSetup.cs tests/Scrinia.Tests/AgentHookSetupTests.cs
rtk git commit -m "fix(hooks): pin project-scope hooks to their workspace root"
```

---

### Task 10: Document the MCP server entry

**Files:**
- Modify: `docs/getting-started.md`, `docs/troubleshooting.md`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: the behaviour from Tasks 7-9.
- Produces: no code.

Scrinia writes no MCP server configuration, so `scri serve`'s working directory is the client's choice. The docs are the only place this can be stated.

- [ ] **Step 1: Find the MCP registration instructions**

Run: `rtk proxy grep -rn "scri serve" docs/ README.md`

- [ ] **Step 2: Update each registration example**

For every example that registers the MCP server, add `--workspace-root` with the project path, plus one sentence: the server resolves its workspace from the nearest enclosing git repository, and passing the root explicitly makes it independent of the directory the client launches from.

- [ ] **Step 3: Add a troubleshooting entry**

In `docs/troubleshooting.md`, add a section titled "Memories from another project appear, or my memories seem missing" covering: the workspace root is the nearest enclosing git repository; a project outside any repository uses its own directory; `scri` prints a warning naming any store it is ignoring; and `bundle export`/`bundle import` move memories between roots.

- [ ] **Step 4: Add the changelog entry**

Under the unreleased heading in `CHANGELOG.md`:

```markdown
### Fixed

- Workspace root resolution is now bounded at the nearest enclosing git repository. Projects
  without their own store no longer bind to a store in a distant ancestor directory, which
  caused unrelated projects to share one memory pool.
- The store is no longer created while reading. Constructing the store and running `hint`,
  `recall`, or `search` against a workspace with no store leave no directory behind.
- Generated `.scrinia/.gitignore` now covers suffixed lock files (`*.lock`) and vector indexes.
```

- [ ] **Step 5: Commit**

```bash
rtk git add docs/ CHANGELOG.md
rtk git commit -m "docs: workspace root resolution and explicit MCP workspace root"
```

---

## Manual verification

After Task 10, confirm the reported bug is fixed against real directories rather than temp fixtures:

- [ ] `cd C:/projects/bludon && rtk proxy scri hint --plain --json` — the resolved root must be `C:/projects/bludon`, and no hints from other projects appear.
- [ ] Confirm `C:\.scrinia` is unchanged and was not deleted: `rtk proxy ls C:/.scrinia`.
- [ ] In a scratch directory with no `.git`, run a read command and confirm no `.scrinia` is created.
- [ ] In a git worktree, confirm resolution lands on the worktree directory, not the main checkout.
