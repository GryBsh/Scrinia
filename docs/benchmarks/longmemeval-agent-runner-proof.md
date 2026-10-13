# LongMemEval agent runner verification

Offline verification: 2026-10-11. Bounded live proof started on 2026-10-12 after the user supplied the endpoint/model, made the two named credential variables available, and requested continuation. The seven-case live run finished with explained outcomes on 2026-10-12. The separate process proof verified interruption and clean restart, but endpoint connectivity blocked the saved-answer, judge-only resume, and completed-case skip boundaries.

## Bounded live proof (2026-10-12 to 2026-10-13 UTC)

The frozen seven-case run uses the original cleaned LongMemEval-S dataset (500 cases; SHA-256 `d6f21ea9d60a0d56f34a05b609c79c88a451d2ae03597821ea3d5a9678c3a442`). Selection was fixed before inference: `01493427`, `00ca467f`, `0e5e2d1a`, `06878be2`, `001be529`, `08f4fc43`, and `031748ae_abs`. These cover the six official categories plus an abstention case.

Both roles use the user-supplied OpenAI-compatible endpoint and requested model `SC117/Ornith-1.5-35B-A3B-MTP-APEX-GGUF:Ornith-1.5-35B-A3B-MTP-APEX-I-Compact`. The provider reports `SC117/Ornith-1.5-35B-A3B-MTP-APEX-GGUF`. Credentials remain in `BENCH_AGENT_API_KEY` and `BENCH_JUDGE_API_KEY`, outside configuration and evidence. Temperature is zero; output limits are 4,096 agent / 512 judge tokens. The original run used episode ceilings of 16 model requests and 120 seconds, at most two attempts per transient request, and a 1,800-second case ceiling.

The original first case reached its 1,800-second deadline during learning: 33 historical sessions completed and the 34th was interrupted. Its immutable outcome is `ExecutionFailed / learn / CASE_DEADLINE`, with no answer or grade. Responses reported 2,322,147 input and 83,580 output tokens; an interrupted request may have additional unavailable usage. The following case had begun when the original owned process tree was stopped; later cases remained unattempted. This run is incomplete, not a quality score.

The user approved a separate 3,600-second case allowance. At 18:35:16 UTC the transition started `smoke-60m.json` in `smoke-60m-run/`, preserving the same seven IDs, dataset, model settings, prompts, episode ceilings, and runtime binaries. The new run repeats learning from clean state. The original run is retained with file hashes in `original-run-preserved-hashes.json`; `budget-transition.json` records the original failure and owned process termination. That run was later stopped after two episode-budget failures, described below.

The 60-minute run's first case later failed with `EPISODE_DEADLINE`: 19 learning sessions completed; session 20 took 10 model requests and about 126.7 seconds including cleanup. Its final model response arrived near the 120-second limit, before the configured ten-second MCP shutdown grace completed. The same source session took five requests and about 55 seconds in the original run. This is a separate episode-budget failure, not a case-budget failure or an incorrect grade. The runner continued to the next independent case. At least 17 successful recalls read paths written in earlier episodes, demonstrating live persistence across fresh MCP processes.

Case two of that run also stopped before answering: `EPISODE_LIMIT` during session 42 after 41 completed learning sessions and 16 model requests in the final episode. Its outcome remains an execution failure, not a grade. The third case had begun when this owned run was stopped; 292 files are preserved with hashes in `smoke-60m-preserved-hashes.json`.

The final `smoke-5m-episodes.json` run used 300 seconds and 40 model requests per episode, retaining the user-approved 3,600-second case ceiling. This execution adjustment addresses the two observed episode constraints without altering prompts, models, data, tools, binaries, output-token limits, retry count, or the seven-case selection. Preflight passed again; only limit settings and output directory differ, and both earlier evidence directories remain hash-identical. These settings stayed fixed through all seven cases and the separate recovery attempts.

Ruling: enlarge episode bounds within the approved total case allowance after both episode limits prevented answering. Cost if wrong: the new attempt incurs additional ingestion work and may still fail within the case limit; all earlier failures and costs remain visible, and no quality score is discarded or substituted.

Preflight successfully executed a real `guide` call and a separate tool-free judge capability check. Initial learning traces contain real memory reads/writes and exactly preserve source user/assistant turns and dates. The final frozen-run outcomes are recorded below. Evidence is retained under `artifacts/agent-bench/live-2026-10-12/`; `audit.py` checks observed learning inputs against the original dataset without printing transcript contents.

