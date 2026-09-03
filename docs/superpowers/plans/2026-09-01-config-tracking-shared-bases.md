# Config and Tracking (Shared Bases) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add flat workspace tracking/enabled scalars and a typed `SCRINIA_HOME` shared-base registry whose contracts and implementations live in `Scrinia.Core`, with `Scrinia` acting only as the CLI adapter/host.

**Architecture:** `Scrinia.Core` owns the complete reusable boundary consumed by `Scrinia.Mcp`: user-home resolution, typed home configuration, flattened workspace configuration, tracking policy, migration recognition/state, shared-base validation/registry, transactional writes, and result/error types. `Scrinia` owns command parsing, environment acquisition, console/JSON presentation, and host wiring only. `Scrinia.Mcp` references Core contracts directly and never references the executable; Core has no reverse reference. Phase 5 consumes the Core `UserHome` and config contracts unchanged.

**Tech Stack:** .NET 10 (`net10.0`), xUnit, `System.Text.Json` source generation, `DurableFile`, `PathContainment`, `dotnet build`, and `dotnet test`. No new project or dependency.

**Spec:** `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md` §§3.1–3.4, 4.1, 7.1–7.2, 8.1–8.3, 10.2, 13.1, 13.5, 14 items 1, 3, 8, 9, 13, 17. Review record `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` R1-02, R4-01, R5-02, R5-04, R6-04.

## Global Constraints

- `.scrinia/config.json` remains a flat `Dictionary<string,string>` serialized by `ConfigJsonContext.Default.DictionaryStringString`; only `Scrinia:Shared:Enabled` and `Scrinia:Workspace:Tracking` are added.
- Core owns one resolver: `UserHome.Resolve()`; a non-empty absolute `SCRINIA_HOME` is used exactly, an empty or relative value fails closed, and an unset value resolves exactly to `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scrinia")`.
- `SCRINIA_HOME/config.json` is exactly typed `{schemaVersion:1,sharedBases:{name:{root}}}`. Duplicate properties, missing required fields, malformed JSON, invalid values, and `schemaVersion > 1` fail closed.
- Tracking precedence is single-underscore env, double-underscore env, workspace config, then default. Enabled is read directly from workspace config; neither Enabled environment spelling is honored.
- Shared bases use fixed `project-git`; only local workspace tracking supports `local|project-git`.
- All config/registry mutations use an interprocess lock and same-directory `DurableFile` atomic replacement. No active Git invocation; filesystem-only `.git`/`gitdir:` evidence and print-exact commands only.
- Exact test coverage includes atomic fault injection, Git/worktree markers, every migration state/error, containment/collision, and privacy of disabled registry names.
- UTF-8 without BOM, LF endings, safe containment, explicit paths, no `git add .`/`-A`, no push, and `cmds.txt` remains untracked.

---

## Core Contracts and Files

### Core production files (all shared abstractions)

- `src/Scrinia.Core/Configuration/UserHome.cs` — `public static string Resolve()` and `public static string Resolve(string? environmentValue, string localApplicationData)`; throws `ScriniaConfigurationException` for empty/relative override and returns the exact default otherwise.
- `src/Scrinia.Core/Configuration/HomeConfig.cs` — `HomeConfig`, `SharedBaseEntry`, `SharedBasesMap`, `IHomeConfigStore`, and `HomeConfigStore` for typed registry load/save.
- `src/Scrinia.Core/Configuration/WorkspaceConfig.cs` — `IWorkspaceConfig`, `WorkspaceConfig`, `GetValue`, `SetValue`, `UnsetValue`, `ParseEnabled`, `ParseTracking`, and atomic `Save` over the flat dictionary.
- `src/Scrinia.Core/Configuration/TrackingPolicy.cs` — `TrackingPolicy` enum and `ITrackingPolicyStore`/`TrackingPolicyStore`.
- `src/Scrinia.Core/Configuration/SharedBaseRegistry.cs` — `ISharedBaseRegistry`, `SharedBaseRegistry`, canonical naming, root evidence, containment/collision, and exact registry operations.
- `src/Scrinia.Core/Configuration/ManagedIgnoreBlock.cs` and `SharedClassifier.cs` — exact blocks, drift detection, and one ordered derived set.
- `src/Scrinia.Core/Migration/LegacyMergeRecognition.cs` — the Phase 1-preserved pure recognition constants/functions; no active merge behavior.
- `src/Scrinia.Core/Migration/LegacyMigrationState.cs` — all §8.3 states, transitions, recognized-line cleanup plan, exact merge-config comparison, and error precedence.
- `src/Scrinia.Core/Configuration/ConfigResults.cs` — typed command results, error codes, exit classes, manifest, warnings, `gitIndexStatus`, and `gitCommands`.
- `src/Scrinia.Core/Configuration/HomeConfigJsonContext.cs` — source-generated JSON context; `Scrinia.Mcp` may use this context directly.

