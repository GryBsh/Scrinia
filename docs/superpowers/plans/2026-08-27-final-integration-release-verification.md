# Final Integration and Release Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute the complete 0.6.0 acceptance gate from a clean attributable environment and record actual results, archive paths, RIDs, and limitations before release.

**Architecture:** Verification is layered: inspect the final diff, run solution build/tests and the documented benchmark lane, run focused IMP suites, perform host and supported archive inspections, probe version/help/MCP metadata, then validate living docs and historical immutability. Any failed or unavailable check blocks a release claim rather than being relabeled as pass.

**Refs:** `docs/superpowers/specs/2026-08-27-release-improvement-milestone-design.md`; `publish.projects.tsv`, `publish.ps1`, `publish.sh`, and `.github/workflows/release.yml`.
ref: `docs/superpowers/specs/2026-08-27-release-improvement-milestone-design.md`

**Tech Stack:** .NET 10 SDK, xUnit, Bash/PowerShell, Git, publish scripts, executable probe RIDs `win-x64`, `linux-x64`, and `osx-arm64`; all other RIDs are archive-inspection-only.

## Global Constraints

- The milestone is accepted only when every required check passes or has an explicitly recorded unavailable/host-restricted result; unavailable checks are never called passes.
- If a platform cannot execute NativeAOT or Vulkan natively, report archive-only verification.
- If version output is not exactly `0.6.0`, the release gate fails.
- The final diff must prove only approved implementation/docs/test files changed.

## Dependencies / Order

- Depends on IMP-04, IMP-01, IMP-02, IMP-03, the `0.6.0` version/changelog plan, and the living-documentation plan completing first.
- The exact seven plan artifacts in scope are the dated 2026-08-27 IMP-01, IMP-02, IMP-03, IMP-04, version/changelog, living-documentation, and final-integration plans.
- The release checklist and PR record belong in the allowed in-repository report artifact `docs/superpowers/reports/2026-08-27-final-integration-release-verification-report.md`. Verification output, disposable publish/archive directories, and the base marker belong under external `SCRINIA_RELEASE_TEMP_ROOT`; the report is an allowed generated source-control artifact and is not evidence that the source-control tree is clean.
- Preserve pre-existing untracked `docs/server-admin.md` and `docs/web-ui-guide.md`: classify them as excluded artifacts and do not delete, stage, or edit them.
- Use one external `SCRINIA_RELEASE_TEMP_ROOT`: `$env:TEMP\scrinia-release-verification\0.6.0` on Windows and `${TMPDIR:-/tmp}/scrinia-release-verification/0.6.0` on Unix. Its publish directories are `win-x64/host/`, `win-x64/embeddings/`, `linux-x64/host/`, `linux-x64/embeddings/`, `osx-arm64/host/`, and `osx-arm64/embeddings/`; clean disposable outputs after evidence capture.
- Before implementation begins, capture `INTENDED_BASE` exactly once and persist it to `$SCRINIA_RELEASE_TEMP_ROOT/INTENDED_BASE`: PowerShell `$env:INTENDED_BASE = git rev-parse HEAD; New-Item -ItemType Directory -Force $env:SCRINIA_RELEASE_TEMP_ROOT; Set-Content -NoNewline -Path (Join-Path $env:SCRINIA_RELEASE_TEMP_ROOT 'INTENDED_BASE') -Value $env:INTENDED_BASE`; POSIX `INTENDED_BASE=$(git rev-parse HEAD); mkdir -p "$SCRINIA_RELEASE_TEMP_ROOT"; printf '%s' "$INTENDED_BASE" > "$SCRINIA_RELEASE_TEMP_ROOT/INTENDED_BASE"`. Every later scope check reads that same file; it must never be recaptured or compared with itself.
- The allowed changed-file union includes the seven dated plans, their named dated design specs, implementation/tests/living docs explicitly named by those plans, and the generated report. It excludes `.scrinia/**`, the two preserved untracked docs, historical `docs/superpowers/` artifacts, and unrelated files.

### Task 1: Verify clean scope and build

**Files:**
- Read-only: `git diff`, `git status`, all approved plan/spec file lists
- Test: `Scrinia.sln`

**Interfaces:** Consumes completed IMP/docs changes; produces clean-scope evidence and a Release build result.

- [ ] **Step 1: Read the fixed integration base captured before implementation and inspect all scopes.** PowerShell: `$env:INTENDED_BASE = (Get-Content -Raw (Join-Path $env:SCRINIA_RELEASE_TEMP_ROOT 'INTENDED_BASE')).Trim()`. POSIX: `INTENDED_BASE=$(cat "$SCRINIA_RELEASE_TEMP_ROOT/INTENDED_BASE")`. Then run `git status --short`, `git diff --name-only`, `git diff --cached --name-only`, and `git diff --name-only "$INTENDED_BASE" HEAD`. Union working-tree, staged, and fixed-base names before applying the allowlist; do not use `origin/main` or compare the base against itself.
- [ ] **Step 2: Build the solution.** Run `dotnet build Scrinia.sln -c Release`; expect exit code 0 with no warnings treated as errors.
- [ ] **Step 3: Record evidence in `docs/superpowers/reports/2026-08-27-final-integration-release-verification-report.md`; keep raw output and the base marker external.**

### Task 2: Run all required tests