### Frozen-run outcomes

The run finished at about 23:54 UTC on 2026-10-12 with all seven cases terminal. It saved five answers, produced two valid grades (both correct), and recorded three grading failures plus two execution failures. Coverage is 2/7 and end-to-end success is 2/7; accuracy among the two graded cases is provisional and is not a full seven-case quality result. These are the actual frozen-runtime outcomes, without reparsing or replacing evidence.

| Case | Actual outcome | Evidence |
| --- | --- | --- |
| `01493427` | `GradingFailed / JUDGE_INVALID_VERDICT` | All 47 learning sessions completed and an answer was saved. Raw judge `No.` was rejected by the frozen strict parser. |
| `00ca467f` | `Correct` | All 47 learning sessions completed; raw judge `Yes`. 264 requests/responses, 3,237,534 input and 102,118 output tokens. Eight tool errors were recovered without an execution failure. |
| `0e5e2d1a` | `ExecutionFailed / RESPONSE_TRUNCATED` | 21 of 46 learning sessions completed. Session 22 returned `finishReason: length` with 4,096 output tokens, the configured limit. No answer or grade was produced. |
| `06878be2` | `GradingFailed / JUDGE_REQUEST_FAILED` | All 48 learning sessions completed and an answer was saved. The judge returned `finishReason: length` at 512 output tokens without verdict text. 256 requests/responses, 3,088,869 input and 102,009 output tokens. |
| `001be529` | `Correct` | All 51 learning sessions completed, including repeated source IDs at distinct dates; raw judge `Yes`. 280 requests/responses, 3,505,507 input and 114,623 output tokens. |
| `08f4fc43` | `ExecutionFailed / PROVIDER_FAILED` | 41 of 47 learning sessions completed. Session 42 received HTTP 502 twice, exhausting the configured two attempts; no answer or grade. The episode used 13 requests, 11 responses, and about 242 seconds. |
| `031748ae_abs` | `GradingFailed / JUDGE_REQUEST_FAILED` | All 52 learning sessions completed and an answer was saved. The judge returned `finishReason: length` at 512 output tokens without verdict text. |

The independent audit verified 309 observed source learning inputs (307 completed sessions), plus fresh question/date-only answer inputs and exact pinned judge prompts for all five answered cases. `verify_closed.py` also verified all terminal event hashes and saved-answer receipts. The truncation remains an explained failure under the fixed configuration; no output-token increase or replacement attempt is planned for this smoke run.

The dataset SHA-256 and all five JSONL exports were independently verified. The completed run's 1,363 files are protected by `smoke-5m-preserved-hashes.json`. The original Release runtime is copied to `frozen-smoke-runtime/`; all 29 assembly hashes match its manifest, and all 33 copied files have recorded hashes. The tested corrected Release build subsequently succeeded with zero warnings and errors for the separate recovery proof.

Recorded case responses report at least 21,415,796 input and 713,552 output tokens. Total case usage remains explicitly unknown because the two failed provider requests may have unreported usage. Preflight is separate: 12,832 input and 122 output tokens. The runner exited 1 for incomplete successful coverage and kept both the official-shaped five-answer export and the coverage-aware summary.

### Judge punctuation correction

The frozen 300-second episode run completed all 47 learning sessions for first case `01493427`, saved an answer, and invoked the separate judge. The raw verdict was `No.`. The frozen runtime accepts only exact trimmed yes/no, so its immutable outcome is `GradingFailed / JUDGE_INVALID_VERDICT`; this is not recorded as an incorrect grade. All 47 learning inputs matched the source projections, the answer request contained only question/date, and the judge prompt matched the pinned rubric rendered with the original reference and saved answer.

The user approved accepting one trailing period. The source parser now accepts `yes.` and `no.` with existing case/outer-whitespace handling; explanations, mixed answers, internal spaces, other punctuation, and repeated periods remain invalid. Six new acceptance cases failed against the old parser, then all 28 focused rubric/grader tests passed with the correction. Tests also confirm the raw reply remains unchanged and is recorded before the verdict.

