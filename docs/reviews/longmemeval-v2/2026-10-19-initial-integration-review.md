# LongMemEval V2 final integration review

Date: 2026-10-19

Reviewed range: `f79eb3907b7f3395105353830453b76bb492f2f0..a048cb15` in `C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia`.

Verdict: **Needs fixes before approval. Six Important findings; no Critical findings.** The offline implementation is substantial, but the long-source materialization failure alone prevents calling it ready for the intended benchmark. Live inference, a scored run, organizer acceptance, and submission remain separate, unverified stages.

## Scope and evidence

I used the supplied `review-f79eb390..a048cb15.diff` in bounded passes, the design and implementation plan, `progress.md` and task reports, current production files, selected tests, and relevant unchanged MCP/upstream dependencies. I did not regenerate the diff, change source/index/HEAD, spawn reviewers, rerun the reported suites, inspect real V2 rows, make live calls, or resume V1. The only review write is this report. The saved baseline patch identifies prior product/V1 changes; those are not attributed to this V2 implementation. The unchanged product recall cap is examined as a dependency of the new adapter, not reported as a product regression.

Accepted reported evidence includes the full Python 57/worker 37/offline V1 281 passing tests, zero-warning solution build, the subsequent narrow freeze/launch checks, Task 6 fix-round checks, and the isolated source-archive restore/build/publish proof. Those tests establish the behaviors they exercise; the integration gaps below are not disproven by those passes.

One additional, strictly read-only diagnostic used the already pinned tokenizer without loading weights or contacting a provider:

```text
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -B -X utf8 -c "from tokenizers import Tokenizer; t=Tokenizer.from_file('artifacts/longmemeval-v2/processor/tokenizer.json'); text=' original source.'*600; print({'text_chars':len(text),'tokens':len(t.encode(text,add_special_tokens=False).ids),'fragment_token_cap':4096,'recall_content_char_cap':7992})"

{'text_chars': 10200, 'tokens': 1800, 'fragment_token_cap': 4096, 'recall_content_char_cap': 7992}
```

## Strengths

- The external adapter leaves the official harness, reader request builder, evaluator, timing boundary and packaging utilities intact. Worker requests exclude gold answers, question IDs/categories and evaluator configuration.
- Immutable source catalogs, content/image hashes and deterministic host seeding give derived notes an auditable source. Retrieval uses fresh conversations, a read-only catalog, strict output parsing, owned copies, before/after fingerprints and actual source materialization.
- Worker ownership is explicit. Windows workers start suspended and enter an owned job before resuming; cancellation signals stdin before bounded cleanup. The .NET MCP session is asynchronously disposed before a learning receipt or retrieval result can be returned.
- Runtime extraction preserves the V1 facade and explicit recovery policies. Provider attempts and response recovery share one counter, each provider attempt consumes the episode budget, and uncertain tool completion aborts rather than redispatching a mutation.
- New protocol and catalog serialization uses source-generated contexts. JSON schemas reject extra fields, duplicate-key rejection exists at the worker boundary, and embedded prompts are bound to runtime hashes.
- Freeze verification now binds the executing source checkout, complete sibling runtime payloads, data, processor, dependencies and outputs. The corrected archive instructions use the included project closure, and the reported isolated rebuild is meaningful evidence. Readiness is revoked before a new live attempt and only atomically republished after success.
- Operator documentation accurately separates wire checks, empirical probes, operator attestations, process-crash guarantees and unresolved live/organizer claims.

## Critical

None identified in this range.

## Important

### I1. Successfully indexed long sources cannot pass the materializer's mandatory recall check

Locations:

- `benchmarks/longmemeval-v2/batching.py:76` — fragments are bounded by tokenizer count alone.
- `benchmarks/Scrinia.LongMemEvalV2.Worker/Learning/SourceSeeder.cs:20` and `:54` — the complete original trajectory and each serialized source record are stored as one content chunk.
- `benchmarks/Scrinia.LongMemEvalV2.Worker/Evidence/EvidenceMaterializer.cs:33`, `:40`, `:63` — every selected chunk must exactly match an untruncated public recall response.
- `benchmarks/Scrinia.LongMemEvalV2.Worker/Learning/LearningInstructions.cs:47` — derived-note content elements also have no recall-size constraint.
- Dependency: `src/Scrinia.Mcp/McpResponse.cs:163`, `:306`; `src/Scrinia.Mcp/MemoryTools.Core.cs:220`.

The default 4,096-token source fragment limit is larger than the product's 7,992-character recall content limit. The read-only tokenizer diagnostic above demonstrates a 10,200-character text that uses only 1,800 tokens. This is accepted as one fragment; JSON escaping, source metadata and the `Chunk n/count` recall prefix increase its stored/returned size further. Source seeding and snapshot validation read the complete artifact and succeed, but later public recall truncates it and `RequireRecallAsync` rejects it. A correct source selection therefore fails the query, including an image selection whose accompanying source record is long. Selecting the same source as a memory reference does not avoid the check.