**Files:** Read-only test projects and `.runsettings`/test configuration.

- [ ] **Step 1:** Run `dotnet test Scrinia.sln -c Release --no-build`; record exact totals.
- [ ] **Step 2:** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~EntityFact|FullyQualifiedName~EntityGraphScoreContributor|FullyQualifiedName~AddressSurfaceCoverage|FullyQualifiedName~PublishManifest|FullyQualifiedName~PublishArchiveShape|FullyQualifiedName~VersionMetadata"`; do not describe skipped behavior as passed.
- [ ] **Step 3:** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -c Release --filter "Category=LongRunningBenchmark" --blame-hang --blame-hang-timeout 15m`; record completion, failure, timeout, or unavailable environment honestly.

### Task 3: Verify publish and archives

**Files:**
- Read-only: `publish.projects.tsv`, `publish.ps1`, `publish.sh`, `.github/workflows/release.yml`
- Output outside repository: `SCRINIA_RELEASE_TEMP_ROOT`

- [ ] **Step 1: Publish the selected host RID without embeddings.** On Windows run `pwsh -NoProfile -File .\publish.ps1 -OutputDir "$env:SCRINIA_RELEASE_TEMP_ROOT\$rid\host" -Platform $rid`; on POSIX run `./publish.sh "$SCRINIA_RELEASE_TEMP_ROOT/$rid/host" --platform="$rid"`. Record the exact command and output path.
- [ ] **Step 2: Publish the selected host RID with embeddings** using `-WithEmbeddings` on `publish.ps1` or `--with-embeddings` on `publish.sh`; inspect root CLI/merge files and nested `plugins/scri-plugin-embeddings/` executable/model paths. Do not claim `osx-arm64` can be published from Windows if the script/toolchain forbids it; use a supported macOS arm64 host or record the host-restricted limitation.
- [ ] **Step 3: Acquire and inspect each supported archive independently.** For each of `win-x64`, `linux-x64`, and `osx-arm64`, create `SCRINIA_RELEASE_TEMP_ROOT/archives/<rid>/`, acquire `scri-<rid>.tar.gz` where available, and inspect every existing archive with `tar.exe -tf $archivePath` on Windows or `tar -tf "$archivePath"` on POSIX. On Windows use `$archiveDir = Join-Path $env:SCRINIA_RELEASE_TEMP_ROOT "archives\\$rid"; New-Item -ItemType Directory -Force $archiveDir; gh run download --name "scri-$rid" --dir $archiveDir`; on POSIX use `archiveDir="$SCRINIA_RELEASE_TEMP_ROOT/archives/$rid"; mkdir -p "$archiveDir"; gh run download --name "scri-$rid" --dir "$archiveDir"`. Copy a discovered `scri-<rid>.tar.gz` into that directory before listing it. If a cross-host NativeAOT archive is unavailable, record `host-restricted`/`host-unavailable` for that RID and skip execution; never claim a foreign binary passed. Other RIDs are archive-inspection-only.
- [ ] **Step 4:** Record Vulkan replacement or CPU fallback from the exact script warning/path; never claim Vulkan natives were packaged when absent.
- [ ] **Step 5:** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PublishArchiveShapeTests|FullyQualifiedName~PublishManifestTests"`; record RID, archive path, and limitations.

### Task 4: Probe release metadata and documentation

**Files:** `Directory.Build.props`, `CHANGELOG.md`, `docs/architecture/cli.md`, `docs/troubleshooting.md`, `docs/getting-started.md`, and dated artifacts (read-only).

- [ ] **Step 1:** Publish and run `--version` and `--help` only from the selected host output. Windows uses `win-x64` and `scri.exe`; Linux uses `linux-x64` and `scri`; macOS uses `osx-arm64` only on `arm64`. Use the exact host publish command from Task 3, and expect exactly `0.6.0` plus only supported local commands and no removed remote flags.
- [ ] **Step 2:** Probe MCP metadata with the selected host executable using JSON-RPC `initialize` and `tools/list`. Assert server version `0.6.0`, exactly `{guide, remember, recall, search, list, append, forget, compact, link, restore, reconcile, facts}`, nonempty descriptions with the documented fragments, and no `remote` or `http` claims. If the executable cannot run, record unavailable and keep release blocked.
- [ ] **Step 3:** Validate links and commands in exactly `docs/architecture/cli.md`, `docs/troubleshooting.md`, and `docs/getting-started.md`; search for forbidden removed-surface terms and stale plugin paths.
- [ ] **Step 4:** Run `git diff -- docs/superpowers/specs docs/superpowers/plans docs/reviews docs/planning`; expect no historical file changes.
- [ ] **Step 5:** Record the final acceptance decision in `docs/superpowers/reports/2026-08-27-final-integration-release-verification-report.md` with command, result, RID, archive path, and limitation for every check. A failed or unavailable required check keeps the candidate unreleased.

## Self-review

- Spec coverage: scope, build, default/focused/benchmark tests, host publishing, all three supported RID archives, nested plugin/model/native behavior, version/help/MCP probes, docs, and historical immutability are gated.
- Placeholder scan: no unfinished sentence or unsupported runtime claim remains; unavailable work is explicitly classified.
- Scope review: the generated report is an allowed in-repository artifact, while verification outputs and `INTENDED_BASE` remain external.
