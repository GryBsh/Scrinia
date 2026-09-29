# Parallel Release-Readiness and Improvement Audit Read-Only Plan

> **For audit workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to perform this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This is an observational audit, not an implementation plan: it creates only the final reconciled report.

**Goal:** Produce one evidence-backed, current-main release decision and a separately prioritized set of non-blocking improvements without modifying the audited repository state.

**Architecture:** Capture one immutable `main` baseline in the audited checkout, then assign independent release-readiness and improvement reviewers to inspect that same revision without exchanging candidates. A reconciler validates each note against the captured baseline, preserves historical dispositions, and writes the sole audit artifact: the dated report under `docs/reviews/`. Any build or test command runs only in an independently prepared clean-room copy at the captured SHA; its provenance and output are evidence, not a reason to alter the audited checkout.

**Tech Stack:** Git, PowerShell 7, .NET 10 SDK, `dotnet test`, Scrinia solution projects, Markdown evidence report.

**Spec:** `docs/superpowers/specs/2026-08-26-parallel-release-improvement-audit-design.md`

## Global Constraints

- Audit the repository state reachable from the checked-out `main` commit only; record its full SHA, UTC start time, `Directory.Build.props` version, and worktree state before review.
- Do not edit source, tests, configuration, existing documentation, generated output, plans, prior reports, or `.scrinia/` records during the audit.
- Do not create, amend, or switch commits or branches; do not tag, push, clean, reset, restore, delete, reformat, regenerate, update a lock file, or apply a fix.
- The only permitted repository write is `docs/reviews/YYYY-MM-DD-parallel-release-improvement-audit.md`, written after reconciliation. Do not commit it.
- Do not run `dotnet build`, `dotnet test`, `dotnet publish`, either publish script, or an executable that creates `.scrinia/` in the audited checkout. Run those only in a separately prepared clean-room directory at the recorded SHA, with output directories outside both repositories.
- If `main` advances, never combine new-SHA observations with the recorded baseline: finish with an explicit “applies to `<SHA>`” label or restart both tracks at the new SHA.
- A dirty worktree is evidence. Exclude uncommitted content from conclusions and state that exclusion in the final report; do not make it clean.
- Historical documents and memories are leads only. Do not repeat historical counts, severity, paths, closure claims, or recommendations without current-main proof.
- Every defect-class conclusion must enumerate all inspected instances and state which are in scope, ruled out, or not inspectable, with the reason. A sample is not a class-wide conclusion.

---

## File Structure

| File or area | Audit responsibility | Permitted action |
|---|---|---|
| `docs/reviews/YYYY-MM-DD-parallel-release-improvement-audit.md` | Sole reconciled report, including ledger and decision. | Create once, after both notes are complete. |
| `Directory.Build.props`, `Scrinia.sln`, `global.json` if present, `NuGet.config` if present | Version, SDK, solution, and restore/build context. | Read only. |
| `src/Scrinia.Core/`, `src/Scrinia.Mcp/`, `src/Scrinia/`, `src/Scrinia.Merge/` | Storage, NMP/2, search, MCP response, CLI/stdio host, and merge-driver release surfaces. | Read only. |
| `src/Scrinia.Plugin.Abstractions/`, `src/Scrinia.Plugin.Embeddings/`, `src/Scrinia.Plugin.Embeddings.Cli/` | Published plugin contract, optional embeddings implementation, and child-process surface. | Read only. |
| `tests/Scrinia.Tests/`, `tests/Scrinia.Merge.Tests/`, `tests/Scrinia.Plugin.Embeddings.Tests/`, `tests/Scrinia.Benchmarks/`, `tests/Scrinia.Retrieval.Benchmarks.Tests/` | Coverage, release-check evidence, and normal-versus-opt-in benchmark lanes. | Read only in audited checkout; execute only in clean room. |
| `publish.ps1`, `publish.sh`, `publish.projects.tsv` | Publish manifest parity, RID selection, trimmed CLI and optional plugin packaging. | Read only; publish only in clean room when the release process requires it. |
| `README.md`, `CHANGELOG.md`, `docs/getting-started.md`, `docs/cli-reference.md`, `docs/multi-user-setup.md`, `docs/security.md`, `docs/troubleshooting.md`, `docs/architecture/` | Living installation, local CLI/MCP, workspace-sharing, security, and architecture claims. | Read only. |
| `.github/` if present, release workflow files if present, package/project metadata | CI/release evidence and package/publish contract. | Read only. |
| `docs/superpowers/`, `docs/reviews/`, `.scrinia/` | Prior plans, findings, reports, and memory records used only for stale/closed-finding disposition. | Read only except the one new report. |

