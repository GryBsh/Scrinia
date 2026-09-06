# Global Tool Packaging (Phase 5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the immutable NuGet global tool `scri`, move all mutable models/plugins/state to `SCRINIA_HOME`, migrate both legacy asset locations once, and document the exact installation and diagnostic contract.

**Architecture:** Phase 2 owns `UserHome.Resolve()` and Phase 5 consumes that resolver everywhere. The package contains only the managed `scri` global-tool payload, embedded resources, and package readme; runtime writes go only below `SCRINIA_HOME`. Plugins are separately installed and discovered only at `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/`, with version/RID selection and plugin state in that directory.

**Tech Stack:** .NET 10 (`net10.0`), NuGet global tools, xUnit, `dotnet pack`, `dotnet tool`, `dotnet test`, `dotnet build`, isolated local NuGet feed. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md` §3.1, §4.3, §10.1–§10.3, §13.1, §13.5; `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` R3-01, R4-04, R5-01.

## Progress / Phase Tracking

| Phase | Status | Integration | SHA | Date |
|-------|--------|-------------|-----|------|
| Phase 5 — global tool packaging | COMPLETE | fast-forward into `main` from `sdd/global-tool-packaging` | `59eb939` | 2026-09-05 |

### Completion log

#### Phase 5 — COMPLETE (2026-09-05, `59eb939`)

Phase 5 (global tool packaging) is COMPLETE and integrated into `main` at `59eb939` via fast-forward from `sdd/global-tool-packaging`.

All tasks (1, 2, 2A, 3, 4, 5, 6, 7, 8) implemented/reviewed/approved via subagent-driven development (Task 6 required one glob-fix round; Task 2A required one digest-population round).

Independent exit gate PASSED at `59eb939`: `dotnet build Scrinia.sln -c Release` 0 errors; `dotnet test Scrinia.sln -c Release` 0 failed (Scrinia.Tests 2686 passed/20 skipped, Embeddings.Tests 41/41, Retrieval.Benchmarks.Tests 272/272); independent filters all PASS (PackageContent 11, ModelRelocation 10, PluginDiscovery+DocContract 19, GlobalToolInstall 1, ModelDownloadIntegrity 9, PackagingManifest/ReleaseWorkflow 20+3 skips); no AppContext.BaseDirectory runtime writes; no flat plugin fallback; no Git process in Phase 5 files; no Phase 1/Phase 4 ownership violation; CHANGELOG released entries byte-identical (Unreleased-only addition).

Phase 2 UserHome.Resolve() consumed unchanged; Phase 3 frozen contract untouched.

## Global Constraints

- Phase 2 owns the `UserHome` resolver. Phase 5 consumes `UserHome.Resolve()`; it must not redefine, wrap, or reorder Phase 2 config precedence.
- The NuGet global-tool package is immutable after installation. `AppContext.BaseDirectory` is never written at runtime.
- `SCRINIA_HOME` is the only mutable home for config, models, plugins, and plugin state. Workspace memory remains `<workspace-root>/.scrinia/`.
- The mandatory plugin path is exactly `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/`; there is no flat plugin fallback.
- Discovery selects a configured plugin name, compatible version, and current RID from that hierarchy; executable, state, and plugin-downloaded models use the selected version/RID location or `SCRINIA_HOME/models` as specified.
- Perform one idempotent, best-effort migration of both executable-relative legacy `models` and executable-relative legacy `plugins` into the new home. Never copy or write into the package directory.
- The package excludes `Scrinia.Merge`, `scri-merge`, the Vulkan plugin, Vulkan/LLamaSharp RID-native assets, downloaded models, repository data, `.scrinia`, and mutable caches. Vulkan is separately installed.
- The single packaged command supports both `scri serve` and `scri shared serve`; isolated smoke tests must perform both MCP handshakes.
- Phase 5 does not edit Phase 1 merge/reconcile material or Phase 4 hints/hooks/agent artifacts/shared onboarding, even where files are shared; ownership is by exact heading or line subject.
- Durable text is UTF-8 without BOM with LF endings. Never stage `cmds.txt`, never push, and never launch Git.

---

## Files and Ownership

### Modified

- `src/Scrinia/Commands/ScriniaCommands.Setup.cs` — consume `UserHome.Resolve()`, migrate executable-relative models/plugins once, and use `SCRINIA_HOME` diagnostics.
- `src/Scrinia/Commands/WorkspaceSetup.cs` — only the named Phase 5 methods below; consume the mandatory plugin hierarchy and home models path.
- `src/Scrinia/Scrinia.csproj` — global-tool metadata, package readme, package allowlist/denylist.
- `README.md` — the `Install` and `Where data lives` headings only; Phase 4 onboarding is untouched.
- `docs/getting-started.md` — the exact `Model2Vec` and `Install the global tool` headings only; Phase 4 onboarding is untouched.
- `docs/cli-reference.md` — exact model/plugin path examples and setup diagnostics only; merge/reconcile command rows are untouched.
- `docs/security.md` — exact `Plugin trust` and `Immutable tool package` headings only; Phase 4 shared-threat guidance is untouched.
- `docs/architecture/cli.md` — exact `Embeddings integration` and `Plugin host` headings only.
- `docs/troubleshooting.md` — exact headings `Model not found`, `Semantic search not working`, `Vulkan plugin not loading`, `Plugin crashes / degraded to BM25-only`, `Plugin not found despite being installed`, and `Access denied when publishing`; the Phase 1 heading `Manual Git conflict cleanup after merge retirement` is untouched.
- `docs/plugin-authoring.md` — exact `Plugin location and installation` heading only; the authoring contract remains unchanged.
- `.github/workflows/release.yml` — global-tool pack/push job; local release-tree job remains intact.
- `publish.projects.tsv`, `publish.ps1`, `publish.sh` — explicit local-tree versus NuGet-package contract comments/assertions.
- `CHANGELOG.md` — one new unreleased Phase 5 section only; released entries are untouched.

### Added

- `src/Scrinia/package-readme.md`
- `docs/installation.md`
- `tests/Scrinia.Tests/ModelRelocationTests.cs`
- `tests/Scrinia.Tests/PackageContentTests.cs`
- `tests/Scrinia.Tests/GlobalToolInstallTests.cs`
- `tests/Scrinia.Tests/PluginDiscoveryDiagnosticTests.cs`

### Not owned

- `src/Scrinia.Core/Configuration/UserHome.cs` and `tests/Scrinia.Tests/UserHomeTests.cs` — Phase 2.
- `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `src/Scrinia.Mcp/ScriniaPrimer.cs`, `src/Scrinia.Mcp/prompts/guide.md`, and Phase 4 onboarding headings — Phase 4.
- Phase 1 merge/reconcile commands, skills, cleanup heading, and merge package.
- `docs/superpowers/**`, `docs/reviews/**`, and released changelog history.

