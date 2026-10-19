# LongMemEval V2 grouped final-review fix report

Date: 2026-10-19
Base: a048cb153a4f7fe505e09c0338fecb512e97fbc4
Worktree: C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia
Status: All six Important findings implemented and offline verification passed. Parent-scoped re-review remains pending.

## Findings addressed

### I1 — recall-compatible complete source storage

Added V2-only StorageChunks policy v1: at most 7,900 UTF-16 code units per stored element, with surrogate-pair-safe deterministic boundaries. The worst public recall prefix, including two Int32.MaxValue indices, occupies 29 code units, leaving room within the 7,992-unit content cap. The host splits the complete serialized SourceMemory and complete original-trajectory JSON, preserving exact concatenated content, escaping, metadata and source IDs; token fragment boundaries are unchanged.

SnapshotValidator requires the exact deterministic chunk count and content. Source materialization reads and checks every stored chunk, verifies its complete public recall projection, reconstructs the record, and verifies original image bytes. A memory-reference selection independently recalls each selected chunk. Truncated/fabricated evidence still fails. Existing public MCP projection behavior (CRLF normalization and trailing-whitespace trim) is checked exactly; original artifact content is retained for the reader.

Derived notes use an explicit contract instead of argument mutation: every content-array element must be nonblank and <=7,900 UTF-16 code units. The pre-dispatch guard and snapshot validator enforce it. Raw model arguments remain untouched and logged. Oversized derived elements never dispatch or publish a snapshot. Policy fields are included in method identity v2.

Actual MCP regression uses the exact 10,200-character repeated source from the review, plus a 10,000-character URL, large action/thought fields with Unicode, quotes, backslashes and CRLF, complete original JSON metadata, and two derived-note chunks. It verifies exact reconstruction and screenshot bytes. Separate boundary cases exercise a surrogate pair before/at/after the 7,900-unit cut and worst-case recall prefix.

### I2 — configured identity from every episode's first response

Both V2 RuntimeAgentEpisode constructors now pass expectedModelId from the resolved configured controller model. No shared-runtime/V1 implementation changed. Missing/substituted first responses, switches between learning batches and separate queries fail before directed tool dispatch. Raw model-response evidence is retained. Valid tool-call response regressions assert MODEL_DRIFT, no stored note and no tool-intent event.

Existing synthetic response helpers now report the configured synthetic model so the tests exercise their intended recovery/guard behavior under the enforced identity contract.

### I3 — serialized JSONL event writes and disposal

LearningEvents uses one asynchronous semaphore for complete line write/flush and disposal. Cancellation is honored while queued and before accepting a line; once accepted, the complete line flushes before cancellation propagates, preventing cancellation from leaving partial JSON. Disposal coordinates through the same gate; queued/later writes either finish or explicitly fail as disposed/cancelled. The managed semaphore remains available for pending callers to observe disposal safely.

Regression concurrently writes 80 large model/stderr events and parses every complete JSONL line. A second regression covers pending writes, queued cancellation, disposal and post-disposal refusal. The original stream-in-use/disposed-handle failures reproduced before the fix.

### I4 — explicit ordered history binding for saved-memory reuse

Generation metadata v2 contains an ordered history array of {id, sourceHash}; dictionary ordering is not used. Validation binds this array to the ledger and final receipt. The logical source hash already includes complete normalized source/metadata and screenshot content hashes while excluding relocated resolved image paths.

The launcher derives expected_history from the actual materialized ordered haystack and source trajectories, using unchanged upstream loading/normalization. It passes only the ordered source identities through the Python memory configuration. Load compares them before worker validation or query/provider calls. Direct load requires an independently supplied requested configuration with expected_history; a saved configuration alone cannot provide that expected binding. Loaded queries recheck the binding. Historical metadata lacking explicit ordered-history evidence is refused clearly.

Tests cover correct relocated source/runtime/bundle histories, reversed order, missing/extra/repeated IDs, same ID with changed text, changed screenshot bytes, direct unbound load, loaded-query binding changes, and sorted-ledger versus insertion-order differences. No gold answers, question IDs/categories or expected-history metadata were added to the worker protocol.

### I5 — retire only positively proven committed intent

Insertion and saving reconcile markers under the snapshot lock. Retirement requires the expected marker filename and exact trajectory ID, source hash, generation, committed operation ID, current verified pointer/fingerprint and matching final ledger receipt. Unproven/conflicting evidence remains blocking; no learn dispatch is replayed.