## Evidence Contract

Use one evidence record for every command, inspection, historical candidate, and final conclusion. Record: baseline SHA; UTC time; reviewer/track; exact command or file-and-symbol/location; working directory and clean-room provenance where applicable; observed output or a stable excerpt; conclusion; limitation; and classification.

For an active blocker or improvement, additionally record the observable/reproduction, consequence, confidence, and why the classification is blocker or improvement. A failed, skipped, unavailable, or unrun check remains exactly that in the evidence ledger; it cannot be summarized as passing. Closure, reachability, coverage, compatibility, security, and readiness claims require direct current-main code inspection or a current execution result.

Each prior candidate must receive one disposition: **confirmed open**, **closed**, **stale or superseded**, or **inconclusive**. Inconclusive evidence is a blocker only if the missing proof itself prevents a responsible release decision.

## Independent-Track Protocol

Assign two named reviewers after Task 1. Both receive only the baseline record, this plan, the repository, and the relevant historical leads. They work independently, keep separate notes outside the audited repository, and do not inspect, edit, promote, suppress, or rewrite the other track’s candidates before Task 4. The reconciler may be a third reviewer or one of the two reviewers after both notes are frozen; reconciliation begins only after both notes are complete.

---

### Task 1: Capture the immutable audited-checkout baseline

**Files:**
- Read: `Scrinia.sln`, `Directory.Build.props`, `CHANGELOG.md`, `AGENTS.md`
- Read: repository Git metadata and tracked file state
- Create: no repository file
- Produces: a baseline record held in the reviewers’ external notes and copied verbatim into the final report’s **Baseline** section

- [ ] **Step 1: Confirm the checkout identifies `main` without changing it**

Run from the audited repository root:

```powershell
git branch --show-current
git rev-parse --verify HEAD
git rev-parse --verify main
git merge-base --is-ancestor HEAD main; "head-is-on-main-history=$LASTEXITCODE"
git status --short
git diff --no-ext-diff --name-status
git diff --cached --no-ext-diff --name-status
```

Expected: the branch is `main`, `HEAD` and `main` resolve to the same 40-character SHA, the ancestry check returns `0`, and all status/diff output is captured even when non-empty. If the SHA differs, stop: this plan applies only to a checked-out `main` baseline.

- [ ] **Step 2: Capture version, host, toolchain, and UTC start evidence without generating output**

Run from the audited repository root and save the terminal output in the external baseline note:

```powershell
Get-Date -AsUTC -Format o
Select-String -Path Directory.Build.props -Pattern '<Version>([^<]+)</Version>'
Get-Content Scrinia.sln -TotalCount 45
dotnet --info
git status --porcelain=v1 --untracked-files=all
git diff --no-ext-diff --name-status -- . ':!docs/reviews/2026-08-26-parallel-release-improvement-audit.md'
git diff --cached --no-ext-diff --name-status -- . ':!docs/reviews/2026-08-26-parallel-release-improvement-audit.md'
```

Expected: a UTC ISO-8601 timestamp, the configured version (currently read from `Directory.Build.props`), installed SDK/runtime details, and a precise pre-audit worktree record. Do not interpret untracked or modified content as baseline implementation evidence.

- [ ] **Step 3: Establish clean-room provenance before any executable verification**

The release reviewer must obtain a clean-room directory prepared independently before this audit. It must be a separate clone/copy at the recorded SHA, have no uncommitted changes before commands run, and use output paths outside both the audited checkout and the clean room. Do not prepare it with `git worktree add` from the audited checkout, because that mutates the audited checkout’s Git configuration.