### CLI adapter/host files

- `src/Scrinia/Commands/SharedBaseCommands.cs`, `SharedEnableCommands.cs`, `TrackingCommands.cs`, `WorkspaceSetup.cs`, `ConfigJsonContext.cs`, and `CliJsonContext.cs` only bind arguments/environment, call Core interfaces, and format output. They contain no registry/config algorithms.
- `src/Scrinia.Mcp` references `Scrinia.Core` only. It consumes `IHomeConfigStore`, `IWorkspaceConfig`, `ISharedBaseRegistry`, `UserHome.Resolve()`, `TrackingPolicy`, and typed result/error contracts; it has no reference to `Scrinia`.

## Exact Interfaces

```csharp
public static class UserHome
{
    public static string Resolve();
    public static string Resolve(string? scriniaHome, string localApplicationData);
}

public interface IHomeConfigStore
{
    HomeConfig Load(string homeDirectory);
    void Save(string homeDirectory, HomeConfig config);
}

public interface IWorkspaceConfig
{
    string? GetValue(string key);
    void SetValue(string key, string value);
    void UnsetValue(string key);
    IReadOnlyList<string> GetEnabledBases();
    TrackingPolicy GetTrackingPolicy();
    void Save();
}

public interface ISharedBaseRegistry
{
    SharedBaseResult Add(string name, string root);
    SharedBaseResult Remove(string name);
    IReadOnlyList<SharedBaseEntry> List(bool includeDisabled);
    SharedBaseResult Enable(string name);
    SharedBaseResult Disable(string name);
    SharedBaseEntry? Lookup(string name);
    IReadOnlyList<SharedBaseCollision> Collisions();
}
```

`HomeConfig` has required `SchemaVersion` and `SharedBases` properties. `SharedBaseEntry` has required `Root` and a registry-only enabled/name association; disabled names remain private to the registry and are never emitted by `List(false)` or startup resolution. Core result types carry declared error code and exit class, not console text.

## Tasks

### Task 1 — Core user-home resolver and typed JSON boundary

**Files:** Create the Core files listed above; add source-generated `HomeConfigJsonContext`; tests `tests/Scrinia.Tests/UserHomeResolverTests.cs`, `HomeConfigSerializationTests.cs`, `HomeConfigParserValidationTests.cs`.

- [ ] Test `UserHome.Resolve`: absolute override is exact; unset uses exact LocalApplicationData/Scrinia; empty and relative values throw `ScriniaConfigurationException` and do not fall back.
- [ ] Test serialization round-trip and explicit parser validation for malformed JSON, duplicate `schemaVersion`, duplicate `sharedBases`, missing either required top-level field, missing `root`, invalid schema, and `schemaVersion > 1`; use a parser layer that inspects UTF-8 JSON property names before source-generated deserialization, because source generation alone is not the duplicate-property policy.
- [ ] Run the focused filters; expect failure.
- [ ] Implement `UserHome`, required DTOs, `IHomeConfigStore`, `HomeConfigStore`, `HomeConfigJsonContext`, and explicit `JsonDocument` duplicate/required/schema validation. Use `DurableFile` for writes and no executable reference.
- [ ] Rerun tests; commit only Core files and these tests.

### Task 2 — Core flat workspace config and precedence

**Files:** Create/modify `src/Scrinia.Core/Configuration/WorkspaceConfig.cs`, `TrackingPolicy.cs`, `ConfigResults.cs`; adapter `src/Scrinia/Commands/WorkspaceConfig.cs`/`WorkspaceSetup.cs`; tests `WorkspaceConfigScalarTests.cs`, `TrackingPrecedenceTests.cs`, `NoSharedEnabledEnvTests.cs`.

- [ ] Test absent Enabled = zero; empty/empty-segment/duplicate/noncanonical Enabled = invalid; Tracking accepts only `local|project-git`; and `GetConfigValue` precedence is `SCRINIA_WORKSPACE_TRACKING` → `SCRINIA__WORKSPACE__TRACKING` → flat workspace key → default.
- [ ] Test `SCRINIA_SHARED_ENABLED` and `SCRINIA__SHARED__ENABLED` cannot alter Enabled.
- [ ] Run focused filters; expect failure.
- [ ] Implement flat dictionary parsing in Core. CLI supplies environment values through a host adapter; Core’s Enabled parser reads only the workspace dictionary. Move all shared logic out of executable `WorkspaceConfig`.
- [ ] Rerun tests and commit.

### Task 3 — Durable atomic writes and lock/fault gates

**Files:** Core `HomeConfigStore.cs`, `WorkspaceConfig.cs`; tests `AtomicConfigWriteTests.cs`.

