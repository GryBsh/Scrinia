# Confirmed Review Remediation Implementation Plan

> **For agentic workers:** Use test-driven development and independent parallel problem-domain implementation, then a fresh integrated review. Preserve other workers' edits.

**Goal:** Fix every confirmed finding from the October 27 code/design review, beginning with data preservation.

**Architecture:** Keep transaction and archive policy in Core; facades request atomic transformations. Separate acknowledged embedding administration from best-effort event observers. Preserve validation while bounding snapshot retention and candidate selection work.

**Tech Stack:** .NET 10/C#, xUnit, Python unittest, existing filesystem transaction infrastructure.

**Spec:** User-approved October 27 review and instruction to fix all confirmed findings; durable detail in Scrinia memory `/findings/full-code-design-review-2026-10-27`.

## Constraints

- Current checkout at 3b69a57d plus pre-existing changes is the baseline; do not revert or stage unrelated changes.
- No live benchmark/model runs, publication, remote deployment, or push.
- Keep 0.6.0, source-generated JSON, recovery validation, archive retention, and official benchmark methodology intact.
- Use failing regression tests before each fix, followed by focused tests and integrated validation.
- Start with tasks 1-3; independent follow-up work may overlap their final verification. Tests use temporary isolated stores and offline fakes.

## Review focus

- A subsequent public write must preserve the newest backup after a committed cleanup warning.
- Compaction and append must serialize or reject stale writes, including ephemeral memory.
- Malformed/duplicate/nested instruction markers must never consume user-owned text.
- Embedding cancellation, unavailable plugins, and changing dimensions must not become false success.
- Snapshot retirement must preserve readers, uncertain evidence, saved snapshots, and validation failures.

## Tasks and ownership

1. [x] Archive cleanup: `RecoveryEnvelope.cs`, `ArchiveService.cs`, recovery regression tests. Protect the current archive pair and prune only validated stale candidates. Reproduce append and replacement cleanup warnings followed by an unrelated public write; prove live bytes and prior-version archives survive.
2. [x] Atomic compaction: Core store operation and MCP lifecycle facade; compaction regression tests. Transform under the store's workspace/scope ownership, reuse whole-memory transactions, and use CAS for ephemeral data. Deterministically interleave append/compact and prove no successful append is lost. Retain metadata and retention semantics.
3. [x] Managed markers: `ManagedMarkdownBlock.cs` and artifact installer tests. Refuse ambiguous/unbalanced input before writing. Test repeat install and uninstall on missing, nested, duplicate, and reversed markers; preserve valid-block idempotence and outside text.
4. [x] Acknowledged reindex: embedding index administrative contract, `McpPluginHost.cs`, `EmbeddingReindexer.cs`, composition and tests. Return typed outcomes for index commands; never count failures or undispatched work as embedded; preserve ordinary observer best effort and cancellation.
5. [x] Vector freshness: `VectorStore.cs` and tests. Revalidate persistent caches using reliable identity under existing locks or read fresh persistent snapshots; preserve ephemeral cache and concurrent writer correctness. Test warm reader after another writer updates/removes data.
6. [x] Dimension discovery and circuit admission: HTTP embedding providers, `CircuitBreaker.cs`, tests. Unknown configured model dimensions are established from a valid first response and enforced subsequently; one half-open probe is admitted across single/batch paths, releasing permits on all exits. Test concurrent shape discovery, inconsistent/non-finite/empty responses, cancellation and failure.
7. [x] YAML fidelity: `McpResponse.cs` and response tests. Correct CRLF/lone-CR handling without introducing extra breaks; verify actual YAML parsing and preserve trailing content semantics.
8. [x] Compaction API/retrieval consistency: shared operation, tool descriptions/guide/tests. Use the workspace's documented keep-only-N semantics consistently; explicitly describe destructive retention. Remove the false shrink-via-compact advice. Offer a non-destructive bounded read path for oversized chunks and test reassembly.
9. [x] Snapshot growth: `benchmarks/longmemeval-v2/snapshots.py` and offline tests. Bound retained complete generations after durable publication, respecting current readers, pending evidence, save/load and failure recovery; do not weaken fingerprint/identity validation or change the official harness. Test many successive insertions and faults around publication/retirement.
10. [x] Candidate selection: `SearchCandidateSelection.cs`, lexical scoring helper and tests. Tokenize/score once per candidate, retain bounded heap with existing deterministic ties, eligibility before selection, and final ordering. Compare with reference ranking over randomized/tie/filter cases; avoid timing-based tests.
11. [x] Integrated review and verification: focused regressions, full default solution suite, offline Python tests, Release build/contracts. Fresh reviewer checks changed source and tests; repair actionable findings. No live benchmarks or publication.