Run in the clean-room root, replacing `D:\scrinia-audit-output` only with an external writable evidence directory:

```powershell
git rev-parse --verify HEAD
dotnet --info
New-Item -ItemType Directory -Force D:\scrinia-audit-output | Out-Null
git status --porcelain=v1 --untracked-files=all
```

Expected: the same baseline SHA, empty initial status output, the recorded .NET environment, and an external output location. Capture the clean-room absolute path, preparer, preparation time, SHA, and command output. If a matching clean room is unavailable, record verification as unavailable rather than running mutating commands in the audited checkout.

- [ ] **Step 4: Freeze the reviewer contract**

Record the named **Release-readiness reviewer**, **Improvement-opportunities reviewer**, and **Reconciler** in the external baseline note. Give each reviewer the full SHA and dirty-worktree exclusion. Require each note to begin with `Baseline SHA: <captured SHA>` and to enumerate historical leads it examined. Do not share candidate lists until both notes are marked complete.

---

### Task 2: Run the independent release-readiness audit

**Depends on:** Task 1

**Files:**
- Read: all areas in the File Structure table, prioritizing `src/Scrinia.Core/FileMemoryStore.cs`, `src/Scrinia.Core/Encoding/`, `src/Scrinia.Core/Formats/`, `src/Scrinia.Core/Bundles/`, `src/Scrinia.Mcp/MemoryTools*.cs`, `src/Scrinia.Mcp/McpResponse.cs`, `src/Scrinia/Commands/`, `src/Scrinia.Merge/`, and their corresponding tests
- Read: `publish.ps1`, `publish.sh`, `publish.projects.tsv`, all `*.csproj`, and release workflow files if present
- Read: `CHANGELOG.md`, `README.md`, `docs/getting-started.md`, `docs/cli-reference.md`, `docs/multi-user-setup.md`, `docs/security.md`, `docs/troubleshooting.md`, `docs/architecture/`
- Create: no repository file
- Produces: an external, frozen release-readiness note containing blockers, ruled-out candidates, test evidence, and evidence gaps

- [ ] **Step 1: Inventory supported release surfaces and their implementation owners**

Read the documented local-only surface against implementation: `scri serve` stdio MCP; CLI commands in `src/Scrinia/Commands/ScriniaCommands.cs`; MCP tools in `src/Scrinia.Mcp/MemoryTools*.cs`; `FileMemoryStore` and NMP/2 persistence in Core; `scri-merge`; and the optional embeddings child process. Inspect every `public` MCP tool method and every public plugin contract, then enumerate every CLI command declared by the command host. Verify that no documentation or package/release path claims the removed HTTP server, REST surface, remote mode, `--remote`, `--api-key`, or `--store` option.

Run from the audited repository root and retain the matching paths/lines (a no-match exit code is evidence, not a failure to conceal):

```powershell
git grep -n -E 'public (async )?(Task|ValueTask|McpResponse|[A-Za-z0-9_<>]+) [A-Za-z0-9_]+\(' -- src/Scrinia.Mcp
git grep -n -E 'class ScriniaCommands|\[Command|public .*\(' -- src/Scrinia/Commands
git grep -n -E 'public (interface|abstract class|class)|public .*\(' -- src/Scrinia.Plugin.Abstractions
git grep -n -E -- '--remote|--api-key|--store|HTTP|REST|remote mode' -- README.md CHANGELOG.md docs src publish.ps1 publish.sh
git grep -n -E 'serve|workspace-root|stdio|MCP' -- README.md docs/getting-started.md docs/cli-reference.md src/Scrinia src/Scrinia.Mcp
```

Record each inspected surface with its document location and implementation location. A mismatch is a blocker only when it affects a supported installation, release, compatibility, security, or data-integrity path; otherwise carry it as an improvement with rationale.

- [ ] **Step 2: Audit completed recovery and durable-write work as current release surfaces**