The scope is larger than fragment text: a large URL/action/thought field can overflow an otherwise short source record, Unicode JSON escaping adds size, `/source/<hash>/trajectory` contains an unbounded original JSON document, and an agent may write an otherwise valid derived-note chunk above the cap. The public recall API only provides a chunk index; it has no within-chunk paging or caller output-budget override. `test_batching.py:18` tests fragment preservation with a 64-token cap, not the actual default-to-materialization boundary.

Fix within V2: define a storage chunk policy compatible with the exact public recall projection, preserving all bytes and source associations. Deterministically split complete serialized source records and original-trajectory metadata into recallable content chunks, and teach snapshot validation/materialization to verify/reconstruct them through public recall. If instead tightening fragment boundaries, account for the whole serialized record and unbounded metadata, not just text tokens. Derived notes need a matching explicit chunk contract and enforcement (or deterministic subdivision before dispatch), rather than accepting chunks that can never be returned. Keep the current rejection of truncated or fabricated evidence; do not fix this by forwarding truncated text or silently trusting an incomplete recall. No product API change is inherently required.

Validation: actual MCP insertion and materialization of the 10,200-character example at production caps, Unicode/escaping and large metadata, the complete original-trajectory record, and multi-chunk derived notes. Assert exact reconstructed evidence and original screenshot bytes. This changes a frozen method policy, so update the method identity/configuration accounting and create a new freeze.

### I2. V2 does not enforce the configured controller identity on the first response of an episode

Locations: `benchmarks/Scrinia.LongMemEvalV2.Worker/Learning/TrajectoryLearner.cs:60`; `benchmarks/Scrinia.LongMemEvalV2.Worker/Retrieval/EvidenceRetriever.cs:32`; enforcement seam `benchmarks/Scrinia.Agent.Runtime/ObservedChatClient.cs:118`.

Both V2 callers construct `RuntimeAgentEpisode` without its existing `expectedModelId` argument. With that argument null, the observer only compares later responses against the first response of that same episode. A server returning a substituted model consistently, returning no model ID, or switching models between learning batches/queries is accepted. Learning creates a new episode for every batch, so no identity observation survives that boundary. A successful earlier live probe and unchanged local attestation file do not prevent endpoint routing changes during the actual run, and frozen verification checks local inputs rather than response identities.

Fix: pass the frozen expected reported controller identity into every V2 episode. The current preflight already requires the reported ID to equal the configured model, so passing the resolved configured model is consistent with that contract. Preserve V1's existing behavior. If a provider needs a separate advertised ID, make that an explicit frozen, preflight-verified binding rather than silently accepting the first response.

Validation: wrong/missing model on the first learning or retrieval response must fail before any model-directed tool dispatch; also cover a model switch between learning batches and separate queries. Keep raw response evidence, but never publish the affected snapshot or successful retrieval.

### I3. The V2 event sink is unsafe for the runtime's concurrent writers

Locations: `benchmarks/Scrinia.LongMemEvalV2.Worker/Learning/TrajectoryLearner.cs:166-175`; concurrent caller `benchmarks/Scrinia.Agent.Runtime/ScriniaMcpSession.cs:151-161`.

`LearningEvents` shares one `StreamWriter` and performs asynchronous `WriteLineAsync`/`FlushAsync` without serialization. The session's diagnostics pump writes stderr events independently of the model/tool execution task, and it starts before connection/catalog events are written. A warning or diagnostic arriving while a large request/tool event is being flushed can overlap operations on that writer. `StreamWriter` does not support concurrent asynchronous writes: this can throw an in-use exception or corrupt/interleave the required JSONL evidence. A benign diagnostic can consequently cancel a valid episode through the diagnostics failure token, or leave unusable evidence. This sink is used by both learning and retrieval. The V1 journal already serializes its analogous writes with a semaphore.

Fix: serialize each complete line write plus flush with an async gate, and coordinate disposal with the same lifetime. Avoid holding an ordinary lock across awaits. Preserve cancellation/failure propagation and ensure all queued writes are completed or explicitly failed before disposal.

Validation: concurrent large model events and repeated MCP stderr events against the actual sink; every completed event must parse as one JSONL object, with no lost/merged records or spurious stream-in-use exceptions. Include cancellation/disposal while a write is pending.

### I4. Saved-memory reuse does not verify that its history matches the requested ordered haystack

Locations: `benchmarks/longmemeval-v2/run_scrinia.py:123-145`; `benchmarks/longmemeval-v2/portability.py:64-76`, `:133-170`; `benchmarks/longmemeval-v2/snapshots.py:141`.

