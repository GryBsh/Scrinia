# IMP-03 Publish Manifest and Workflow Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `publish.projects.tsv` the auditable source of publish selection, enforce parity across Bash, PowerShell, and release workflow, and verify supported archive layouts honestly.

**Architecture:** Preserve the six-column manifest and three existing rows. Add strict parity checks in `PublishManifestTests` and add a release-workflow validation step that runs the manifest parity test before archive upload, so the workflow cannot drift while the manifest remains the sole project inventory. Archive tests inspect actual output trees; foreign NativeAOT execution is reported as host-restricted.

**Refs:** `docs/superpowers/specs/2026-08-27-imp-03-publish-manifest-parity-design.md` (approved manifest/workflow architecture); `publish.projects.tsv`, `publish.ps1`, `publish.sh`, and `.github/workflows/release.yml` (repository publish sources).
ref: `docs/superpowers/specs/2026-08-27-imp-03-publish-manifest-parity-design.md`

**Tech Stack:** Bash, PowerShell, GitHub Actions YAML, .NET 10 publish, xUnit, FluentAssertions, `PublishManifestTests`.

## Global Constraints

- “Preserve the six-column manifest and current rows as the canonical project inventory.”
- “No project additions or removals.”
- “No replacement of `publish.projects.tsv` with a second generated list.”
- “No claim of native execution on a platform that was not executed.”
- “Missing Vulkan natives produce the existing explicit warning and are recorded as CPU fallback.”

---

## Dependencies / Order

- Task 1 defines the test-local manifest/parser fixtures. Task 2 consumes those checks in a separate prerequisite workflow job. Task 3 runs only after Tasks 1–2 and produces disposable publish outputs.
- Complete this plan before the `0.6.0` version/changelog plan and the final integration gate.
- Use one external disposable root for every publish/archive check: `SCRINIA_RELEASE_TEMP_ROOT` means `$env:TEMP\scrinia-release-verification\0.6.0` on Windows and `${TMPDIR:-/tmp}/scrinia-release-verification/0.6.0` on Unix. Never use an `artifacts/` directory in the repository.
- **Ref:** `docs/superpowers/specs/2026-08-27-imp-03-publish-manifest-parity-design.md` (external disposable-output constraint).

### Task 1: Extend manifest parity tests

**Files:**
- Modify: `tests/Scrinia.Tests/PublishManifestTests.cs`
- Read: `publish.projects.tsv`, `publish.ps1`, `publish.sh`, `.github/workflows/release.yml`

**Interfaces:**
- Consumes: existing `ManifestRow`, `ReadManifest`, `ReleaseWorkflowProjects`, `ManifestVocabulary` helpers.
- Produces: exact six-column/header/three-row assertions, script/workflow set parity, duplicate-ID/project checks, and identical malformed-input expectations.

**Ref:** `tests/Scrinia.Tests/PublishManifestTests.cs`, `publish.projects.tsv`; `docs/superpowers/specs/2026-08-27-imp-03-publish-manifest-parity-design.md` (strict parser and parity decisions).

- [ ] **Step 1: Add exact row assertions** for `scri`, `scri-merge`, and `scri-plugin-embeddings`, including project, condition, profile, output, and RID scope.
- [ ] **Step 2: Add duplicate and missing-project assertions** using the solution-authoritative project list, not filesystem walking.
- [ ] **Step 3: Add malformed fixture tests** for reordered headers, seven fields, trailing tabs, wrong-case enum values, unknown enum values, and empty manifests; assert both readers reject them before publishing.
- [ ] **Step 4: Add a test-local fixture/parser harness** with `tests/Scrinia.Tests/Fixtures/publish-manifest/valid.tsv` and exactly these malformed files: `reordered-header.tsv`, `seven-fields.tsv`, `trailing-tab.tsv`, `wrong-case-enum.tsv`, `unknown-enum.tsv`, and `empty.tsv`. Parse the six tab-separated fields strictly, assign `Path.GetFullPath(fixturePath)` to `fullFixturePath`, then pass that exact variable to the PowerShell adapter (`pwsh -NoProfile -File tests/Scrinia.Tests/Fixtures/publish-manifest/read.ps1 -Manifest $fullFixturePath`) and the Bash adapter (`bash tests/Scrinia.Tests/Fixtures/publish-manifest/read.sh $fullFixturePath`). Each adapter emits normalized project IDs as one JSON-line array and exits nonzero for every malformed file. If a shell is unavailable, record that adapter as host-unavailable and skip only its execution; never silently substitute the other shell.
- [ ] **Step 5: Wire executable workflow-fixture mutation into the parser test.** Copy `.github/workflows/release.yml` to a unique directory below `SCRINIA_RELEASE_TEMP_ROOT`, mutate one publish-project token in the copy, pass the copied path to the same workflow parser used for the real file, and assert the mismatch fails before any publish/archive command is reached. The deliberate outside-manifest set must be empty and justified; the repository fixture itself remains unchanged.
- [ ] **Step 6: Run the focused suite.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PublishManifestTests"`

Expected: PASS for the current manifest/scripts/workflow; malformed fixture cases fail only when the corresponding reader accepts an invalid file.

- [ ] **Step 7: Commit parity tests and fixture scripts.**

```bash
git add tests/Scrinia.Tests/PublishManifestTests.cs tests/Scrinia.Tests/Fixtures/publish-manifest
git commit -m "test: enforce publish manifest parity"
```