The full benchmark regression suite passed 180/180 with zero skips in 4m11s in Debug; its TRX is retained under `live-2026-10-12/verdict-regression/`. All 29 Release assembly hashes still matched the active manifest after this verification, so the active smoke binary stayed frozen through completion. Existing answers, receipts, grades, outcomes, and manifests are not rewritten. The corrected binary was built only after smoke completion and used a new recovery-run manifest; no frozen-manifest fingerprint was bypassed.

### Live interruption and recovery: partially verified

After the smoke ended, the corrected Release runtime used the same dataset, selection, prompts, model settings, tools, payload, and execution limits in a new `resume-run/`. Its manifest content hashes differ only for the runner DLL, runner version, and derived local-input fingerprint. The output directory is separate.

| Boundary | Live evidence |
| --- | --- |
| Interrupt after a persistent write, before answering | Verified. The controller terminated only its owned process tree after successful `remember` calls. Two real `.nmp2` memories remained in the interrupted workspace. |
| Restart incomplete learning from clean state | Verified. Attempt 2 began with the same initial workspace file list (recreated configuration and lock files) and an empty user home, before its first tool call. Source inputs were audited again. |
| Interrupt after a durable saved answer | Not reached. Attempt 2 failed during learning session 4 after three completed sessions. |
| Resume grading without replaying learning | Unverified live; the offline boundary tests pass. |
| Skip a completed case on resume | Unverified live; the offline tests pass. |

The failed recovery request recorded `ClientResultException`, status 0, and `PROVIDER_FAILED` after about 92.6 seconds, without a saved answer. One bounded continuation kept the same configuration and retained every attempt. It failed during preflight with another status-0 transport error after about 21.1 seconds; no third attempt for the first case was created. A credential-free request to the endpoint then timed out connecting after ten seconds. Further live attempts stopped because the endpoint was unreachable from the runner.

`resume-control.json` and `resume-continuation-control.json` record both controllers and their owned processes. `audit_resume.py` verifies the observed source inputs. All 60 recovery files remain on disk, with hashes recorded in `resume-final-hashes.json`. The final preservation check confirmed that the original run (204 files), the 60-minute run (292 files), the completed smoke (1,363 files), and the copied original runtime (33 files) are unchanged.

Remaining gate: once the endpoint is reachable, finish the saved-answer interruption, judge-only resume, and completed-case skip proof with preserved attempts and unchanged configuration. This live recovery gate remains unchecked; the current run does not establish it.

### Dataset compatibility correction

The first launch stopped during validation before inference because the reader incorrectly required unique source session IDs. Thirteen source cases reuse a session ID; in selected case `001be529`, an identical transcript appears at two different dates. The reader now preserves both dated occurrences and still requires unique question IDs and nonempty session IDs. The dataset and selection were not altered. A regression first failed on the old guard, then the reader/input-isolation checks passed 22/22 with zero skips. The full benchmark suite subsequently passed 167/167 with zero skips in 4m31s (Debug, preserving the active Release binaries); its TRX is retained under `live-2026-10-12/full-regression/`. Runtime build `6219b3a` contains this fix; subsequent source line-ending cleanup does not change the frozen Release binaries.

Ruling: treat source session IDs as reusable transcript identifiers, preserving every dated occurrence in stable chronological order. Requiring uniqueness would reject valid source data; deduplication would change benchmark information. Cost if wrong: malformed repeated records remain visible to the agent, consistent with preserving the original benchmark rather than silently repairing it.

## Verified offline

The standalone runner uses Microsoft Agent Framework 1.24.0, MCP C# SDK 2.2.0, and real Scrinia 0.6.0 subprocesses. Scripted SDK model replies drive the tests; memory operations use actual discovered MCP tools, with no direct production memory-store references.

- Tasks 1–7: 156 tests passed, zero skipped, in the committed orchestration gate.
- Final offline gate after review fixes (`c2502fc`): 166/166 passed, zero skipped, in 4m28s. This closed the original offline portion of Task 8; the later live results and remaining gate are recorded above.
- Final end-to-end proof: 3 tests passed, zero skipped. The initial proof failed on missing recorded tool definitions; definitions and readable deployed runtime/product versions were added and the proof passed.
- Dedicated model-free payload: `dotnet publish src/Scrinia/Scrinia.csproj -c Release -o artifacts/agent-bench/scri` succeeded.
- Whole-solution Release build succeeded with zero warnings and zero errors.
- `Check-ReleaseContracts.ps1` passed: exact seven skills, canonical version 0.6.0, CLI/MCP metadata, and current workflow targets. Its optional-model warnings are expected for this model-free check.
- Full Release solution test run: core 5,071 passed, 58 optional-model/platform skips, one release-inventory failure; retrieval 272/272 passed; embeddings plugin 65/65 passed; initial agent runner 159/159 passed. The single inventory failure was corrected. The final Release rerun of its complete class passed 54 tests with 14 platform skips. Unchanged core tests were not repeated after that targeted test-only correction.
- Final Release solution build after remediation: zero warnings and zero errors. The standalone Unix path proof also passed against these Release artifacts.