Inspect the current implementations and tests for: `DurableFile`, durable sidecar/file-entity replacement, bundle file-entity admission, `RestoreJournal`, `FileMemoryStore` scope recovery and lock paths, `BundleFormatService`, `McpResponse`, persistent MCP routes, CLI recovery routes, and the tests named `JournaledRestoreRecoveryTests.cs`, `RecoveryBlockedFailureClassCoverageTests.cs`, `McpRecoveryBlockedRouteTests.cs`, and `CliRecoveryBlockedRouteTests.cs`.

Run this exhaustive route inventory before making any class-wide statement:

```powershell
git grep -n -E 'DurableFile\.|AtomicWrite|Write(AllText|AllBytes)|File\.Move|File\.Replace|File\.Delete' -- src
git grep -n -E 'RestoreJournal|RecoverScope|EnsureScopeRecovered|RECOVERY_BLOCKED|DURABLE_WRITE_FAILED|INVALID_BUNDLE' -- src tests
git grep -n -E 'ImportTopicEntries|ImportTopicsFromZip|bundle import|WriteSidecarMeta|ReadSidecarMeta' -- src tests
git grep -n -E 'JournaledRestoreRecoveryTests|RecoveryBlockedFailureClassCoverageTests|McpRecoveryBlockedRouteTests|CliRecoveryBlockedRouteTests' -- tests
```

For the defect classes “durable replacement,” “file-entity bundle admission,” “journalled topic restore,” and “recovery-blocked error mapping,” enumerate every production write/import/read route located by a repository-wide search. State for each route whether it is covered, out of scope, or an evidence gap; do not infer class closure from one route or one test. Check that the current documented limitations match the changelog: topic restores are per-scope roll-forward, while skill/agent file entities remain independently durable rather than transactional sets.

- [ ] **Step 3: Inspect build, test, package, publish, and install contracts**

Compare `Scrinia.sln`, all source/test project references, version metadata, `publish.projects.tsv`, both publish scripts, package identifiers, runtime identifiers, trimming/AOT settings, and release workflow files if present. Confirm manifest projects are represented by their scripts and test coverage, the `scri-merge` host-RID restriction is documented, and optional embeddings packaging agrees with `-WithEmbeddings`/`--with-embeddings` documentation.

Run the read-only contract inventory from the audited repository root:

```powershell
Get-ChildItem src,tests -Recurse -Filter *.csproj | Select-Object -ExpandProperty FullName
Get-Content publish.projects.tsv
Select-String -Path publish.ps1,publish.sh -Pattern 'publish.projects.tsv|WithEmbeddings|with-embeddings|RuntimeIdentifier|PublishTrimmed|PublishAot|scri-merge'
Get-ChildItem .github -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
git grep -n -E 'LongRunningBenchmark|BenchmarkFact|Category' -- tests
```

In the clean room only, run the required default verification and retain raw console output externally:

```powershell
dotnet test Scrinia.sln --results-directory D:\scrinia-audit-output\default-tests
```

Expected: an exit code of `0`; otherwise record the failing project/test, output location, and baseline SHA as a blocker or evidence gap. Never substitute an old test count for this execution result.

Then inspect benchmark-lane classification in both `tests/Scrinia.Tests` and `tests/Scrinia.Plugin.Embeddings.Tests`. Run the documented opt-in lane only when its prerequisites are available:

```powershell
dotnet test Scrinia.sln --filter "Category=LongRunningBenchmark" --blame-hang --blame-hang-timeout 15m --results-directory D:\scrinia-audit-output\long-running-benchmarks
```

Expected: capture the exit code, discovered/executed/skipped counts, elapsed time, and any unavailable GPU/model/environment prerequisite. The baseline memory identifies a prior benchmark-lane concern; treat that as a lead and establish its current disposition by enumerating all benchmark classes and their category/default-exclusion behavior.

- [ ] **Step 4: Verify publish and installation evidence without touching the audited checkout**

First inspect whether a current release workflow supplies platform-specific artifact evidence. If it does not, use the clean room and external output directory to execute only the host-supported documented publish route:

