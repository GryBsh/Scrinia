# Parallel Release-Readiness and Improvement Audit Design

**Date:** 2026-08-26  
**Status:** Approved design  
**Mode:** Read-only audit

## Purpose

This audit provides a current, decision-ready view of Scrinia before a release without
turning release review into an unbounded refactoring exercise. It runs two independent
tracks against the same current-main baseline:

1. **Release readiness** identifies defects, regressions, documentation or packaging
   mismatches, and verification gaps that must block a release.
2. **Improvement opportunities** identifies worthwhile follow-up work that is not a
   release blocker, including maintainability, performance, testability, and usability
   improvements.

The tracks are deliberately separate so urgent release risk is neither diluted by a
large improvement backlog nor silently downgraded because a potential fix is broad.

## Scope

The audit examines the repository state reachable from the checked-out `main` commit,
including source, tests, package/publish configuration, living documentation, and
existing audit or finding records relevant to the release decision.

It must establish a reproducible baseline before review begins:

- Record the exact checked-out `main` commit SHA, UTC start time, configured build
  version, and relevant workspace state in the final report.
- Treat that SHA as the sole implementation baseline. A claim supported only by an old
  branch, a historical design document, a prior review, or a memory record is not a
  current finding.
- If `main` advances during the audit, do not merge evidence from the new revision into
  the existing result. Either finish and label the report as applying to its recorded
  SHA, or restart both tracks on the new SHA.
- A dirty worktree is recorded. Uncommitted content is excluded from conclusions unless
  it is already part of the checked-out `main` snapshot; the report must say so.

The audit evaluates only evidence available at that baseline. It does not certify
untested platforms, unreproducible environments, or behavior not represented by the
repository and its documented release process.

## Exclusions and non-interference rules

The audit is observational. Its reviewers and reconciler must not:

- edit source, tests, configuration, existing documentation, or generated artifacts;
- create commits, amend commits, tag a release, push changes, or change branches;
- clean, reset, restore, delete, or otherwise alter workspace files to obtain a clean
  result;
- apply a fix, reformat a file, update a lockfile, regenerate output, or convert a
  finding into a code change while the audit is running.

The only intended artifact is the reconciled dated report. Any command that would
change repository or build output state is outside this audit. If clean-room
verification is required, it must occur in a separately prepared environment and its
provenance must be recorded as evidence; it is not permission to clean up this
workspace.

Historical planning documents under `docs/superpowers/` and prior reports are evidence
of past intent, not statements about current behavior. The audit does not rewrite
them.

## Review method

### Independent tracks and reviewers

Assign different reviewers to the two tracks. They may share the recorded baseline and
the repository, but they must investigate independently and produce separate notes
before reconciliation. Neither reviewer may suppress, promote, or rewrite the other
track's candidates during investigation.

The release-readiness reviewer examines:

- build, test, package, publish, installation, and documented local CLI/MCP surfaces;
- correctness, data integrity, compatibility, security, and release-documentation
  risks that affect a supported release path;
- whether required release checks have current, reproducible evidence.

The improvement-opportunities reviewer examines:

- concrete opportunities to improve maintainability, performance, reliability,
  testability, operability, or user-facing coherence;
- duplication, unclear ownership, avoidable complexity, measured costs, and missing
  non-blocking tests or documentation;
- candidate streams that are bounded enough to plan separately.

An improvement item is not a release blocker merely because it is valuable. A
release-readiness issue is not demoted to an improvement merely because its repair may
be large.

### Current-main evidence standard

Every reported candidate must include all of the following:

1. the baseline SHA;
2. a precise location or release surface (file and symbol, command, package artifact,
   or documented behavior);
3. the observation or reproduction made against that baseline;
4. the consequence if it remains unresolved;
5. the classification and rationale: blocker, improvement, ruled out, or already
   closed.

Assertions such as “pre-existing,” “fixed,” “unreachable,” “covered,” or “release
ready” require direct evidence appropriate to the assertion. For example, a closed
finding requires current-main inspection or a current test result that demonstrates
the stated closure; an old merged commit or a prior report alone is insufficient.
Where a finding describes a defect class, reviewers must enumerate the inspected
instances of that class and explicitly state which are in scope, which are ruled out,
and why. One verified instance is not evidence that the class is exhausted.

Commands and test results are evidence only when their command, result, execution
context, and baseline SHA are captured. A failing or unavailable check is reported as
such; it is never represented as a passing release check.

### Stale and closed-finding control

Prior findings are leads, not open work by default. Before carrying one forward, its
reviewer must compare its claim with the recorded current-main baseline and assign one
of these dispositions:

