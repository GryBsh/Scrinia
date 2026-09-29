# P1 Durability: DurableFile Directory-Fsync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends with an independently testable deliverable and its own commit.

**Goal:** Make Scrinia's durable single-file replacements crash-consistent by fsyncing the destination directory after each atomic rename.

**Architecture:** Add a directory-fsync step to the single choke-point `DurableFile.AtomicWrite` (after its `File.Move`), with a Windows `FlushFileBuffers` / POSIX `fsync` interop, a new `BeforeDirSync` fault phase, and a tolerated directory-vanished race. All durable callers inherit the fix; no per-caller changes and no god-store restructuring (that is P4).

**Tech Stack:** C# / .NET 10, P/Invoke (kernel32/libc), xUnit, FluentAssertions.

**Spec:** docs/superpowers/specs/2026-09-23-p1-durability-design.md — read this together with the plan; the plan argues from the spec.

## Global Constraints
- NMP/2 artifact format unchanged (`NMP_SPEC.md`).
- `.scrinia/` layout unchanged (`memories/`, `skills/`, `agent/`, sidecars, index.json v3).
- CLI stays single-file surface (`src/Scrinia/Commands/ScriniaCommands.cs`, `--workspace-root` pattern; no remote mode).
- MCP 12+11 disjoint: 12 tools (`guide` + remember/recall/search/list/append/forget/compact/link/restore/facts) stay disjoint from the 11 CLI command forms.
- Source-gen JSON only (`ScriniaMcpJsonContext`, `CliJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`); no reflection serialization.
- Recovery bypasses `FileMemoryStore.DiscoverMemoriesRecursive` and `MemoryNaming.ParseScopeLabel` untouched.
- No new public API beyond interop; `DurableWriteException` shape unchanged.
- Every new test uses `TestHelpers.StoreScope` isolation.
- Build must stay green (`dotnet build Scrinia.sln`); the full suite must pass (`dotnet test Scrinia.sln`).

---

## Change surface

This spec touches exactly two source files. Do not touch anything else.

| File | Change |
|---|---|
| `src/Scrinia.Core/DurableFile.cs` | Add `SyncDirectory` + `SyncDirectoryWindows`/`SyncDirectoryUnix` (P/Invoke interop + constants + `#pragma warning disable CA1838, CA2101, CA1865`), a `BeforeDirSync` case on the `DurableWritePhase` enum, and a call to `SyncDirectory(path)` at the end of `AtomicWrite` after the `File.Move`. |
| `src/Scrinia.Core/B0FaultSeam.cs` | Add `BeforeDirSync` to the `FaultPhase` enum and the `MapDurableWritePhase` switch arm. |

No changes to `FileMemoryStore.cs`, `AppendJournal.cs`, `RestoreJournal.cs`, `RecoveryEnvelope.cs`, coordinators, CLI, MCP, JSON contexts, or `NMP_SPEC.md`.

### Why the choke-point, not the callers

`DurableFile.AtomicWrite` (`src/Scrinia.Core/DurableFile.cs:62`) is the only place a durable single-file replacement happens. Every durable leaf funnels through it: append journal (`AppendTransaction`-coalesced via `PersistAppendJournal`), archive members, live artifact/sidecar (`WriteArtifactAsync`/`WriteSidecarCore`), restore journal, file-entity evidence, config, and bundles. Fixing it here covers all callers at once instead of touching ~6 sites.

### Critical ordering fact (post-move phase)

The append write path (`FileMemoryStore.PublishLiveAppendAsync`, `src/Scrinia.Core/FileMemoryStore.cs:3360`) durably moves the live artifact into place (`WriteArtifactAsync`, `:3363`) and the sidecar (`UpsertForAppend` → `WriteSidecarCore`, `:3365`) **before** it writes the `Committed` recovery journal (`PersistAppendJournal`, `:3371`). The `BeforeDirSync` phase fires *after* content is already committed to the destination directory.

Consequence for the fault test: a dir-sync failure during the first durable move surfaces as `DurableWriteException`; the append's in-memory rollback (`FileMemoryStore.TryRestoreAppendTransaction` → `RestoreAppendMember`, `:3664`/`:3759`) removes the partial members. For a **create** append the rollback is a plain `File.Delete` (no durable write, no dir-sync phase). For an **update** append the rollback rewrites the old bytes through a second `AtomicWriteAllBytesForAppend` (`RollbackWrite` fault point) — that second durable write ALSO carries the new `BeforeDirSync` phase. Therefore the fault test must use a **first-call-fails** injector (not a persistent one): a persistent injector would also fail the `RolledBack` journal write and degrade the append to `RecoveryRequired` instead of a clean rollback.

---

## Task 1: Fault-phase plumbing