A clean-store fault immediately after current.json publication leaves an intent. Reopening and identical insertion retires it without any additional worker call, then save/load succeeds. A forged operation ID remains and blocks idempotent recovery; existing truly uncertain-write and save-blocking regressions continue passing.

### I6 — prompt/parser/guard contract alignment

Both /derived and /source memory selections are documented as the same JSON object shape. Source stateIndex must be read from and match the source record; zero is explicitly illustrative. Learning documents ASCII letters/digits/hyphens, nonempty string-array source attributes, explicit bounded content elements and deterministic host source subdivision. The documented remember example and memory selections pass the strict guard/parser tests.

Parent-dispatched UX review: docs/reviews/ux-polish/2026-10-19-longmemeval-v2-final-contracts.md. All three earlier prompt findings resolved, no new findings. Report preserved verbatim.

## Contract and scope discipline

- Worker request/response DTOs and public product APIs are unchanged. No source-generated JSON registration or worker protocol schema changes were needed; existing shared Python/.NET protocol fixture tests passed in the complete suites.
- Minor M1 remains deferred: owned-path schema duplication was not touched. Optional M2 style/help work was not expanded.
- Method identity is v2 with explicit storagePolicy/historyPolicy; old policy/history-less bundles are not migrated silently.
- Unrelated dirty product/V1 changes were preserved. No reset/clean, blanket stage, upstream edits, public API changes, real benchmark examples, live calls, V1 resume, real scored run, upload/submission/push/merge.
- Shared runtime was not changed, so the parent-requested conditional extra offline V1 run was unnecessary. The full solution build still covered its compilation.

## TDD and focused checks

All shell entries used rtk. Python entries used artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8. Worktree writes/build/test execution used the required scoped escalation.

1. Initial worker RED:
   rtk proxy dotnet test tests/Scrinia.LongMemEvalV2.Worker.Tests/Scrinia.LongMemEvalV2.Worker.Tests.csproj --filter FullyQualifiedName~FinalReviewRegressionTests --logger "trx;LogFileName=final-red-worker.trx" --results-directory artifacts/longmemeval-v2/validation/final-fix
   Exit1: 7 failed, 2 passed. Reproduced concurrent stream-in-use and disposal races, absent query/batch identity enforcement and missing derived cap. Initial fixture issues were corrected before relying on long-source/first-learning assertions.

2. Corrected worker RED:
   rtk proxy dotnet test tests/Scrinia.LongMemEvalV2.Worker.Tests/Scrinia.LongMemEvalV2.Worker.Tests.csproj --filter "FullyQualifiedName~Long_sources_metadata|FullyQualifiedName~Wrong_first_learning|FullyQualifiedName~Prompt_examples" --logger "trx;LogFileName=final-red-worker-corrected.trx" --results-directory artifacts/longmemeval-v2/validation/final-fix
   Exit1: 4 failures. Missing/wrong learning model returned success and prompt contract text was absent. The long-note fixture initially crossed the unrelated conversation-growth budget; its large derived note was moved to a real guarded public remember call, retaining production caps.

3. Genuine long-source RED:
   rtk proxy dotnet test tests/Scrinia.LongMemEvalV2.Worker.Tests/Scrinia.LongMemEvalV2.Worker.Tests.csproj --filter FullyQualifiedName~Long_sources_metadata --logger "trx;LogFileName=final-red-long-source.trx" --results-directory artifacts/longmemeval-v2/validation/final-fix
   Exit1: EVIDENCE_RECALL_CHANGED_OR_TRUNCATED from actual MCP source materialization.

4. Python RED:
   rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 -m unittest discover -s benchmarks/longmemeval-v2/tests -p test_final_review.py -k HistoryRecoveryTests -v
   Exit1: all5 tests failed for absent explicit history, accepted unbound load, retained committed marker, accepted forged marker, and missing expected-history parameter contract.

5. Focused GREEN:
   Same worker filter after implementation:10/10 passed,22s; strengthened boundary/pre-dispatch cases then16/16 passed,31s (final-green-worker-1.trx and final-green-worker-2.trx).
   Python test_final_review.py: first5/5 passed in1.345s; source/image/permutation/legacy extensions7/7 passed in2.196s.

## Final stable-production verification

Collector:
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 artifacts/longmemeval-v2/validation/final-fix/run_validation.py

It executes the following exact argument arrays and retains raw stdout/stderr in build.log, worker.log, python.log, plus results.json:

- dotnet build Scrinia.sln --no-restore --nologo
  Exit0; 0 warnings,0 errors;9.313s collector wall time.