The end-to-end cases demonstrate an assistant-originated location, a corrected appointment, persisted recall across restarted processes, isolation of a second abstention case, tool-free judging, official-shaped JSONL, and summary totals. Scripted verdicts are plumbing assertions, not accuracy evidence.

The interruption cases deny a durable event append after a real memory write, or after a fully saved answer. Both leave prior evidence unchanged. A retry before the answer receipt uses a fresh workspace; a retry after it invokes only the judge. This proves recovery at those durable boundaries. The later live run verified the pre-answer interruption and clean restart; the saved-answer and completed-case boundaries remain unverified live, as described above.

## Retained evidence

Local, ignored artifacts are under `artifacts/agent-bench/`:

- `offline-proof/continuity-*/`: synthetic input/configuration, manifest, full tool definitions and exchanges, answers, grades, and report for two cases.
- `offline-proof/before-answer-*/` and `after-answer-*/`: both preserved attempts and separate preflights for each recovery boundary.
- `nickd_XEX-BOX-UN_2026-10-11_11_55_26.trx`: the final three-test proof result.
- `solution-tests/*.trx`: full solution results.
- `final-offline/*.trx`: the final 166-test benchmark gate; `review-red/`, `review-green/`, `cleanup-red/`, and `cleanup-green/` retain regression evidence.
- `release-inventory-green/*.trx`: the corrected Release inventory class (54 passed, 14 platform skips).
- `scri/`: the separately published model-free payload.

Retained manifests record original temporary fixture paths. These copies are evidence for inspection; they are not ready-to-run live configurations. The model identity `reported-fixture-model` belongs to the scripted test double.

## Platform limits and remaining live step

Windows is the validated process lane. The full Linux suite remains unverified: Ubuntu has SDK 10.0.112 and fails resolution of the repository's pinned 10.0.203; Ubuntu-24.04 has no `dotnet`. A standalone managed fixture ran directly on Ubuntu's installed .NET 10 runtime: the original path guard accepted a FIFO; the corrected guard rejects FIFOs, sockets, symlinks, and devices without opening them, while accepting regular files, directories, and missing paths. No SDK installation, global repair, or `global.json` change was made.

The endpoint, model IDs, credential-variable names, and bounded run scope were supplied and exercised. Secret values stay outside configuration and evidence. The remaining live recovery boundaries require the endpoint to become reachable again; no new credentials or configuration change is implied by the transport failures. The full baseline, other datasets, and legacy-runner retirement remain outside this scope.

## Independent review and remediation

### Execution rulings