```powershell
pwsh -File .\publish.ps1 -OutputDir D:\scrinia-audit-output\publish-host -Platform win-x64
```

Expected on a Windows `win-x64` host: exit code `0`, a `scri` publish output, and `scri-merge` output. On a different host, run the corresponding documented host-supported script/platform or record the Windows-specific command as not run; do not claim cross-platform validation.

Inspect the published files and execute non-mutating help/version probes from the external publish output:

```powershell
Get-ChildItem -Recurse D:\scrinia-audit-output\publish-host | Select-Object FullName,Length
& D:\scrinia-audit-output\publish-host\scri.exe --version
& D:\scrinia-audit-output\publish-host\scri.exe --help
```

Expected: a version consistent with `Directory.Build.props` and help that exposes only documented supported commands/options. Record platform, RID, script, output listing, command output, and limitations. Do not run workspace-creating commands against the audited repository; if an isolated temporary workspace is used for an installation probe, record its path and delete nothing from the audited checkout.

- [ ] **Step 5: Produce and freeze the release-readiness note**

For every candidate, write the full evidence contract and one of: blocker, ruled out, closed, stale/superseded, or inconclusive. For a blocker include an owner-free next action and the exact verification needed to close it. State plainly whether required release checks passed, failed, or were not run. Do not issue `GO`, `NO-GO`, or `CONDITIONAL`; the decision belongs to reconciliation.

---

### Task 3: Run the independent improvement-opportunities audit

**Depends on:** Task 1

**Files:**
- Read: architecture and release surfaces in the File Structure table
- Read: `docs/superpowers/`, `docs/reviews/`, and `.scrinia/` historical records as leads
- Create: no repository file
- Produces: an external, frozen improvement note with evidence-backed, bounded candidate streams and historical dispositions

- [ ] **Step 1: Establish the architecture map and inspect ownership boundaries**

Starting from `docs/architecture/overview.md`, inspect Core storage/encoding/search/embeddings boundaries; MCP response/tool boundaries; CLI workspace setup and plugin child-process supervision; merge-driver isolation; and published plugin abstractions. Identify concrete duplication, ambiguous ownership, avoidable coupling, or test seams by citing specific symbols and call chains. Do not call a design preference an improvement without a measurable or observable consequence such as repeated logic, divergent behavior, excessive work, hard-to-test flow, or user-facing inconsistency.

Run these read-only inventories before selecting candidate streams:

```powershell
git grep -n -E 'interface I(MemoryStore|EncodingStrategy|EmbeddingProvider|SearchScoreContributor|MemoryEventSink|MemoryOperationHook)|class FileMemoryStore|class MemoryTools|class ScriniaCommands' -- src
git grep -n -E 'MemoryStoreContext|SearchContributorContext|MemoryEventSinkContext|WithTools<|JsonSerializerContext|JsonSerializable' -- src
git grep -n -E 'TODO|FIXME|HACK|Obsolete|NotSupportedException|throw new NotImplementedException' -- src tests docs
git grep -n -E 'remote|HTTP|REST|web UI|server' -- README.md CHANGELOG.md docs src
```

- [ ] **Step 2: Review non-blocking quality opportunities across the complete release surface**

Inspect maintainability, performance, reliability, testability, operability, and user-facing coherence across:

1. NMP/2 encode/decode, durable formats, migration, `FileMemoryStore`, search ranking/attribute extraction, facts, vectors, and retry/lock behavior.
2. MCP response envelopes and all tool descriptions; CLI commands, structured output, help, exit semantics, workspace discovery, configuration, setup, migration, bundle, reconcile, and plugin discovery.
3. Merge-driver conflict handling and multi-user documentation.
4. Publish scripts/manifest, trim/AOT configuration, plugin ABI, optional embeddings, and benchmark lanes.
5. The living docs listed in the File Structure table, checking terminology against the actual local stdio-only product.

For every defect class identified, enumerate the full searched class before asserting scope. For each useful candidate, state the baseline location, observation, consequence, expected value, confidence, a bounded proposed stream, and why it is not a release blocker. Exclude speculative rewrites and unmeasured performance claims.