- dotnet test tests/Scrinia.LongMemEvalV2.Worker.Tests/Scrinia.LongMemEvalV2.Worker.Tests.csproj --no-restore --no-build --nologo --logger "trx;LogFileName=final-worker.trx" --results-directory C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/artifacts/longmemeval-v2/validation/final-fix
  Exit0;53 passed,0 failed/skipped;31.718s collector wall time.
- artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 -m unittest discover -s benchmarks/longmemeval-v2/tests -p test_*.py -v
  Exit0;69 passed;398.953s unittest duration (406.875s collector wall). Includes actual localhost-only upstream lifecycle/frozen-package integration and protocol/schema fixture checks.

After that complete run, self-review corrected only two existing negative test fixtures to supply expected_history. This keeps changed-runtime/missing-asset tests from passing accidentally on the new earlier binding gate. Assertions now require METHOD_IDENTITY_MISMATCH and BUNDLE_FINGERPRINT_MISMATCH respectively. No production source changed:
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 -m unittest discover -s benchmarks/longmemeval-v2/tests -p test_official_harness.py -k relocation -k bundle -v
Exit0;2 passed in0.691s. No redundant complete-suite repeat.

Scoped whitespace check:
rtk git -c core.whitespace=cr-at-eol diff --check -- benchmarks/Scrinia.LongMemEvalV2.Worker benchmarks/longmemeval-v2 tests/Scrinia.LongMemEvalV2.Worker.Tests
Exit0, no findings.

## Fresh freeze

See the appended final freeze result below. It uses the retained actual canonical catalog/guide capture, current final binaries/prompts/source, pinned processor and existing invented source-only data. Old synthetic grades stay attached to their old freeze. The new paired configuration has an empty measurements array and unexecuted run roots; no previous timings are represented as measurements of the changed method.

## Remaining limitations

Offline implementation evidence does not establish real model image/context/thinking behavior, live endpoint/model identity stability, GPT-5.2 access, real scores or organizer acceptance of the multi-file source ZIP. Process crash/cancellation evidence does not establish host/power-loss durability. Existing public product size/resource limits remain enforced. Derived-note over-limit calls fail explicitly by contract.

Independent parent re-review is the remaining implementation approval step.

## Final freeze result

Command: rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 artifacts/longmemeval-v2/validation/final-fix/capture_freeze.py

Exit0: fresh freeze, offline_preflight, and every source ZIP member hash verified. Offline verified; live unverified.

Evidence root: artifacts/longmemeval-v2/validation/final-fix/

- source-freeze/: manifest.json, manifest.sha256, source.zip, resolved_pair.json, web.json, enterprise.json, dependency/runtime/data bindings.
- final-pair.json: fresh unexecuted roots and empty measurements.
- offline-preflight.json, method-identity.json, freeze-summary.json.
- run_validation.py, capture_freeze.py, results.json, build.log, worker.log, python.log and TDD/final TRX files.

Exact bindings:

```json
{
  "manifestSha256": "db3e5fb8f5615afeef1527357bfee11273c9715eebe7da6e427178f65dbfd90b",
  "sourceZipSha256": "cdf44044a18bac1ffbd29b19b518c4215a39fa4a711bd864ae23f4117a5799a8",
  "sourceFiles": 496,
  "methodFingerprint": "275db44f47e95767d05b3385fad4eecd9f7f179d9ced04d4c1829a6e25bbd954",
  "learningInstructionsHash": "5fc5cb7d753469aad312822f4c51fe344e9f83fa9a1642eb0d05ed29fc0ba71c",
  "retrievalInstructionsHash": "b58eec9060ecf652948764345d2e700817147016cd3727727f16f3ba59b3356d",
  "storagePolicy": {
    "version": 1,
    "maxChunkUtf16": 7900,
    "sourceRecords": "deterministic-surrogate-safe",
    "derivedNotes": "guarded-explicit-chunks",
    "publicRecallContentChars": 7992
  },
  "historyPolicy": {
    "version": 1,
    "identity": "ordered-id-logical-source-and-image-hash"
  },
  "offline": "verified",
  "live": "unverified"
}
```

## Scoped commit

5f41ebb25cbd7ec417145e18d99410074aa34ac3 — fix: close V2 final review storage and reuse gaps

21 scoped files;644 insertions,34 deletions. Staged whitespace check passed. The final-contract UX report is included verbatim; the parent's initial integration-review report remains outside this commit. No unrelated staged changes were present.

Post-commit read-only checks: scoped V2 status empty; pinned upstream status empty; no worktree-owned scri.exe or scrinia-lme-v2-worker.exe remained. The first sandboxed CIM check lacked access; the authorized read-only escalated check completed successfully with no matches. No processes were terminated.