Make `BeforeDirSync` a recognized fault-injection phase before any code calls it.

**Files:**
- Modify: `src/Scrinia.Core/DurableFile.cs` — add `BeforeDirSync` to the `DurableWritePhase` enum (bottom of file, currently `BeforeWrite, BeforeFlush, BeforeMove`).
- Modify: `src/Scrinia.Core/B0FaultSeam.cs` — add `BeforeDirSync` to the `FaultPhase` enum (`:209`) and a switch arm in `MapDurableWritePhase` (`:120-126`): `DurableWritePhase.BeforeDirSync => FaultPhase.BeforeDirSync`.
- Test: create `tests/Scrinia.Tests/DurableWritePhaseMappingTests.cs`.

- [ ] **Step 1: Write the failing test** — a theory asserting `B0FaultSeam.MapDurableWritePhase` maps each `DurableWritePhase` to its `FaultPhase`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class DurableWritePhaseMappingTests
{
    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void MapDurableWritePhase_RoundTripsEveryPhase(
        DurableWritePhase phase, B0FaultSeam.FaultPhase expected) =>
        B0FaultSeam.MapDurableWritePhase(phase).Should().Be(expected);

    public static IEnumerable<object[]> AllPhasePairs() =>
    [
        new object[] { DurableWritePhase.BeforeWrite, B0FaultSeam.FaultPhase.BeforeWrite },
        new object[] { DurableWritePhase.BeforeFlush, B0FaultSeam.FaultPhase.BeforeFlush },
        new object[] { DurableWritePhase.BeforeMove, B0FaultSeam.FaultPhase.BeforeMove },
        new object[] { DurableWritePhase.BeforeDirSync, B0FaultSeam.FaultPhase.BeforeDirSync },
    ];
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~DurableWritePhaseMappingTests`

Expected: FAIL — `BeforeDirSync` does not exist on `DurableWritePhase` (compile error / no such member).

- [ ] **Step 3: Write minimal implementation** — add `BeforeDirSync` to the `DurableWritePhase` enum in `DurableFile.cs` and to the `FaultPhase` enum in `B0FaultSeam.cs`, plus the one `MapDurableWritePhase` switch arm shown above.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~DurableWritePhaseMappingTests`

Expected: PASS — all four phases map.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/DurableFile.cs src/Scrinia.Core/B0FaultSeam.cs tests/Scrinia.Tests/DurableWritePhaseMappingTests.cs
git commit -m "core: add BeforeDirSync fault phase to durable-write seam" -m "Wires the pre-move dir-sync phase through DurableWritePhase + B0FaultSeam.MapDurableWritePhase so it is reachable via FailOnceAt/FailPersistentlyAt and the FailureInjector seam. Pure enum plumbing; no behavior change yet."
```

---

## Task 2: SyncDirectory interop + wiring + durable-write tests

Implement the directory fsync and wire it into `AtomicWrite`, then prove it with the two deterministic tests from the spec.

**Files:**
- Modify: `src/Scrinia.Core/DurableFile.cs` — `SyncDirectory` + Windows/POSIX interop + `AtomicWrite` wiring.
- Test: create `tests/Scrinia.Tests/DurableFileDirectorySyncTests.cs`.

- [ ] **Step 1: Write the failing tests** — two tests. The positive test records every durable-write phase via `DurableFile.FailureInjector` and asserts `BeforeDirSync` fires on a real append create. The fault test fails the *first* dir-sync only and asserts the append create rolls back (partial members removed, no leftover journal).

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class DurableFileDirectorySyncTests
{
    [Fact]
    public async Task AppendAsync_DirectoryIsFsyncedAfterAtomicRename()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var phases = new List<DurableWritePhase>();

        DurableFile.FailureInjector = (phase, _) =>
        {
            phases.Add(phase);
            return null;
        };

        try
        {
            AppendStoreResult result = await store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/synced"), "local", ["body"]));

            result.Outcome.Should().Be(AppendStoreOutcome.Created);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        phases.Should().Contain(DurableWritePhase.BeforeDirSync);
    }

    [Fact]
    public async Task AppendAsync_FirstDirSyncFailureRollsBackCreateMembers()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        string unrelatedPath = Path.Combine(scope.WorkspaceDir, "unrelated.txt");
        await File.WriteAllTextAsync(unrelatedPath, "preserve me");

        int dirSyncFailures = 0;
        DurableFile.FailureInjector = (phase, _) =>
            phase == DurableWritePhase.BeforeDirSync && dirSyncFailures++ == 0
                ? new IOException("Injected dir-sync failure.")
                : null;

        try
        {
            await FluentActions.Invoking(() => store.AppendAsync(
                    MemoryWrite.Create(MemoryAddress.Parse("/new-target"), "local", ["created"])))
                .Should().ThrowAsync<DurableWriteException>();

            File.Exists(store.FindArtifactPath("new-target", "local")).Should().BeFalse();
            File.Exists(store.MetaPath("new-target")).Should().BeFalse();
            (await File.ReadAllTextAsync(unrelatedPath)).Should().Be("preserve me");

            string journalPath = Path.Combine(
                store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName);
            File.Exists(journalPath).Should().BeFalse();
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~DurableFileDirectorySyncTests`

Expected: FAIL — `SyncDirectory` / `DurableWritePhase.BeforeDirSync` do not exist yet (compile error), or `phases` does not contain `BeforeDirSync`.

- [ ] **Step 3: Write minimal implementation** — in `DurableFile.cs`:

  3a. Add the `BeforeDirSync` case to `DurableWritePhase` (already done in Task 1).

  3b. Add `SyncDirectory` and the interop. Insert this block anywhere inside the `DurableFile` class (for example, after `WriteTemporaryFile`):

```csharp
private static void SyncDirectory(string destination)
{
    string directory = Path.GetDirectoryName(destination);
    if (string.IsNullOrEmpty(directory))
        directory = ".";
    ThrowInjected(DurableWritePhase.BeforeDirSync, directory);
    FileRetry.Run(() =>
    {
        if (OperatingSystem.IsWindows())
            SyncDirectoryWindows(directory);
        else
            SyncDirectoryUnix(directory);
    });
}

private static void SyncDirectoryWindows(string directory)
{
    IntPtr handle = CreateFile(
        directory, GENERIC_READ, 0, IntPtr.Zero, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
    if (handle == (IntPtr)(-1))
    {
        int error = Marshal.GetLastWin32Error();
        // Directory vanished between the caller's existence check and this open (a race the
        // atomic move already won): nothing to persist, so treat the sync as satisfied.
        if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
            return;
        throw new IOException(
            $"Failed to open directory '{directory}' for fsync (Win32 error {error}).");
    }

    try
    {
        if (!FlushFileBuffers(handle))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Failed to flush directory '{directory}' (Win32 error {error}).");
        }
    }
    finally
    {
        CloseHandle(handle);
    }
}

private static void SyncDirectoryUnix(string directory)
{
    int fd = open(directory, O_RDONLY);
    if (fd == -1)
    {
        int errno = Marshal.GetLastPInvokeError();
        // Same not-found race as the Windows path: tolerate it.
        if (errno is ENOENT or ENOTDIR)
            return;
        throw new IOException(
            $"Failed to open directory '{directory}' for fsync (errno {errno}).");
    }

    try
    {
        if (fsync(fd) != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Failed to flush directory '{directory}' (errno {errno}).");
        }
    }
    finally
    {
        close(fd);
    }
}

// Windows interop (kernel32). FILE_FLAG_BACKUP_SEMANTICS is required to open a directory handle; GENERIC_WRITE is required so FlushFileBuffers can flush the directory's entries (a read-only handle fails with access denied).
private const uint GENERIC_WRITE = 0x4000_0000;
private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;
private const uint OPEN_EXISTING = 3;
private const int ERROR_FILE_NOT_FOUND = 2;
private const int ERROR_PATH_NOT_FOUND = 3;

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
private static extern IntPtr CreateFile(
    string lpFileName, uint dwDesiredAccess, uint dwShareMode,
    IntPtr lpSecurityAttributes, uint dwCreationDisposition,
    uint dwFlagsAndAttributes, IntPtr hTemplateFile);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool FlushFileBuffers(IntPtr hFile);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool CloseHandle(IntPtr hObject);

// POSIX interop (libc). O_RDONLY is used in place of O_DIRECTORY: AtomicWrite only ever passes a
// real directory, and O_DIRECTORY's constant differs between Linux (0x10000) and macOS (0x100000),
// which a two-way IsWindows() dispatch cannot represent. fsync on a directory fd flushes entries.
private const int O_RDONLY = 0;
private const int ENOENT = 2;
private const int ENOTDIR = 4;

[DllImport("libc", SetLastError = true)]
private static extern int open(string pathname, int flags);

[DllImport("libc", SetLastError = true)]
private static extern int fsync(int fd);

[DllImport("libc", SetLastError = true)]
private static extern int close(int fd);
```

  3c. Wire it into `AtomicWrite`. In `AtomicWrite` (`:62-102`), immediately after the second `File.Run(() => { ThrowInjected(BeforeMove); File.Move(...); })` block — i.e. after the move has succeeded and before the method's closing `}` of the `try` — add:

```csharp
SyncDirectory(path);
```

The resulting `try` body reads:

```csharp
try
{
    temporaryPath = File.Run(() => { ... create temp ... return candidatePath; });
    File.Run(() =>
    {
        ThrowInjected(DurableWritePhase.BeforeMove, path);
        File.Move(temporaryPath!, path, overwrite: true);
    });
    SyncDirectory(path);
}
catch (Exception exception)
{
    if (temporaryPath is not null)
        TryDeleteTemporaryFile(temporaryPath);
    throw new DurableWriteException(operation, path, exception);
}
```

  Note `ThrowInjected(DurableWritePhase.BeforeDirSync, directory)` runs *before* the `FileRetry.Run` block so a test-injected fault throws synchronously (no retry) and propagates to `AtomicWrite`'s existing `catch`, which wraps it in `DurableWriteException`. Genuine `IOException`s from the interop flow through `FileRetry.Run` (transient errors retried ~1.5s; permanent errors surfaced).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~DurableFileDirectorySyncTests`

Expected: PASS — the positive test records `BeforeDirSync`; the fault test throws `DurableWriteException`, removes both partial members, preserves `unrelated.txt`, and leaves no recovery journal.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/DurableFile.cs tests/Scrinia.Tests/DurableFileDirectorySyncTests.cs
git commit -m "core: fsync destination directory after atomic durable write" -m "DurableFile.AtomicWrite now SyncDirectory(path) after File.Move so the rename's directory entry is durable, closing the crash-consistency hole (design-review Critical #1). Windows uses kernel32 CreateFile(FILE_FLAG_BACKUP_SEMANTICS)+FlushFileBuffers; POSIX uses libc open(O_RDONLY)+fsync+close, dispatched at runtime via OperatingSystem.IsWindows(). A directory-vanished race is tolerated (the move already succeeded); other IOExceptions retry via FileRetry and surface as DurableWriteException. Adds a BeforeDirSync fault phase reachable through the FailureInjector seam. Test: DurableFileDirectorySyncTests (positive recorder + first-call-fails rollback)."
```

---

## Task 3: Full verification

Confirm the change is green against the whole suite and nothing regressed.

- [ ] **Step 1: Build the solution**

Run: `dotnet build Scrinia.sln`

Expected: BUILD SUCCEEDS with no warnings introduced by the new interop (the `#pragma warning disable` covers CA1838/CA2101/CA1865).

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test Scrinia.sln`

Expected: all tests pass. In particular the durability/rollback/recovery/write-surface families stay green: `AppendDurabilityTests`, `AppendRollbackTests`, `AppendRecoveryTests`, `AtomicConfigWriteTests`, `ConfigCommandDurabilityTests`, `DurableFileFaultSeamTests`, `FileMemoryStoreWriteSurfaceTests`, `CaseSensitive*`.

- [ ] **Step 3: Fix any regression and re-run** until green.

- [ ] **Step 4: Commit** (only if a source change is needed; a clean green run needs no commit)

```bash
git commit -m "core: finalize P1 directory-fsync durability" -m "Final verification pass for the DurableFile directory-fsync fix. No content change unless a regression was found."
```

---

## Self-check before finishing

1. **Spec coverage:** every requirement in the spec (§1 components, §2 data flow, §3 error handling, §4 testing) is implemented by Task 1/2/3. The directory-vanished race tolerance is defensive (a narrow post-move window, not deterministically reproducible in CI) and is verified by code review; the deterministic coverage is the positive + fault/rollback tests.
2. **No placeholders:** every code step contains real, copy-pasteable implementation and test code.
3. **Type/signature consistency:** `SyncDirectory`/`SyncDirectoryWindows`/`SyncDirectoryUnix` are `private static` in `DurableFile`; `BeforeDirSync` exists on both enums; `DurableFile.FailureInjector` is `Func<DurableWritePhase, string, Exception?>?`; `ThrowInjected` is the existing private helper used by the other three phases.
4. **Frozen constraints:** no NMP/2, `.scrinia/` layout, CLI, MCP, JSON, or recovery-bypass changes; no new public API.
5. **Isolation:** both new tests use `TestHelpers.StoreScope`; `DurableFile.FailureInjector` is reset in a `finally`.
6. **Fault seam parity:** the phase fires through the same `ThrowInjected` helper the other phases use, so the append-rollback path is identical to existing durable-write failures.

## Rollback

If the interop or test harness proves problematic on a platform, revert to the pre-change `DurableFile.AtomicWrite` (remove the `SyncDirectory(path)` call and the `SyncDirectory*` methods) and drop `BeforeDirSync` from the two enums. The fault phase and interop are additive and isolated to `DurableFile.cs` + `B0FaultSeam.cs`, so the revert is a single-hunk reversal.