## Interfaces

### Consumed from Phase 2

- `public static string Resolve()` — uses a non-empty absolute `SCRINIA_HOME` value exactly, rejects an empty or relative override with `ScriniaConfigurationException`, and otherwise returns `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scrinia")`.
- `WorkspaceSetup.GetConfigValue(string key)` and Phase 2's config/tracking contract — unchanged and not reimplemented.

### Consumed from existing code

- `Model2VecModelManager.IsModelAvailable(string modelDir)` and `EnsureModelAsync(string modelDir, ILogger logger, CancellationToken ct = default)`.
- `McpPluginHost.StartAsync(string embeddingsExe, string dataDir, string modelsDir, Func<...> GetConfigValue, CancellationToken)`.
- `DurableFile.AtomicWriteAllText(string path, string contents)` and `PathContainment.IsWithin(string baseDir, string candidate)`.

### Phase 5-owned `WorkspaceSetup` methods

Keep these exact method names and existing signatures while changing their path consumers: `BuildEmbeddingOptions`, `LoadPluginsAsync`, `GetPluginName`, `ResolveEmbeddingsPluginExe`, `TryLoadVulkanPluginAsync`, `IsHttpEmbeddingsProvider`, `LoadExplicitHttpEmbeddings`, `LoadBuiltInEmbeddings`, `MaybeReindexAfterModelSwitch`, `ForceReindexAsync`, and `InstallEmbeddingEventSinks`.

