# Task 1.3 — Task interface and dependency ledger report

Date: 2026-09-09  
Status: **Complete for documentation/verification mapping; Phase A gate blocked**  
Production source/tests/docs changed: **none**  
Ledger: `docs/superpowers/ledgers/2026-09-09-task-1.3.md`

## Work performed

- Read remediation plan section `## 1.3 Task Interface and Dependency Rules` directly because the
  helper could not extract a brief; no standalone `task-1.3-brief.md` is present.
- Inspected current ownership seams for `AppendAsync`/`AppendStoreException`, MCP recovery and
  response builders, shared envelope rendering, package metadata, model relocation, and test-host
  infrastructure.
- Mapped every plan task from 1.1 through 6.3 to owning files, consumed and produced interfaces,
  dependency order, and focused/full-suite verification commands.
- Corrected the dependency map so 4.1, 4.2, and 4.3 are parallel after 1.2; no 4.2-to-4.1 edge is
  present, and the graph now shows each branching directly from 1.2 with no 3.2 prerequisite.
  Downstream edges now reflect only consumed interfaces.
- Retained all 28 failure rows, the ten model-relocation stability checks, and the aggregate
  test-host/MSBuild hang gate. No failure group was excluded or reclassified away.

## Exact revision evidence

```text
baseline 90827cd4c65d1bb803a0d6d56a5208956ac39d4a
current  8d18c8cf5bf915573de452ae19f0502afae876bd
```

The current checkout also contains uncommitted Phase A changes; the ledger records the commit anchor
separately from that working-tree state.

## Verification

This task changes planning artifacts only, so production build/test execution is not a correctness
signal for the mapping itself. The exact required build, project-suite, serialized diagnostic, normal
full-suite, and final acceptance commands are recorded in the ledger. The final acceptance remains
blocked until `dotnet test Scrinia.sln` terminates with zero failures and no hang.

## Task 1.3 evidence record

| Required field | Evidence |
|---|---|
| Exact failing tests / current pass state | No production/test failure is applicable to this documentation-only task. The mapping artifact is readable and the required plan/ledger/report paths exist. Phase A remains blocked: Task 1.1 records 11 reproduced rows and 17 isolated passes pending group/full-suite confirmation; all 28 rows remain mandatory. |
| Root-cause evidence sources | `docs/superpowers/plans/2026-09-09-phase-a-completion.md:155-157`; current source inspection of append contracts, MCP recovery/response builders, shared envelope rendering, package metadata, model relocation, and test-host harness; Task 1.1 and 1.2 reports. |
| Changed-file list | `docs/superpowers/plans/2026-09-09-phase-a-completion.md`; `docs/superpowers/ledgers/2026-09-09-task-1.3.md`; `docs/superpowers/phase-a-completion-task-1.3-report.md`. No production source, tests, or behavior docs changed. |
| Red output | **Pending / not applicable:** no red test was authored for a documentation mapping task. |
| Green output | Path-scoped `git diff --check` on the three artifacts: **PASS** (no output). |
| Mutation/falsification | **Pending / not applicable:** no implementation behavior was mutated; no assertion was weakened. |
| Reviewer result | **Pending independent reviewer.** |
| Remaining failure count | 28 mandatory ledger rows remain (11 reproduced, 17 isolated-pass transitions), plus 10 retained model-relocation checks and the aggregate hang gate. |

## Path-scoped source/test safety evidence

The Task 1.3 change set is restricted to the plan, ledger, and report paths above. The path-scoped
status/diff checks are recorded here because the broader checkout is dirty from earlier Phase A work:

```text
git status --short --untracked-files=all --ignored -- \
  docs/superpowers/plans/2026-09-09-phase-a-completion.md \
  docs/superpowers/ledgers/2026-09-09-task-1.3.md \
  docs/superpowers/phase-a-completion-task-1.3-report.md \
  src tests docs -- ':!docs/superpowers/**'
git diff --name-only -- src tests docs -- ':!docs/superpowers/**'
git diff --check -- \
  docs/superpowers/plans/2026-09-09-phase-a-completion.md \
  docs/superpowers/ledgers/2026-09-09-task-1.3.md \
  docs/superpowers/phase-a-completion-task-1.3-report.md
```

Observed result: the source/test diff listed only the pre-existing Phase A paths (`src/...` and
`tests/...`), and no Task 1.3 artifact appeared in that excluded scope. The status output marked the
three planning artifacts as ignored (`!!`) under the repository's existing `docs/superpowers/`
ignore rule. `git diff --check` reported no whitespace errors. This is path-scoped evidence that
Task 1.3 introduced no production source, test, or behavior-documentation change; the broader
checkout remains dirty and those existing paths are not attributed to this task.

## Concerns

- The standalone brief extraction failure is recorded as an artifact-format/tooling issue, not as a
  missing requirement.
- Task 1.1’s 17 isolated passes are transitions, not removals from the mandatory ledger.
- Package/global/model verification must inspect actual artifacts and isolated homes; source-only
  inspection is insufficient.
- Historical planning/review files remain byte-identical requirements, and the intentional merge
  retirement must not be reversed.
