# UX Polish Review — LongMemEval V2 operator corrections

**Date:** 2026-10-19
**Scope:** README.md changes after ac62dba0 for archive build instructions, source-checkout binding, and readiness revocation; linked ARCHIVE_BUILD.md claims
**Reviewer:** ux-polish-reviewer agent

The revised claims match the inspected code; no blocking issues found, with two optional clarity improvements.

**Must fix**

None.

**Should fix**

None.

**Consider**

1. [README.md:11](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/longmemeval-v2/README.md:11) — Name the restored dependencies explicitly and replace “CLI graph” with the operator’s actions.

   **Before:** “The following checks use a restored full repository checkout. For an extracted source ZIP, follow [the archive restore/build/publish instructions](ARCHIVE_BUILD.md) for the included worker and Scrinia CLI graph.”

   **After:** “The checks below require a full repository checkout with its .NET dependencies already restored. For an extracted source ZIP, follow [the archive instructions](ARCHIVE_BUILD.md) to restore dependencies, build and publish the worker and Scrinia CLI.”

2. [README.md:38](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/longmemeval-v2/README.md:38) — Clarify that readiness revocation affects the report for this freeze.

   **Before:** “Starting a live preflight revokes earlier readiness before checking prerequisites; only a fully successful attempt atomically publishes new readiness.”

   **After:** “Starting a live preflight removes that freeze’s previous readiness report before checking prerequisites; only a fully successful attempt atomically publishes a replacement.”

**Consistency notes**

- The source-checkout binding matches canonical path validation during freezing and frozen-input verification.
- Readiness revocation precedes prerequisite checks, and successful publication uses an atomic file replacement.
- The archive-guide link resolves to an included document; its SDK version matches `global.json`.
- Keep full-checkout checks distinct from archive-specific project builds.
- `SYSTEM_DESCRIPTION.template.md` has no changes relative to `ac62dba0`, so it was excluded from this follow-up.