- [ ] **Step 3: Revalidate historical leads rather than importing them as findings**

Review historical material relevant to recovery, durable writes, package/publish conformance, public-surface coherence, benchmark lanes, merge-driver behavior, docs drift, and prior audit reports. For each lead, inspect current-main code/tests/docs or current clean-room output and assign confirmed open, closed, stale/superseded, or inconclusive. Preserve the old claim only as ruled-out history where it no longer applies.

Inventory the historical leads without modifying them:

```powershell
Get-ChildItem docs/superpowers/specs,docs/superpowers/plans,docs/reviews -Recurse -File -ErrorAction SilentlyContinue | Select-Object FullName,LastWriteTimeUtc
git log --oneline --decorate -40
git log --all --oneline -- docs/superpowers docs/reviews CHANGELOG.md publish.ps1 publish.sh publish.projects.tsv
```

The known recovery work must be treated as current inspection targets, not as automatic evidence of a defect: durable per-file writes and journalled per-topic restore are separate contracts. The known benchmark-lane review must be checked by enumerating all benchmark suites and category behavior on current main; it is not proof that a current release is blocked.

- [ ] **Step 4: Prioritize and freeze the improvement note**

Rank evidence-backed candidates by expected value and confidence. Each entry must say why it is non-blocking and define a boundary small enough for a later separate plan. Include ruled-out/closed history and evidence gaps. Do not inspect or react to the release reviewer’s note before freezing this note.

---

### Task 4: Reconcile evidence and write the mandatory report

**Depends on:** Tasks 2–3

**Files:**
- Create: `docs/reviews/2026-08-26-parallel-release-improvement-audit.md`
- Read: the two frozen external notes, baseline record, and current audited checkout
- Produces: one dated, decision-ready report and no other repository changes

- [ ] **Step 1: Validate candidate identity and baseline applicability**

Compare every candidate’s SHA with the captured baseline. Reject or mark inconclusive any claim lacking a current location/surface, observation/reproduction, consequence, and classification rationale. Merge duplicates only where the current evidence and conclusion describe the same issue. Preserve conflicting evidence, state the conflict and resolution, and never promote or demote classification without written rationale.

Run in the audited repository root before writing the report:

```powershell
git rev-parse --verify HEAD
git rev-parse --verify main
git status --short
git diff --no-ext-diff --name-status
git diff --cached --no-ext-diff --name-status
```

Expected: the SHA remains the captured baseline. If it changed, either restart both tracks or write no report from mixed evidence. If the worktree changed independently, retain the original dirty-worktree exclusion and describe the later change as a limitation.

- [ ] **Step 2: Write the report in the mandatory order**

Create `docs/reviews/2026-08-26-parallel-release-improvement-audit.md` with exactly these top-level sections, in this order:

```markdown
# Parallel Release-Readiness and Improvement Audit — 2026-08-26

## Baseline
## Release decision summary
## Blockers
## Improvements
## Ruled-out and closed history
## Recommended next stream
## Evidence ledger
```

Populate **Baseline** with main SHA, UTC audit/reconciliation timestamps, configured version, complete workspace-state treatment, named reviewers/tracks, clean-room provenance, and exact toolchain/environment. **Release decision summary** must be exactly `GO`, `NO-GO`, or `CONDITIONAL`, point to blockers/conditions, and state the baseline SHA. **Blockers** must explicitly say `None` when empty; otherwise each has evidence, consequence, owner-free next action, and closure verification. **Improvements** must be prioritized by expected value and confidence and say why each is non-blocking. **Ruled-out and closed history** preserves dispositions and current-main rationale without making it an active backlog. **Recommended next stream** selects exactly one bounded improvement with problem, boundary, expected outcome, and why it outranks alternatives; explicitly state when none qualifies. **Evidence ledger** lists every command with output/reference, location, context, SHA, and whether it was performed, failed, unavailable, or not run.

- [ ] **Step 3: Apply the release decision gate**