- [ ] Inject `DurableFile.FailureInjector` at pre-move for workspace and home writes; assert original bytes remain, temp/lock artifacts are cleaned, registry is not committed, and the result is `CONFIG_WRITE_FAILED` with exit class 4.
- [ ] Test concurrent writers use the interprocess lock and same-directory replacement; assert unrelated flat keys survive.
- [ ] Run focused tests; expect failure.
- [ ] Implement both stores through `DurableFile.AtomicWriteAllText` within the existing lock discipline; never use `File.WriteAllText` for these mutations.
- [ ] Rerun tests and commit.

### Task 4 — Core shared-base validation and registry

**Files:** Core `SharedBaseRegistry.cs`; tests `SharedBaseRegistryTests.cs`, `SharedBasePrivacyTests.cs`.

- [ ] Test canonical names (`^[a-z][a-z0-9-]{0,31}$`), absolute/existing roots, direct `.git` directory, worktree `.git` file beginning `gitdir:`, rejection of ancestor-only `.git`, `.scrinia` roots, traversal, nested/equivalent physical roots, and duplicate roots.
- [ ] Test disabled registry names remain private: startup/list without `includeDisabled` and MCP-facing lookup do not disclose disabled names or roots; explicit `list --all` is the only Core operation that returns them.
- [ ] Run focused tests; expect failure.
- [ ] Implement `ISharedBaseRegistry`/`SharedBaseRegistry` in Core with `PathContainment.IsWithin`, canonical physical comparison, locked typed home reads/writes, fixed shared `project-git`, and declared result codes.
- [ ] Rerun tests and commit.

### Task 5 — Core exact ignore block and classifier

**Files:** Core `ManagedIgnoreBlock.cs`, `SharedClassifier.cs`; tests `ManagedIgnoreBlockTests.cs`, `DerivedClassifierEquivalenceTests.cs`.

- [ ] Test exact LF/no-BOM local and anchored project-git blocks, missing/duplicate/reordered/malformed/colliding markers, and drift refusal.
- [ ] Test the ordered first-match derived set is identical for ignore output, fingerprint exclusion, and static cleanup pathspecs, including root/deep `.tmp` and root-child `.lock` cases.
- [ ] Run focused tests; expect failure.
- [ ] Implement one Core classifier source and derive every consumer from it; no merge classifier or Git invocation.
- [ ] Rerun tests and commit.

### Task 6 — Core migration-only state machine

**Files:** Core `LegacyMergeRecognition.cs`, `LegacyMigrationState.cs`; CLI adapter `TrackingCommands.cs`; tests `LegacyMigrationStateMachineTests.cs`, `LegacyMigrationErrorTests.cs`.

- [ ] Test every §8.3 state: `fresh`, `policy-required`, `legacy-clean`, `legacy-mixed`, `legacy-manual`, `legacy-unsafe`, `local`, `project-git`, and `policy-drift`; test `status`, `set local`, `set project-git`, and both `migrate-legacy --to` targets.
- [ ] Test every declared error and precedence: argument shape before config, `MANAGED_FILE_UNSAFE` before recognized legacy state, `LEGACY_MIGRATION_REQUIRED`, malformed config, `CONFIG_WRITE_FAILED`, and `IO_FAILURE` only for otherwise-unclassified I/O.
- [ ] Test only the two exact `.gitattributes` lines are removable, empty-only file deletion, exact legacy `merge.config` recognition, unrelated lines/bytes preserved, atomic rollback on injected failure, and static unset argv returned without execution.
- [ ] Run focused tests; expect failure.
- [ ] Implement all state detection and migration planning in Core, consuming the Phase 1 recognition type. The CLI adapter only maps command arguments and prints exact commands.
- [ ] Rerun tests and commit.

### Task 7 — Core transactional base commands

**Files:** Create Core `src/Scrinia.Core/Configuration/SharedBaseOperations.cs`; modify CLI adapters `src/Scrinia/Commands/SharedBaseCommands.cs` and `src/Scrinia/Commands/SharedEnableCommands.cs`; tests `tests/Scrinia.Tests/SharedBaseTransactionTests.cs` and `tests/Scrinia.Tests/SharedEnableTests.cs`.

- [ ] Test add validates and writes `<root>/.scrinia` plus managed block before home registry lock/commit; injected registry failure restores pre-add `.gitignore` bytes and removes a newly created empty directory.
- [ ] Test idempotent identical pair, existing name/different root, containment/collision, remove/list, enable/disable order preservation, final disable removing the flat key, and no-op repeat calls.
- [ ] Run focused tests; expect failure.
- [ ] Implement Core transactional operations and adapters with declared `SHARED_BASE_ALREADY_EXISTS`, `SHARED_BASE_ROOT_COLLISION`, `SHARED_TRACKING_POLICY_DRIFT`, and write-failure results.
- [ ] Rerun tests and commit.

