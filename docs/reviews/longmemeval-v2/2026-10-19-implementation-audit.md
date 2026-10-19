# LongMemEval V2 implementation audit

Date: 2026-10-19
Status: Offline implementation complete; all six final findings independently approved. Live verification remains unverified.
Branch: codex/longmemeval-agent-runner
Implementation range: f79eb3907b7f3395105353830453b76bb492f2f0..5f41ebb25cbd7ec417145e18d99410074aa34ac3

## Delivered scope

External Python adapter and owned .NET worker integrate Scrinia with the pinned, unchanged official LongMemEval-V2 harness, reader, judge and packaging utilities. Six planned tasks passed independent review; the broad integration review then identified six Important issues, implemented together in 5f41ebb. The final scoped review approved I1-I6 with no new findings; see 2026-10-19-final-approval.md.

The implementation preserves source text/images, uses guarded learning and read-only retrieval, checks actual public recall evidence, isolates snapshots and conversations, binds ordered histories, retains bounded recovery, and freezes source/runtime/dependency/data/prompt identities. The source archive builds through its included worker/CLI project graph.

The original V1 run remains paused. No real V2 dataset examples, live inference, public score, submission, merge or push occurred during this implementation. Earlier synthetic grades remain attached to their original method freezes.

## Verification

| Check | Result |
|---|---|
| Final solution build | Passed; zero warnings/errors |
| Final worker suite | 53 passed |
| Final full Python suite | 69 passed |
| Subsequent fixture-only relocation checks | 2 passed; intended identity/fingerprint errors asserted |
| Offline V1 unit baseline | 281 passed; final fix did not change shared runtime/V1 |
| Actual frozen synthetic web/enterprise pair | Passed; image-only source evidence, unchanged official reader/judge/package paths |
| Extracted production source ZIP | Both locked offline restores, Release builds and local publishes passed |
| Final prompt/contract UX review | No findings; all earlier prompt contract findings resolved |
| Final fresh freeze and offline preflight | Passed; 496 source files verified; live unverified |

Final test/freeze evidence is under artifacts/longmemeval-v2/validation/final-fix/. Earlier Task 6 and isolated archive acceptance evidence remains under artifacts/longmemeval-v2/validation/task6-final/ and task6-fix1/.

Final manifest SHA-256:
db3e5fb8f5615afeef1527357bfee11273c9715eebe7da6e427178f65dbfd90b

Final method fingerprint:
275db44f47e95767d05b3385fad4eecd9f7f179d9ced04d4c1829a6e25bbd954

Final source ZIP SHA-256:
cdf44044a18bac1ffbd29b19b518c4215a39fa4a711bd864ae23f4117a5799a8

The final freeze uses current source/binaries/prompts and actual canonical catalog/guide capture, invented source-only data, unexecuted run roots and empty measurements. It is an offline validation artifact, not a production experiment or a reuse of earlier grades.

## Remaining live prerequisites

- Supply and pin real dataset and immutable deployment identities without inspecting real answers during development.
- Supply GPT-5.2 endpoint/access and an explicit credential-variable reference.
- Verify Qwen images, returned identity, effective sampling, server-default thinking and actual context/overhead; retain the distinction between empirical probes and deployment attestation.
- Estimate real cost/runtime from authorized live synthetic measurements; fake-peer timings do not establish either.
- Confirm organizer acceptance of the multi-file source archive format before submission.
- Obtain authorization before live evaluation or submission.

Process-crash/cancellation evidence is not a power-loss durability guarantee. The source freeze includes the actual tested dirty working-tree prerequisites; unrelated prior product/V1 edits were not silently committed by the V2 tasks.

## Deferred nonblocking items

- Repeated owned-path schema definitions are currently consistent; consolidate when that schema is next changed.
- Optional operator wording and preflight help improvements remain suggestions. Factual documentation and fatal prompt-contract mismatches were corrected.

## Review record

- Initial broad integration findings: 2026-10-19-initial-integration-review.md.
- Final grouped fix report and scoped review: 2026-10-19-fix-validation.md and 2026-10-19-final-approval.md.
- Original and follow-up UX reports: ../ux-polish/2026-10-19-longmemeval-v2-*.md.
- Complete plan ledger/reports/packages are preserved under ignored artifacts/longmemeval-v2/validation/final-audit/ before plan-scratch cleanup.

## Rulings made

1. Preserve existing dirty edits in task-owned files when moving/extracting them and include those prerequisites if a task commit necessarily carries them; do not commit unrelated paths - the tested working copy is the source of truth - if wrong, reviewer must disentangle baseline versus extraction using the saved patch.

2. Use this execution baseline for the final V2 review rather than the entire historical V1 branch merge-base, with the dirty baseline patch available - older release/V1 work is outside this implementation - if wrong, a cross-boundary dependency could need an additional focused audit.

3. Task 2 must fail explicitly for real learn/retrieve until their implementation tasks, rather than ship fake successful placeholder outputs - scaffolding is intermediate only - if wrong, early end-to-end validation is deferred by two tasks.

4. V2 caller/source catalog owns image sidecars; shared runtime records media type/hash/byte count only, with fixture verification. Reject remote image URIs at V2 boundaries in Tasks 2-4 - no evidence path exists in the shared runtime contract and V1 behavior must stay stable - if wrong, missing caller persistence would weaken replay evidence and must fail later source validation.

5. Keep ordinary canonical paths in worker/MCP protocol and implement explicit extended-length filesystem access inside V2 Python/official-reader boundaries - fixes the observed integration issue without changing unrelated product recovery semantics - if wrong, an overlooked IO conversion can still reject long valid paths; regression must cover copy/hash/cleanup/relocation and official image reads. Avoid implicit dual-identity Path subclass unless a scoped justification and reconstruction tests show it necessary.

6. Live readiness may combine hashed explicit deployment/config/log attestation with empirical probes rather than require a bespoke applied-options response field - ordinary OpenAI-compatible endpoints do not universally report effective settings, and HTTP200 alone proves nothing - if wrong, inadequate attestation could hide silently ignored options; unknown/conflicting evidence must fail readiness and attested facts must not be labeled empirical.

7. Rebuild the source archive through its included worker/scri project graph and omit the misleading full solution, rather than archive unrelated solution projects - this keeps the method artifact focused while satisfying reproducibility through explicit locked restore/build/publish - if wrong, an omitted transitive build/resource input could break reconstruction; closure checks and isolated extraction builds are required.

8. Retain live Qwen capabilities, GPT-5.2 access/cost, real benchmark scores/coverage, and organizer source-ZIP acceptance as explicitly unverified stages; retain the documented process-crash-only guarantee without claiming power-loss durability - offline fixtures and local package utilities cannot prove those facts, and live execution was excluded - if wrong, additional capability/format/durability work may be required before a valid scored or publishable result; no current readiness claim may imply those stages passed.

9. Use a frozen 7900-UTF16-character Unicode-safe storage-chunk policy with exact public-recall verification/reconstruction, with an explicit guarded derived-note limit (refined from automatic subdivision before implementation to preserve raw arguments) - token limits alone cannot guarantee recallable character size and public recall has no within-chunk paging - if wrong, reconstruction or prefix accounting could still reject valid evidence; production-cap/Unicode/metadata/multichunk regressions and a new method freeze are required, and old incompatible bundles must be rejected.