| Disposition | Meaning | Report treatment |
|---|---|---|
| Confirmed open | Current evidence reproduces or demonstrates the stated risk. | Include with current severity and evidence. |
| Closed | Current evidence demonstrates that the stated risk no longer applies. | List in ruled-out history with closure evidence; do not reopen it. |
| Stale or superseded | The old claim no longer describes the code, surface, or release process. | List in ruled-out history with the reason; do not restate it as current. |
| Inconclusive | The review could not establish current status. | Report the evidence gap explicitly; classify as a blocker only when the missing proof itself prevents an informed release decision. |

Reviewers must not copy old severity, counts, test totals, paths, or recommendations
into the current report without revalidation. The reconciler removes duplicate
restatements of closed or stale items and preserves their disposition history so they
are not rediscovered as new findings later.

## Reconciliation and report format

After both independent notes are complete, a reconciler compares their evidence
against the same baseline and writes one report at:

`docs/reviews/YYYY-MM-DD-parallel-release-improvement-audit.md`

The report is dated by its reconciliation date and has these required sections, in
this order:

1. **Baseline** — main SHA, audit and reconciliation timestamps, version, workspace
   state, reviewers/tracks, and the exact verification environment.
2. **Release decision summary** — `GO`, `NO-GO`, or `CONDITIONAL`; the decision must
   point to the blocker section and any stated conditions.
3. **Blockers** — each current release blocker with evidence, consequence, owner-free
   next action, and the verification required to close it. “None” is explicit when no
   blockers remain.
4. **Improvements** — non-blocking, evidence-backed opportunities, prioritized by
   expected value and confidence. Each says why it is not a release blocker.
5. **Ruled-out and closed history** — every investigated historical or duplicate
   candidate that was closed, stale, superseded, or otherwise rejected, with its
   current-main rationale. This is a history of decisions, not an active backlog.
6. **Recommended next stream** — one bounded improvement stream selected from the
   improvement list, with its problem statement, proposed boundary, expected outcome,
   and reason it outranks the alternatives. If no evidence-backed stream is suitable,
   say so explicitly.
7. **Evidence ledger** — commands, observed outputs or references, relevant locations,
   and limitations. It distinguishes performed checks from checks not run.

The reconciler may merge duplicate candidates only when their current evidence and
conclusion concern the same issue. It must retain conflicting evidence, explain the
resolution, and not elevate an improvement to a blocker—or the reverse—without a
written rationale.

## Decision gate

The release decision is made only after reconciliation, never from an individual
track's notes.

- **GO:** Current-main evidence identifies no unresolved blocker, and the evidence
  ledger contains the required release checks or explicitly accepted, non-blocking
  limitations.
- **NO-GO:** One or more current blockers remain, or a required release assertion
  cannot be supported and that missing evidence prevents a responsible release
  decision.
- **CONDITIONAL:** The release may proceed only after the report's named, bounded
  condition is satisfied and re-evidenced against the same baseline. A conditional
  decision is not a substitute for leaving a blocker unresolved.

No decision authorizes an audit reviewer to implement the recommended stream or fix a
blocker. Implementation, verification after a fix, and release operations are
separate work.

## Success criteria

The audit is complete when all of the following are true:

- Two separate reviewers completed the release-readiness and improvement tracks against
  one recorded current-main SHA.
- Every active conclusion has current, reproducible evidence and a clear classification.
- Historical candidates have an explicit confirmed-open, closed, stale/superseded, or
  inconclusive disposition; no stale or closed finding is presented as active.
- One dated reconciled report contains the baseline, decision, blockers, improvements,
  ruled-out history, recommended next stream, and evidence ledger in the required
  format.
- The report makes one unambiguous `GO`, `NO-GO`, or `CONDITIONAL` release decision.
- The audit made no source, commit, branch, tag, cleanup, reset, or other workspace
  changes beyond writing its report artifact.

## Self-review

Reviewed for placeholders, contradictions, scope, and ambiguity:

- No `TBD`, `TODO`, unspecified reviewer role, or unresolved decision placeholder remains.
- The audit is consistently read-only: its report is the only intended artifact, and
  source, existing documentation, commits, branches, tags, and cleanup are explicitly
  excluded.
- Both tracks use the same fixed current-main baseline while retaining independent
  reviewers and pre-reconciliation notes.
- The evidence standard, historical disposition table, and report structure prevent
  stale or closed findings from being reported as active without current-main proof.
- The decision gate distinguishes missing non-essential context from an evidence gap
  that blocks a responsible release decision, and it does not authorize implementation.