### Task 8 — Core/CLI JSON result boundary and MCP contract test

**Files:** Core `ConfigResults.cs`, `HomeConfigJsonContext.cs`; CLI `CliJsonContext.cs`; tests `CliErrorPrecedenceTests.cs`, `SharedConfigJsonOutputTests.cs`, `McpCoreReferenceTests.cs`.

- [ ] Test every §8.1.1 nonzero code with exit class and precedence; every success/failure envelope has exactly one declared code when failing, and `gitIndexStatus` is `UNVERIFIED`.
- [ ] Test JSON fields for base, manifest, bases, tracking, state, warnings, and static `gitCommands`; disabled registry names are absent unless explicitly requested.
- [ ] Test project references: `Scrinia.Mcp` references Core contracts and has no reverse reference to `Scrinia`; executable adapters do not own duplicate config/base implementations.
- [ ] Run focused tests; expect failure.
- [ ] Register every new serializable type in the appropriate source-generated context and make CLI/MCP adapters consume Core results without redefining them.
- [ ] Rerun tests and commit.

### Task 9 — Living docs owned by Phase 2

**Files:** only the config/tracking/shared-base sections of `docs/getting-started.md`, `docs/cli-reference.md`, `docs/security.md`, `AGENTS.md`, and `src/Scrinia.Mcp/prompts/guide.md`.

- [ ] Document the two files, exact resolver rules, six command forms, fixed shared `project-git`, migration states/errors, atomic behavior, and disabled-name privacy without documenting Phase 3/4/5 surfaces.
- [ ] Verify no executable-owned contract, `{exeDir}/models`, `{exeDir}/plugins`, shared-MCP tool, active merge/reconcile surface, or placeholder appears in these sections.
- [ ] Commit only the named doc sections.

### Task 10 — Independent Phase 2 exit gate

- [ ] Run `dotnet build Scrinia.sln` and `dotnet test Scrinia.sln` with benchmarks excluded.
- [ ] Run repository searches proving Core contains all shared config/base abstractions consumed by `Scrinia.Mcp`, the executable contains adapters only, and no Core→Scrinia or MCP→Scrinia reverse reference exists.
- [ ] Re-run all focused test classes from Tasks 1–8, including fault injection, worktree markers, all migration states/errors, containment/collision, and disabled-name privacy.
- [ ] Verify Phase 5 can consume `UserHome.Resolve()` and the Core typed boundary unchanged; Phase 5 must not redefine resolution or precedence.

## Exit Criteria

- `UserHome.Resolve()` is the sole SCRINIA_HOME resolver with exact fail-closed empty/relative behavior and exact LocalApplicationData/Scrinia fallback.
- All shared config/base interfaces and implementations consumed by `Scrinia.Mcp` are in Core; `Scrinia` is adapter/host only; no reverse reference exists.
- Flat workspace config remains `Dictionary<string,string>` with only the two owned scalars and exact precedence/no-override rules.
- Typed home registry uses source-generated JSON plus explicit duplicate/required/schema/malformed validation and locked atomic writes.
- Base validation, fixed shared `project-git`, transactional registration, exact classifier, migration states/errors, privacy, JSON boundary, and every assigned fault/Git/worktree/containment test pass.
- Phase 5 consumes Core contracts unchanged; no Phase 5 model/plugin behavior is modified here.
- No active merge/reconcile/Git process behavior is reintroduced; only named migration-recognition artifacts remain from Phase 1.

## Coverage Matrix

| Spec/review ruling | Requirement | Assigned task |
|---|---|---|
| §3.1 | Sole fail-closed `UserHome` resolver and typed registry | Task 1; Task 10 |
| §3.2 | Flat scalars, precedence, no Enabled env, zero-key behavior | Task 2 |
| §3.3 | Canonical names, roots, visibility, fixed shared policy | Task 4; Task 8 |
| §3.4 | No usable bases outcome | Tasks 2, 4, 8 |
| §7.1–7.2 | No Git invocation; exact print-only manifest commands | Tasks 6–8 |
| §8.1.1 | Every command/error/precedence/exit class | Task 8 |
| §8.2 | Exact blocks, drift, atomic writes | Tasks 3, 5, 7 |
| §8.3, R5-04 | Every migration state and explicit legacy migration | Task 6 |
| §4.1, R5-02 | One ordered classifier and exact consumer equivalence | Task 5 |
| R4-01 | Fixed `project-git`, transactional add, collision/containment | Tasks 4, 7 |
| R6-04 | Complete tracking transition matrix | Task 6 |
| §13.1, §13.5, §10.2 | Core ownership and unchanged Phase 5 consumption | Tasks 8–10 |