The launcher materializes the current haystack but forwards `--load-memory-dir` without comparing it to the saved memory's inserted history. Reconciliation checks method settings and runtime assets; `method_identity` intentionally contains no trajectory content or ordered history. Bundle validation proves the saved snapshot is internally intact, not that it belongs to the requested history. The pinned upstream harness (`evaluation/harness.py:1180-1190`) only checks that the current questions share an ordered haystack, then loads memory and skips insertion; it does not validate the bundle's history for this adapter.

A bundle built from A/B can therefore be loaded for B/A, A alone, or a different trajectory version under the same runtime configuration. This violates the promised identical ordered-history reuse and can expose evidence not in the question's allowed history. The initial frozen-pair path rejects `load_memory_dir`, so this is not a bypass of the fresh scored-run gate; it is a defect in the explicitly delivered save/load and non-frozen harness lifecycle. The current relocation tests validate method/asset changes and correct round trips, but not this mismatch.

Fix: persist and verify an explicit ordered insertion identity (trajectory IDs plus logical content/image hashes), then compare the saved history against the actual materialized history before handing a loaded memory to the unchanged harness. Do this in the launcher/adapter boundary using source histories only; do not send question files or gold answers to the worker. A relocation with identical content must remain valid. Do not rely on dict ordering: `atomic_json` sorts ledger keys, and no current load path performs a history comparison.

Validation: correct relocated history succeeds; permutation, missing/extra trajectory, and same ID with changed text/image fail before any query/provider request. Cover a direct load/reuse path sufficiently that it cannot claim the identical-history guarantee without an expected history binding.

### I5. A proven committed insertion remains permanently unsavable after lost acknowledgment

Locations: `benchmarks/longmemeval-v2/snapshots.py:94-100`, `:147-150`; `benchmarks/longmemeval-v2/portability.py:94-99`.

After `current.json` has been atomically published but before the uncertainty marker is unlinked, a process crash or acknowledgment-loss fault leaves a fully verified committed generation and its intent marker. On restart, identical insertion correctly recognizes the receipt and returns at line 97, but does so before locating/reconciling that marker. Every subsequent save rejects *any* uncertainty marker before examining the committed receipt. Repeating the successful idempotent insertion never repairs this, so a trajectory the API reports as successfully recovered cannot be saved through the promised lifecycle.

This is distinct from the justified policy of never replaying an uncertain mutation. The marker's operation ID/source hash/generation can be compared against the validated committed receipt and pointer. The existing acknowledgment-loss test checks only the idempotent return, while the save test checks a genuinely unproven marker; their combination is missing.

Fix: under the snapshot lock, reconcile only markers positively proven committed by the verified current generation/ledger and matching operation/source/generation evidence. Either retire those markers safely or make save distinguish proven committed evidence from unresolved intent. Retain and block on unproven/conflicting markers; never replay a tool call or delete evidence by assumption.

Validation: inject a failure immediately after publication in a clean store, reopen it, perform the idempotent insertion, then successfully save/load that generation without another learn dispatch. Separately prove an actual uncertain write and a mismatched/forged marker still block.

### I6. Agent instructions omit hard protocol rules that terminate otherwise reasonable calls

Locations: `benchmarks/Scrinia.LongMemEvalV2.Worker/Retrieval/retrieval-instructions.txt:3-4`; `benchmarks/Scrinia.LongMemEvalV2.Worker/Learning/learning-instructions.txt:1`; parsers/guards `Evidence/EvidenceSelection.cs:15-23`, `Learning/LearningInstructions.cs:36-51`.

The prior prompt UX findings remain unfixed. Retrieval explicitly describes a memory reference as a JSON object “or an exact /source/... path,” but `EvidenceSelection.Parse` accepts only an object containing `kind`, `path` and `chunks`. A controller following the permitted bare-path reading gets an integration failure. Learning advertises `<note-name>` without its ASCII-alphanumeric/hyphen restriction; a normal name such as `account_settings` passes the public tool's general path schema but the hidden V2 guard aborts the entire learning episode. The prompt also does not spell out the required nonempty source-ID array, and the source-reference example uses state zero without saying it must match that source record.

These are behavioral contract mismatches, not optional prose styling. Guard/parser failures are not a gentle retry with clarification; they can abort a trajectory or a scored query.

Fix: apply the narrow contract corrections already proposed in `docs/reviews/ux-polish/2026-10-19-longmemeval-v2-prompts.md`: both path namespaces use the same memory-reference object; stateIndex must be read from and match the source; note names use ASCII letters/digits/hyphens; `attributes.source` is a nonempty string array. Incorporate the storage-chunk guidance from I1 if needed. Recompute prompt/method hashes and freeze anew. The parser and guards should remain strict.