- Use the new benchmark test project's full suite for per-task GREEN and run solution-wide verification at final integration as the approved plan prescribes — production code is untouched and prior full-suite duration is substantial — cost if wrong: a cross-project build/test regression could be discovered later, so final solution gates remain mandatory before completion.
- Export one verified saved answer from the latest attempt per question to official-shaped answers.jsonl, retaining every older answer in immutable attempt evidence — duplicate question IDs from all attempts would make the official evaluator's input ambiguous; summary explicitly states this policy — cost if wrong: consumers wanting every historical answer must read attempt evidence instead of the official export.
- Preserve the original resolved configuration in a resumed immutable manifest while recording current resolved paths in each preflight — behavior and content fingerprints exclude relocation paths by design, so byte-identical relocated inputs can resume without rewriting provenance — cost if wrong: readers must inspect the latest preflight for current local paths.
- Fingerprint deployed runtime assemblies instead of relying only on the NuGet lock file — this captures the actual SDK and prompt-builder implementations used by an installed runner — cost if wrong: unrelated DLL changes beside the runner conservatively refuse resume.
- The original offline-only scope was superseded by the authorized bounded live proof above. Its scoring coverage is incomplete, and the remaining live recovery boundaries are blocked by endpoint connectivity. Scripted tests establish those offline behaviors; they do not substitute for the missing live evidence.
- Native Linux whole-suite execution remains unverified — the installed SDK cannot satisfy the repository pin; a standalone native-runtime regression verifies the concrete Unix file-type fix — cost if wrong: Linux integration issues may remain beyond that tested boundary.
- Legacy runner retirement, other datasets, full baselines, and dollar estimates remain deferred — the approved scope proves LongMemEval first and records tokens/time without inventing provider pricing — cost if wrong: users retain two runners and must calculate monetary costs separately.
- Hostile concurrent filesystem replacement and physical power-loss guarantees remain outside this proof — path validation is not an OS sandbox; tests cover ordinary write failures and durable recovery boundaries — cost if wrong: stronger adversarial/crash guarantees need separate architecture and testing.
- Extend the existing release-inventory test's explicit exclusions for the new benchmark executable and executable test fixture — the whole-solution gate demonstrated these developer-only projects otherwise become unexplained release omissions — cost if wrong: a future decision to distribute the runner must deliberately move it into the shipping manifest.
- Run the single fresh whole-branch review against the committed implementation while final solution verification finishes — source and focused end-to-end proof are frozen, and the reviewer is read-only; remaining test results and narrow fixes are reported to that same reviewer — cost if wrong: any later implementation change must receive focused regression coverage because there is no second review pass.
- Configure the SDK FunctionInvokingChatClient explicitly beneath ChatClientAgent with UseProvidedChatClientAsIs=true — Agent Framework's default function loop has a 40-iteration ceiling not exposed by its agent options; explicit SDK middleware preserves sequential dispatch and the declared request budget without writing a custom loop — cost if wrong: default agent decorators are bypassed, so tests must prove local conversation history and tool dispatch behavior.
- Add a readable ProcessFixture.cs entry point in the test assembly and make that test assembly executable for failed-handshake/hung-child cases — real Scrinia remains the successful MCP integration target, while deterministic failure fixtures avoid encoded shell commands and modifications to production or shared fixtures — cost if wrong: the new test executable must continue working under the solution test runner, covered by final solution validation.
- Publish a separate answer receipt with a hash of its durable event prefix before treating an ungraded answer as reusable — a saved answer must survive a later interrupted grading append without presenting that append as a complete outcome — cost if wrong: receipt publication failure forces a fresh attempt while preserving the answer evidence.
- IRunEvents also implements IAsyncDisposable; the journal disposes its attempt sinks and the application owns preflight sink disposal — asynchronous event gates need an explicit lifetime and the existing write contract remains unchanged — cost if wrong: callers retaining a sink beyond its owner lifetime fail explicitly.
- Use a create-new sibling reservation file to claim each attempt, retaining reservations after initialization failure — Directory.CreateDirectory alone is not exclusive; failed initialization must consume its attempt number — cost if wrong: incomplete attempts require a new number, preserving evidence rather than recycling it.

### Review findings

One fresh whole-branch review found no Critical findings and four Important findings. The single remediation pass added regression coverage for:

- MCP exit after a successful tool reply, before final model text: the old implementation accepted success; the corrected lifecycle monitor rejects the episode and cancels pending work. Normal intentional shutdown still passes.
- First journal failure preservation: failed writes retain their original exception and cancellation, subsequent cleanup cannot publish an outcome through the failed sink, and secondary summary failures do not conceal the primary failure. Recovery tests check `JOURNAL_FAILED` as well as preserved evidence.
- Unix special-file classification before content access, demonstrated on a native Linux runtime as described above.
- Safe actionable validation diagnostics: malformed configuration, missing dataset fields, existing runs requiring `--resume`, and missing named credential variables. Only explicitly authored validation messages reach the CLI; raw values and provider exception text remain excluded.

The integration suite also identified the two new executable projects as unexplained release omissions. Both are now explicitly classified as development/test tools in the existing inventory test; the release manifest is unchanged.

Deferred minor findings: README commands still name the example after instructing users to copy it; console accuracy shows `0/0` when no case is graded; “phase costs” refers to tokens/time rather than money; “empty workspace/home” wording omits recreated configuration and optional seed assets. The report-only UX review recorded two Must fix and three Should fix findings; the functional diagnostic issue was included in the correctness remediation.