### Produced

- `SCRINIA_HOME/models/` for Model2Vec and plugin-downloaded models.
- `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` for separately installed plugin executable/assets.
- `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/state/` for mutable plugin state.
- One package command named `scri`, with both server modes and the package readme.

## Test Plan and Implementation Steps

### Task 1: Consume Phase 2 home resolution and relocate Model2Vec

**Files:** Modify `src/Scrinia/Commands/ScriniaCommands.Setup.cs`; test `tests/Scrinia.Tests/ModelRelocationTests.cs`.

- [x] **Step 1: Write failing tests** that drive setup/model resolution with an isolated `SCRINIA_HOME` and assert Model2Vec resolves to `SCRINIA_HOME/models/m2v-MiniLM-L6-v2`, never `AppContext.BaseDirectory/models`.
- [x] **Step 2: Run** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ModelRelocationTests`; **Expected:** FAIL because setup still uses executable-relative paths.
- [x] **Step 3: Replace** every Phase 5 model path in setup with `Path.Combine(UserHome.Resolve(), "models", "m2v-MiniLM-L6-v2")`; use the existing Phase 2 resolver without redefining it.
- [x] **Step 4: Run** the focused filter; **Expected:** PASS.
- [x] **Step 5: Commit** with `feat: relocate Model2Vec data under SCRINIA_HOME`.

### Task 2: Mandatory version/RID plugin discovery

**Files:** Modify `src/Scrinia/Commands/WorkspaceSetup.cs`; test `tests/Scrinia.Tests/ModelRelocationTests.cs` and `tests/Scrinia.Tests/PluginDiscoveryDiagnosticTests.cs`.

- [x] **Step 1: Write failing tests** for discovery at exactly `SCRINIA_HOME/plugins/scri-plugin-embeddings/1.2.3/<current-rid>/`, compatible-version selection, current-RID selection, missing version/RID, and rejection of both `SCRINIA_HOME/plugins/<plugin>/<plugin>` and any flat plugin path. Assert diagnostics name the searched hierarchy and `SCRINIA_HOME`.
- [x] **Step 2: Run** the two focused filters; **Expected:** FAIL because discovery uses executable-relative or flat paths.
- [x] **Step 3: Update** only the named `WorkspaceSetup` methods. Resolve `plugins/<plugin>/<version>/<rid>/`, select the configured compatible version and current RID deterministically, verify containment with `PathContainment.IsWithin`, and pass `Path.Combine(UserHome.Resolve(), "models")` to `McpPluginHost.StartAsync`. Do not add a flat fallback.
- [x] **Step 4: Run** both filters; **Expected:** PASS, including exact not-found/degraded diagnostics.
- [x] **Step 5: Commit** with `feat: discover plugins by version and RID under SCRINIA_HOME`.

### Task 2A: Verified model-download identity and atomic promotion

**Files:** Modify `src/Scrinia.Core/Embeddings/Model2VecModelManager.cs` and `src/Scrinia.Plugin.Embeddings/VulkanModelManager.cs`; add `tests/Scrinia.Plugin.Embeddings.Tests/ModelDownloadIntegrityTests.cs`. The existing download callers are `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingBenchmarkAllTests.cs:38-58` and the production setup paths in `src/Scrinia/Commands/ScriniaCommands.Setup.cs:84-88,420-437`.

- [x] **Step 1: Write failing tests** in `ModelDownloadIntegrityTests.cs` against both current managers' `EnsureModelAsync` entry points. Use a fake `HttpMessageHandler`/response stream and an isolated temporary `SCRINIA_HOME/models` directory to assert: (a) the asset key/path contains the model identifier and the expected lowercase SHA-256 content digest; (b) a response whose bytes do not match the expected digest throws before promotion and leaves no final model file; (c) two concurrent downloads use distinct per-download temporary paths and produce one valid final asset without exposing either partial file; (d) cancellation, a stream failure, or a pre-seeded crash/partial temp leaves no visible model and a later retry starts cleanly; and (e) matching bytes are flushed, validated, atomically promoted into `SCRINIA_HOME/models`, and are visible only after promotion. Name tests `DigestMismatch_IsRejectedBeforePromotion`, `ConcurrentDownloads_UseUniqueTempsAndPublishOneValidAsset`, `CrashOrPartialTemp_IsNotVisible_AndRetrySucceeds`, and `MatchingDigest_IsAtomicallyPromoted`.
- [x] **Step 2: Run** `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter FullyQualifiedName~ModelDownloadIntegrityTests`; **Expected:** FAIL because the managers currently use fixed `<file>.tmp` names, do not verify content digests, and promote any HTTP response bytes.
- [x] **Step 3: Implement** the minimum shared behavior in each manager while retaining the public `EnsureModelAsync(string modelDir, ILogger logger, CancellationToken ct = default)` contract: define each downloadable asset by model identifier, URL, filename, and expected SHA-256; derive the destination below `Path.Combine(UserHome.Resolve(), "models")` from the identifier plus verified digest; create a unique sibling temp file per download (PID/ GUID or equivalent); hash while streaming and flush/close before validation; reject and delete a digest-mismatched, cancelled, or failed temp; and atomically move only a validated temp into the final path. If another download wins the promotion race, discard the validated duplicate and treat the already-valid destination as success. Keep retries idempotent and ensure no `.tmp` or partial file is considered available by `IsModelAvailable`.
- [x] **Step 4: Run** the focused filter again; **Expected:** PASS for digest mismatch, concurrent downloads, crash/partial temp recovery, retry, and successful promotion. Then run `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter FullyQualifiedName~EmbeddingProvidersFixture` to confirm the existing Model2Vec/Vulkan callers still compile against the unchanged manager entry points.
- [x] **Step 5: Commit** with `fix: verify model downloads before atomic promotion`.

### Task 3: One-time migration of both legacy asset trees

**Files:** Modify `src/Scrinia/Commands/ScriniaCommands.Setup.cs`; test `tests/Scrinia.Tests/ModelRelocationTests.cs`.

- [x] **Step 1: Write failing tests** for executable-relative `models` migration, executable-relative `plugins` migration into the mandatory plugin/version/RID hierarchy, idempotent second startup, existing destination preservation, traversal rejection, and move/copy failure remaining non-fatal. Test that no package-directory file is created.
- [x] **Step 2: Run** the relocation filter; **Expected:** FAIL because the migration helper and both call sites are absent.
- [x] **Step 3: Implement** one internal idempotent migration boundary with exact inputs `string legacyRoot, string homeRoot`, canonicalize and contain every destination, migrate legacy model content to `SCRINIA_HOME/models`, migrate legacy plugin content to the selected `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/`, record completion in `SCRINIA_HOME` state, and swallow only expected migration I/O failures after preserving startup. Never write under `AppContext.BaseDirectory`.
- [x] **Step 4: Run** the focused filter; **Expected:** PASS for both asset classes and second-run behavior.
- [x] **Step 5: Commit** with `feat: migrate legacy models and plugins into SCRINIA_HOME once`.

### Task 4: Immutable NuGet global-tool package and readme

**Files:** Modify `src/Scrinia/Scrinia.csproj`; create `src/Scrinia/package-readme.md`; test `tests/Scrinia.Tests/PackageContentTests.cs`.

- [x] **Step 1: Write failing package tests** that pack Release output and assert the allowlist contains the `scri` command, managed dependencies, embedded prompts/skills/schemas/setup templates, and `package-readme.md`; assert the denylist contains no Merge, `scri-merge`, Vulkan/LLamaSharp/native RID asset, model, repository, `.scrinia`, or mutable cache.
- [x] **Step 2: Run** the package filter; **Expected:** FAIL because the project is not yet the required global tool.
- [x] **Step 3: Add** `PackageId` `scrinia`, `PackageType` `GlobalTool`, `ToolCommandName` `scri`, `PackTool` `true`, `PackageReadmeFile` `package-readme.md`, and explicit package include/exclude rules. Keep Vulkan and `Scrinia.Merge` out of the package. Write the readme with exact install/update/uninstall commands, immutable package wording, `SCRINIA_HOME` layout, mandatory plugin path, both server commands, and separate Vulkan installation.
- [x] **Step 4: Run** `dotnet pack src/Scrinia/Scrinia.csproj -c Release` and the focused package filter; **Expected:** PASS with the exact allowlist/denylist.
- [x] **Step 5: Commit** with `feat: package scri as an immutable NuGet global tool`.

### Task 5: Isolated local-feed install, update, uninstall, and both handshakes

**Files:** Create `tests/Scrinia.Tests/GlobalToolInstallTests.cs`.

- [x] **Step 1: Write failing smoke tests** that pack Release output into a temporary feed, install into an empty isolated tool path outside the source checkout, run `scri --version`, handshake `scri serve` and `scri shared serve` over MCP stdio, update from a second package, restart after config/state changes, and uninstall cleanly. Capture the installed package tree before execution and assert it is byte-identical afterward; assert models/plugins/state are below isolated `SCRINIA_HOME`.
- [x] **Step 2: Run** the install filter; **Expected:** FAIL because packaging/install and relocation are incomplete.
- [x] **Step 3: Implement** only test helpers that invoke `dotnet pack`, `dotnet tool install --tool-path`, `dotnet tool update --tool-path`, `dotnet tool uninstall --tool-path`, and both MCP initialize/initialized handshakes with verified exit codes. Do not skip either server handshake.
- [x] **Step 4: Run** the filter; **Expected:** PASS for install, update, both handshakes, immutable package tree, isolated assets, and uninstall.
- [x] **Step 5: Commit** with `test: verify isolated global-tool lifecycle and server handshakes`.

### Task 6: Release manifest, scripts, and workflow

**Files:** Modify `publish.projects.tsv`, `publish.ps1`, `publish.sh`, `.github/workflows/release.yml`.

- [x] **Step 1:** Add exact comments/assertions stating the local publish scripts produce single-file release trees, while the one NuGet global tool is `scri`; Vulkan and Merge are not packaged.
- [x] **Step 2:** Add a release job that packs `src/Scrinia/Scrinia.csproj` in Release and pushes the resulting `.nupkg` to NuGet using the repository's existing secret convention; retain the existing local release-tree job unchanged.
- [x] **Step 3:** Verify the workflow YAML and script assertions; **Expected:** exactly one packaged command, no Vulkan/Merge payload, local tree contract preserved.
- [x] **Step 4:** Commit with `chore: publish the scri global tool alongside release trees`.

### Task 7: Phase 5 living documentation and exact diagnostics

**Files:** Modify the exact headings listed in Files and Ownership; create `docs/installation.md`; add the unreleased `CHANGELOG.md` section; test `tests/Scrinia.Tests/PluginDiscoveryDiagnosticTests.cs`.

- [x] **Step 1: Write failing doc/source tests** for the exact headings `Install`, `Where data lives`, `Model2Vec`, `Install the global tool`, `Plugin trust`, `Immutable tool package`, `Embeddings integration`, `Plugin host`, `Plugin location and installation`, and all six troubleshooting headings. Assert `SCRINIA_HOME/models`, the mandatory version/RID plugin path, no flat fallback, separate Vulkan install, and no stale `{exeDir}/models` or `{exeDir}/plugins` in Phase 5-owned living docs, Phase 5-owned source/help text, setup output, or diagnostics. The scan fixture must explicitly exclude `docs/superpowers/**`, `docs/reviews/**`, `docs/planning/**`, dated planning/review snapshots elsewhere under `docs/`, and released changelog history; those historical artifacts retain their historical-doc exemption.
- [x] **Step 2: Run** the documentation/diagnostic filter; **Expected:** FAIL.
- [x] **Step 3: Edit** only the Phase 5 headings. Add `docs/installation.md` and package-readme installation/layout material; keep Phase 4 onboarding sections and Phase 1 merge/conflict material unchanged. Update source-generated path diagnostics to name `SCRINIA_HOME` and the mandatory hierarchy.
- [x] **Step 4: Run** the focused filter and scan only the Phase 5-owned living/source/help scope: `rg -n "exeDir/(plugins|models)" README.md src/Scrinia/Commands/ScriniaCommands.Setup.cs src/Scrinia/Commands/WorkspaceSetup.cs src/Scrinia/package-readme.md docs/getting-started.md docs/cli-reference.md docs/security.md docs/architecture/cli.md docs/troubleshooting.md docs/plugin-authoring.md docs/installation.md`. Do not scan `docs/superpowers/**`, `docs/reviews/**`, `docs/planning/**`, any other dated planning/review artifacts, or released changelog history; preserve the historical-doc exemption for those artifacts. **Expected:** no stale matches in the owned scope; historical documents remain unchanged.
- [x] **Step 5: Commit** explicit documentation paths with `docs: document global-tool installation and plugin layout`.

### Task 8: Independent Phase 5 exit gate

- [x] **Step 1:** Run `dotnet build Scrinia.sln -c Release`; **Expected:** clean.
- [x] **Step 2:** Run `dotnet test Scrinia.sln -c Release`; **Expected:** all non-benchmark tests PASS.
- [x] **Step 3:** Run package allowlist/denylist, relocation/migration, plugin discovery, diagnostic, and isolated lifecycle filters independently; **Expected:** each PASS.
- [x] **Step 4:** Verify no `AppContext.BaseDirectory` runtime writes, no flat plugin fallback, no Git process, no Phase 1/Phase 4 file ownership violation, and no released-history change.
- [x] **Step 5:** Commit remaining Phase 5 files by explicit path; do not push.

## Exit Gate

Phase 5 is complete only when the independent gates prove: Phase 2's resolver is consumed unchanged; models and plugin state use `SCRINIA_HOME`; plugin discovery uses only `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/`; both executable-relative legacy models and plugins migrate once; the immutable package contains only the exact allowlist; Vulkan is excluded; install/update/uninstall and both MCP handshakes pass from an isolated local feed; release scripts/workflow publish the one `scri` tool; exact living-doc headings and source diagnostics are updated; Phase 1 and Phase 4 ownership remains disjoint; and the full build/test suite is green.

## Coverage Matrix

| Citation | Requirement | Task |
|---|---|---|
| §3.1 | Phase 2-owned deterministic `UserHome.Resolve()` consumed by Phase 5 | 1 |
| §4.3, §10.2 | Immutable package; models, plugins, and state under `SCRINIA_HOME` | 1–3, 5 |
| §4.3 | Model asset keys include verified content digests; unique per-download temps, pre-promotion validation, atomic promotion, invisible failed/partial downloads, and retry/concurrency recovery | 2A |
| §10.1 | One NuGet global-tool command `scri`; both server modes | 4–5 |
| §10.1–§10.3 | Exact package allowlist/denylist and Vulkan exclusion | 4–6 |
| §10.2 | Version/RID plugin hierarchy, discovery, state, and migration | 2–3 |
| §13.1, §13.5 | Package readme, installation docs, release artifacts, diagnostics | 4, 6–7 |
| R3-01, R4-04 | No overlap with Phase 1 or Phase 4; disjoint headings | 7–8 |
| R5-01 | Existing exact staging guidance remains outside Phase 5 | 7–8 |

## Citations

- `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md` §3.1, §4.3, §10.1–§10.3, §13.1, §13.5.
- `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` R3-01 and R4-04: exclusive phase ownership and exact living-document headings.
- `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` R5-01: Phase 3's authoritative staging guidance remains unchanged.