### Task 2: Align the release workflow with the manifest

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `tests/Scrinia.Tests/PublishManifestTests.cs`

**Interfaces:**
- Consumes: six-column manifest rows and existing publish-script arguments.
- Produces: a workflow validation step that mechanically validates the release project selection before archive creation; no third hand-maintained project set.

- [ ] **Step 1: Add a separate prerequisite job named `validate-publish-manifest`** that checks out the repository, installs the pinned .NET SDK used by the workflow, runs `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PublishManifestTests"`, and has every publish/archive job depend on it through `needs: validate-publish-manifest`. Keep the existing explicit publish commands because the test mechanically proves their project set against `publish.projects.tsv`.
- [ ] **Step 2: Preserve matrix RIDs `win-x64`, `linux-x64`, and `osx-arm64`, host-only `scri-merge`, optional embeddings, and existing archive names.
- [ ] **Step 3: Add a workflow test assertion that the executable temp-copy mismatch fixture fails inside `validate-publish-manifest`; assert that the job has no publish/archive step and that every dependent job is blocked before upload when the prerequisite exits nonzero.**
- [ ] **Step 4: Run YAML/static parity checks.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PublishManifestTests"`

Expected: PASS; removing or adding a workflow project makes the parity test fail before release artifacts are accepted.

- [ ] **Step 5: Commit workflow parity.**

```bash
git add .github/workflows/release.yml tests/Scrinia.Tests/PublishManifestTests.cs
git commit -m "ci: gate release publishing on manifest parity"
```

### Task 3: Verify publish archives and plugin/Vulkan layout

**Files:**
- Create: `tests/Scrinia.Tests/PublishArchiveShapeTests.cs`
- Read: `publish.ps1`, `publish.sh`, `src/Scrinia.Plugin.Embeddings.Cli`, `src/Scrinia.Plugin.Embeddings`

**Interfaces:**
- Consumes: actual publish output directories and the existing plugin discovery/model-directory contract.
- Produces: archive inspection assertions for host CLI/merge outputs, nested `plugins/scri-plugin-embeddings/` executable/model placement, expected omissions, Windows/Linux native replacement, and macOS CPU fallback.

**Ref:** `publish.ps1`, `publish.sh`, `src/Scrinia.Plugin.Embeddings.Cli`, `src/Scrinia.Plugin.Embeddings`, and `docs/superpowers/specs/2026-08-27-imp-03-publish-manifest-parity-design.md` (archive/plugin/native layout).

- [ ] **Step 1: Add archive-inspection helpers** that accept an output directory and RID, enumerate relative paths, and assert exact executable placement without executing an unsupported foreign binary.
- [ ] **Step 2: Add host publish command tests** using `pwsh -NoProfile -File .\publish.ps1 -OutputDir "$env:TEMP\scrinia-release-verification\0.6.0\win-x64\host" -Platform win-x64` on Windows or `./publish.sh "${TMPDIR:-/tmp}/scrinia-release-verification/0.6.0/linux-x64/host" --platform=linux-x64` on Unix; assert `scri` and host `scri-merge` exist. Run the embeddings variant with `-WithEmbeddings` or `--with-embeddings` and the corresponding `embeddings` output directory.
- [ ] **Step 3: Add embeddings publish tests** with the corresponding switch and assert the plugin executable is nested under `plugins/scri-plugin-embeddings/` with its model directory and no stray root plugin output.
- [ ] **Step 4: Add native-backend checks** against the exact local NuGet paths/version `0.25.0`; assert Windows/Linux replacement when natives exist and explicit CPU-fallback evidence when absent/macOS.
- [ ] **Step 5: List every produced archive before inspection** with `tar.exe -tf $archivePath` on Windows PowerShell and `tar -tf "$archivePath"` on Unix, where `archivePath` is the absolute archive found below `SCRINIA_RELEASE_TEMP_ROOT`; record the exact command, RID, output path, and limitation in test/release evidence. Do not execute a foreign NativeAOT binary; mark that host/archive case `host-restricted` and perform archive-only inspection.

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PublishArchiveShapeTests"`

Expected: PASS for executed host/archive inspections; foreign NativeAOT targets are marked host-restricted rather than claimed as native runtime passes.

Use disposable outputs below the single `SCRINIA_RELEASE_TEMP_ROOT`: `win-x64/host/`, `win-x64/embeddings/`, `linux-x64/host/`, `linux-x64/embeddings/`, `osx-arm64/host/`, and `osx-arm64/embeddings/`. Create the selected directory before each invocation, pass its absolute path to the script, remove it in a `finally`/trap cleanup, and retain only recorded archive evidence outside Git. If the host cannot execute the selected script or target, record `host-unavailable` with the exact command and path, run archive-only inspection when an archive exists, and do not report a pass.

- [ ] **Step 6: Commit archive verification.**

```bash
git add tests/Scrinia.Tests/PublishArchiveShapeTests.cs
git commit -m "test: verify publish archive layouts"
```

## Self-review

- Spec coverage: exact manifest, strict parser parity, workflow parity, all three RIDs, host-only merge, nested plugin/model layout, Vulkan replacement/fallback, and honest limitations are covered.
- Placeholder scan: no unbounded “verify appropriately” language; commands, paths, and expected results are stated.
- Type consistency: tests reuse `ManifestRow` and existing script/workflow names; archive helpers take `(string outputDirectory, string rid, bool withEmbeddings)`.