## Execution record

- Baseline: HEAD `3b69a57d` plus existing uncommitted changes. Unrelated benchmark, release, and memory changes were preserved. No live benchmark/model run, publication, push, or commit was performed.
- Archive recovery: five regressions reproduced backup deletion; 82 focused recovery tests passed after the fix.
- Atomic compaction: persistent and ephemeral interleavings now preserve acknowledged appends. Fresh review also found a first-creation race; eight concurrent writes reproduced loss before the fix, and the new TryAdd retry regression passes.
- Managed markers: 50 unsafe cases reproduced destructive behavior; all 132 Hooks tests passed with ambiguous input refused before mutation.
- Embeddings: acknowledged administrative reindexing, persistent vector freshness, discovered HTTP dimensions, and exclusive half-open probes have regression coverage. Focused suites passed 124 reindex/freshness tests and 86 provider tests.
- YAML and retrieval: exact content fidelity, surrogate-safe bounded recall pages, consistent shared keepRecent behavior, and deterministic bounded candidate selection are covered. Integration exposed the V2 worker's old hand-written YAML parser; it now uses a pinned YamlDotNet representation parser and exact ordinal content comparison. All 23 new parser regressions and the complete 139-test worker suite passed. Official evidence selection and reader payload construction are unchanged.
- Snapshot retention keeps current and predecessor generations, validates retirement tickets, protects active readers, and refuses deletion when publication durability cannot be proven. Fresh review found the missing durability barrier; 18 focused snapshot tests passed after repair, including injected failures. A reviewer independently reran the barrier tests. Tiny lease files and cumulative copying remain; older adapters must be stopped before upgrading. Full platform power-loss certification is not claimed.
- Core default suite: 5,293 passed and 58 expected skips, with one stale guide assertion failing because it trimmed the now-preserved trailing newline. The assertion was corrected; its complete 41-test class passed on rerun. Effective core coverage: 5,294 passing tests, 58 skips.
- Auxiliary projects initially lacked restored test-SDK assets, so a no-restore solution invocation silently omitted them. A complete solution restore/build succeeded, followed by explicit auxiliary execution: embeddings plugin 65 passed, retrieval benchmark 274 passed, agent benchmark 307 passed, and V2 worker 139 passed after the YAML integration repair. Total verified .NET tests across the full and targeted runs: 6,079 passing, 58 skipped.
- Release build passed with CS1591 treated as an error: zero warnings and errors. A default parallel attempt failed without compiler diagnostics; a diagnostic serial retry completed successfully after local compiler-server pipe access failures fell back to compilation.
- Release contracts passed against the rebuilt Release outputs: exact seven skills, canonical 0.6.0 version, CLI/workspace/shared/plugin MCP metadata, and current workflow targets. The XML check remains build-only, not comprehensive API documentation coverage.
- Fresh source reviews found no remaining actionable issues after repairs. The UX review and final documentation addendum both found zero issues; report: `docs/reviews/ux-polish/2026-10-27-confirmed-review-remediation.md`.
- Scoped `git diff --check` passed. Hosted CI and native macOS/Linux jobs were not rerun for these uncommitted changes.

- The first complete Python attempt was interrupted after the frozen-package failure and an asyncio setup hang. A standalone `asyncio.run(asyncio.sleep(0))` reproduced the sandbox hang in Windows socket-pair creation, and passed immediately outside the sandbox. The complete offline suite was rerun outside that restriction, without modifying production code or using live services.

- Final complete offline Python run: 118 tests passed in 513.217 seconds, including real frozen web/enterprise packages with local fake services, official-harness cancellation/save/load, and snapshot retention/durability regressions. Exit code 0. Log: `artifacts/review-remediation-20261027/python-tests-unsandboxed.log`.
- Final Release evidence: `artifacts/review-remediation-20261027/release-build.log` and `artifacts/review-remediation-20261027/release-contracts.log`. Core/auxiliary TRX records are under `artifacts/review-remediation-20261027/`.