Choose `GO` only if current-main evidence has no unresolved blocker and the ledger contains required release checks or explicitly accepted non-blocking limitations. Choose `NO-GO` if a current blocker remains or missing essential proof prevents a responsible decision. Choose `CONDITIONAL` only when a named, bounded condition can be satisfied and re-evidenced at the same SHA; do not use it to leave a blocker unresolved. The report recommends work but does not authorize implementation, post-fix verification, publishing, tagging, or release operations.

---

### Task 5: Perform final freshness, diff, and report-quality verification

**Depends on:** Task 4

**Files:**
- Read: `docs/reviews/2026-08-26-parallel-release-improvement-audit.md`, baseline record, and Git state
- Modify: no file
- Produces: final evidence that the report is fresh and is the only intentional repository change

- [ ] **Step 1: Verify baseline freshness after report production**

Run from the audited repository root:

```powershell
git rev-parse --verify HEAD
git rev-parse --verify main
git status --short
git diff --no-ext-diff -- docs/reviews/2026-08-26-parallel-release-improvement-audit.md
git diff --cached --no-ext-diff --name-status
```

Expected: `HEAD` and `main` still equal the captured SHA; the only audit-created diff is the dated report. If other changes are present, distinguish pre-existing/external changes from audit action using Task 1’s baseline status and report the limitation. Do not clean or revert anything.

- [ ] **Step 2: Check mandatory report structure and terminology**

Run:

```powershell
$report = 'docs/reviews/2026-08-26-parallel-release-improvement-audit.md'
$required = '## Baseline','## Release decision summary','## Blockers','## Improvements','## Ruled-out and closed history','## Recommended next stream','## Evidence ledger'
$headings = Get-Content $report | Where-Object { $_ -match '^## ' }
$required | ForEach-Object { if ($headings -notcontains $_) { throw "Missing required heading: $_" } }
$actualRequiredHeadings = ($headings | Select-Object -First 7) -join "`n"
$expectedRequiredHeadings = $required -join "`n"
if ($actualRequiredHeadings -cne $expectedRequiredHeadings) { throw 'Required report headings are not in the mandated order.' }
Select-String -Path $report -Pattern '\bGO\b|\bNO-GO\b|\bCONDITIONAL\b'
```

Expected: all seven headings appear in the required order and one unambiguous release decision is visible. Manually verify that “MCP”, “CLI”, “stdio”, “local”, “blocker”, “improvement”, and the four historical dispositions are used consistently with the specification and current documentation.

- [ ] **Step 3: Self-review the audit deliverable and leave it uncommitted**

Check the report against every section of the approved design: baseline reproducibility; read-only non-interference; independent tracks; current-main evidence; historical disposition; mandatory order; reconciliation rules; decision gate; and success criteria. Confirm no sentence claims untested platforms or unavailable checks passed, no historical finding is active without current evidence, and every defect-class assertion enumerates inspected instances.

Do not run `git add`, `git commit`, `git tag`, `git push`, cleanup, reset, restore, or any source/config/artifact change. The final handoff states the report path, SHA, decision, clean-room limitations, and that no commit was created.

## Plan Self-Review

- **Spec coverage:** Task 1 covers reproducible current-main baseline, dirty-worktree treatment, and clean-room provenance. Tasks 2 and 3 implement the two independent tracks, current evidence standard, completed recovery inspection, and historical disposition controls. Task 4 implements reconciliation, the mandatory seven-section report, duplicate/conflict handling, and the decision gate. Task 5 provides final freshness/diff verification and the no-change guarantee.
- **Placeholder scan:** The plan supplies concrete paths, commands, reviewers’ deliverables, classifications, expected command outcomes, report headings, and decision criteria. It contains no unresolved work markers or unspecified execution steps.
- **Type and terminology consistency:** The plan consistently uses **baseline SHA**, **release-readiness reviewer**, **improvement-opportunities reviewer**, **reconciler**, **blocker**, **improvement**, and the four historical dispositions defined by the design. It consistently describes Scrinia as a local CLI and stdio MCP server with an optional embeddings child process; it does not introduce a remote or HTTP release surface.