Validation: example memory/source selections and the documented remember call must pass the existing parser/guards. This needs a small contract regression, not live tuning against benchmark answers.

## Minor

### M1. Owned-path schema repetition remains a maintenance risk

Location: `benchmarks/longmemeval-v2/protocol.schema.json:236`, `:343`, `:427`.

The same owned-path object is repeated in each operation rather than referenced from one definition. The current copies are consistent, so this is not an observed protocol failure. Consolidate them under `$defs` and retain the Python/.NET fixture checks when next editing the schema. This explicitly carries forward the Task 2 deferred minor.

### M2. Optional operator wording/help remains optional

Locations: `benchmarks/longmemeval-v2/preflight.py:69-70`; `benchmarks/longmemeval-v2/README.md:11`, `:38`.

The original operator review's two factual corrections and concurrency clarification are present. Its remaining suggestion to describe `--offline`/`--live` in CLI help, and the follow-up review's clearer “restore/build/publish” wording and freeze-specific readiness wording, are useful but nonblocking. The current README is accurate. Keep these as optional polish, not additional approval gates.

## Deferred-finding disposition

| Earlier item | Disposition |
| --- | --- |
| Task 2 duplicated owned-path schemas | Open Minor M1; no current divergence found. |
| Task 2 missing-PyTorch import warning | Resolved by the recorded pinned processor environment; no new finding. |
| Task 3 process-crash versus host/power-loss durability | The README and system description now explicitly limit the claim. No requirement for a new power-loss guarantee is inferred. I5 is a separate process-recovery lifecycle defect within the claimed model. |
| Prompt UX bare source path | Important I6; still a runtime contract mismatch. |
| Prompt UX note-name/source-array guidance | Important I6; hidden fatal write constraints must be documented. |
| Prompt UX source/stateIndex guidance | Included in I6's small contract correction; avoids an ambiguous example. |
| Original operator UX wire/capacity/concurrency claims | Corrected in the inspected files. |
| Operator optional CLI help and follow-up wording | Minor M2; no factual blocker. |
| Task 6 LICENSE omission | Resolved by explicit-file inclusion in the archive rules; not reopened. |
| Task 6 source-root binding, stale readiness, archive closure | Fixes inspected and supported by the reported narrow regressions and isolated archive rebuild; not reopened. |

## Recommendations and validation after fixes

Address the six integration defects in bounded changes. Keep the recall fix within V2's storage/provenance contract, reuse the runtime's existing identity seam, and fix the event sink's concurrency rather than suppressing diagnostics. Do not weaken strict evidence validation or unresolved-write handling to make tests pass.

Run focused regressions for the new failure cases, then the worker/Python suites affected by their contracts. Any shared-runtime change also needs the established offline V1 regressions. Update canonical schema fixtures and source-generated DTO coverage together if a protocol or snapshot schema changes. Prompt/chunk/history changes alter the method, so recapture the actual catalog/guide when applicable and produce a new offline freeze; previous synthetic grades remain bound to their old freeze. No live rerun is needed to establish these fixes.

## Declined to judge

- Real Qwen image interpretation, actual served context, effective sampling/thinking, model revision/BF16/MTP and hardware: no authorized live proof was run. Offline wire checks and explicit attestation classification cannot establish those empirical facts.
- Real GPT-5.2 access, reasoning behavior, provider cost and full-run throughput: credentials/endpoints and authorized synthetic live measurements are still prerequisites; fake-peer measurements are not performance evidence.
- Real V2 scores, complete real domain coverage and generalization: no real data/model run was authorized for this review, and no public score is inferred from invented fixtures.
- Organizer acceptance of a multi-file source ZIP passed as the one code artifact: local official package generation succeeds, but organizer format acceptance remains expressly unresolved.
- Host/power-loss durability: the documented guarantee is process-crash/cancellation behavior; filesystem flush-order durability across power loss has not been established and is not claimed. This does not excuse I5.
- Unrelated pre-existing product/V1 changes in `baseline.patch`, the earlier historical branch and V1 resumption: outside the specified execution range. Relevant dependencies were inspected only where they affect V2 behavior, such as public recall sizing.

## Assessment

**Ready to merge/approve this implementation: No, pending the six Important fixes.** The separation from the official method, ownership model, source validation and freeze/package work are strong. Cross-task seams still permit valid long evidence to become unusable, leave model identity and history assumptions unchecked, expose a concurrent event-writer failure, strand a verified committed snapshot after acknowledgment loss, and give the controller incomplete protocol instructions.

After these offline fixes and their regressions, the result can be approved as an offline implementation ready for separately authorized live synthetic preflight. It still must not be described as live-verified, scored, organizer-accepted or submitted.
