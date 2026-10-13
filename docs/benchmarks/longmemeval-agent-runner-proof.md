# LongMemEval agent runner verification

Offline verification: 2026-10-11. Bounded live proof started on 2026-10-12 after the user supplied the endpoint/model, made the two named credential variables available, and requested continuation. The seven-case live run finished with explained outcomes on 2026-10-12. The separate process proof subsequently verified every recovery boundary using the user-approved 16,384-token agent output allowance. Earlier connectivity and 4,096-token truncation failures remain preserved; the successful retest is recorded below.

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

### Initial 4K live recovery attempts: incomplete

After the smoke ended, the corrected Release runtime used the same dataset, selection, prompts, model settings, tools, payload, and execution limits in a new `resume-run/`. Its manifest content hashes differ only for the runner DLL, runner version, and derived local-input fingerprint. The output directory is separate.

| Boundary | Live evidence |
| --- | --- |
| Interrupt after a persistent write, before answering | Verified. The controller terminated only its owned process tree after successful `remember` calls. Two real `.nmp2` memories remained in the interrupted workspace. |
| Restart incomplete learning from clean state | Verified. Attempt 2 began with the same initial workspace file list (recreated configuration and lock files) and an empty user home, before its first tool call. Source inputs were audited again. |
| Interrupt after a durable saved answer | Not reached. Attempt 2 lost connectivity during session 4; attempt 3 hit the output-token limit during session 38 after 37 completed sessions. |
| Resume grading without replaying learning | Unverified live; the offline boundary tests pass. |
| Skip a completed case on resume | Unverified live; the offline tests pass. |

The failed recovery request recorded `ClientResultException`, status 0, and `PROVIDER_FAILED` after about 92.6 seconds, without a saved answer. One bounded continuation kept the same configuration and retained every attempt. It failed during preflight with another status-0 transport error after about 21.1 seconds; no third attempt for the first case was created. A credential-free request to the endpoint then timed out connecting after ten seconds. Further live attempts stopped because the endpoint was unreachable from the runner.

`resume-control.json` and `resume-continuation-control.json` record both controllers and their owned processes. `audit_resume.py` verifies the observed source inputs. All 60 recovery files remain on disk, with hashes recorded in `resume-final-hashes.json`. The final preservation check confirmed that the original run (204 files), the 60-minute run (292 files), the completed smoke (1,363 files), and the copied original runtime (33 files) are unchanged.

On the user's next continuation request, the endpoint returned the expected unauthenticated HTTP 401 in about 63 ms. Both named credentials were present, authenticated preflight passed, and all 29 runtime assembly hashes matched the recovery manifest. The unchanged configuration resumed case 1 as attempt 3 with the expected initial workspace files and empty user home.

Attempt 3 completed 37 of 47 learning sessions, then failed during session 38 with `ExecutionFailed / learn / RESPONSE_TRUNCATED` after about 35 minutes. The final response reported `finishReason: length` and exactly 4,096 output tokens; the attempt recorded zero model-request errors. No answer or answer receipt was saved. All 38 observed learning inputs match the source dataset. The controller stopped its owned process tree after the failure; case 2 had begun attempt 2 but had not issued a learning request. No benchmark process remains running.

`resume-return-control.json` records this continuation and its clean-start observation. The new `return-preservation-check.json` verifies every original smoke/runtime file and all 60 prior recovery files unchanged; `resume-return-final-hashes.json` records all 265 retained recovery files. Durable event/answer envelope verification also passed. The controller and prior reports remain preserved separately.

At that point, saved-answer interruption, judge-only resume, and completed-case skipping remained unverified. Connectivity had recovered, but the fixed 4,096-token agent response ceiling prevented that attempt from reaching the answer boundary. The subsequent user-approved retest used a new configuration and manifest, preserving this frozen run.

### 16K live recovery retest: verified

The user explicitly requested raising the agent output allowance to 16K and retesting. `resume-16k.json` sets `agent.maxOutputTokens` to 16,384 and writes to a fresh `resume-16k-run/`. All seven selected IDs, dataset, prompts, model settings apart from this allowance, Scrinia payload, runtime, and execution limits are unchanged. The judge retains its 512-token limit. Manifest content hashes match the preceding recovery run except for the derived local-input fingerprint; the resolved configuration differs only in the agent allowance and output directory. No rebuild was needed.

The external `recovery_16k.py` controller completed all four stages on 2026-10-13 UTC (2026-10-12 evening in America/New_York). Each stage terminated only its owned process tree at the intended boundary.

| Boundary | Verified live evidence |
| --- | --- |
| Interrupt after persistent writes, before answering | First case attempt 1 persisted memories and was stopped without an answer receipt. |
| Restart incomplete learning from clean state | Attempt 2 began with the same initial configuration/lock files and empty user home, before tool dispatch. |
| Interrupt after a durable saved answer | Attempt 2 completed all 47 learning sessions and saved its answer; it was stopped at judge phase-start before a judge request or terminal outcome. |
| Resume grading without replaying learning | Attempt 3 recorded `gradingOnly: true`, issued one tool-free judge request, and had no learning events or tool calls. Its copied answer is byte-identical to attempt 2. The judge returned `Yes`, producing `Correct`. |
| Skip a completed case on resume | The next resume created no fourth attempt for the completed first case. The interrupted second case restarted as attempt 2 from the same initial workspace state and empty user home. |

The successful learning/answering attempt took about 40 minutes 38 seconds from attempt-start to judge phase-start. All 286 recorded agent requests carried the 16,384-token ceiling; all 286 received responses, with zero model-request errors. Its largest observed response was 1,950 tokens. Thus this retest verifies the configured allowance and successful recovery flow, but does not establish that extra output capacity caused success: this attempt did not need more than the former 4,096-token ceiling. Six recorded tool errors were handled during the agent flow and did not cause a terminal failure.

`audit_resume_16k.py` verified all 47 historical inputs, the fresh answer input, and the pinned judge prompt. Reported usage for the complete learning/answering attempt was 3,913,032 input and 107,976 output tokens; the grading-only attempt reported 178 input and 54 output tokens. These figures exclude preflight and deliberately interrupted attempts; they are not the total cost of the recovery exercise.

`resume-16k-control.json` records all verified boundaries. Per-stage hash snapshots and `resume-16k-final-hashes.json` retain 284 files. `16k-preservation-check.json` verifies every previous smoke/runtime file, all 265 prior 4K recovery files, and each earlier 16K stage unchanged. It also verifies the dataset hash and byte-identical reused answer. `closed-evidence-audit-16k.json` verifies the durable answer and outcome envelopes.

All owned benchmark processes have stopped. Later selected cases deliberately remain unfinished because this run tests recovery boundaries; it is not another seven-case accuracy result. The original smoke still has scoring coverage of 2/7. The live recovery gate is now verified; a full baseline and other dataset migrations remain outside the approved scope.

### Link failure diagnosis and repair

The original seven-case smoke recorded 30 link calls and 26 failures. Twenty-five failures addressed existing Markdown agent files; one used the invalid nested agent path `/agent/user-interests/gig-economy`. The separate 16K recovery recorded 11 link calls and three failures, all addressing existing agent files.

The local link implementation checked and updated indexed NMP memories and ephemeral memories only. Agent and skill memories created through their reserved paths live in Markdown files with JSON sidecars, so successful `remember` calls could be followed by false `NOT_FOUND` link responses.

The fix routes file-backed endpoints through the existing file-entity transaction coordinator. It preserves body bytes and unrelated sidecar fields, unions reciprocal references under the entity lock, validates both endpoints before writing, and retains support for legacy indexed agent entries. Invalid nested agent paths remain rejected. Local file-entity references use the existing top-level sidecar `ref` convention; this fix does not add file entities to local search results.

A deterministic replay sent all 41 recorded link calls through the actual fixed MCP server against isolated copies of the retained final workspaces. Forty calls succeeded with reciprocal references verified; the invalid nested path returned `INVALID_PARAMETER`. All 28 valid calls that previously failed now succeed. These copies represent the final retained workspace states, not exact historical checkpoints. Regression tests separately cover linking immediately after creation, both endpoint orders, skills, ephemeral destinations, repeated and concurrent links, malformed metadata, preservation across content edits, and legacy indexed agents.

Evidence: `artifacts/agent-bench/link-replay/report.json`, the replay script beside that directory, and `link-fix-tests/link-green-final.trx` (95 focused tests passed). Focused review found two additional edge cases: BOM-encoded sidecars and loss of structured recovery details on second-endpoint failure. Six regressions reproduced them before remediation; the final expanded link/reference/transaction subset passed 144 tests with zero skips (`link-fix-tests/review-green.trx`). The reviewer confirmed both remediations. Original workspace hashes were verified unchanged during replay. This is a product fix and deterministic tool verification, not a new model run or revised accuracy result. The separately published Release payload at `artifacts/agent-bench/scri-link-fix/` passed the same replay (40 successes, one correct invalid-path refusal); `link-replay-release/report.json` and `link-fix-build-provenance.json` retain results and binary/source hashes. The prepared `live-2026-10-12/smoke-16k-link-fix.json` uses this payload and a new output directory, retaining all seven case IDs, the 16,384-token agent limit, and the 512-token judge limit. The rerun has not been launched.

Validation: the full Debug solution suite passed 5,606 tests with zero failures (core 5,089 passed/58 optional or platform skips; agent runner 180 passed; retrieval 272 passed; embeddings plugin 65 passed). That run began before the two review remediations; the final 144-test subset above covers both fixes and the related transaction behavior. The final solution build passed with zero warnings and zero errors using an isolated artifacts directory; its embeddings test fixture required prebuilding the CLI for `win-x64` in that directory. The final preservation check verified all 2,441 frozen evidence/runtime files unchanged, and the published payload still matched its replay and source fingerprints.

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

The interruption cases deny a durable event append after a real memory write, or after a fully saved answer. Both leave prior evidence unchanged. A retry before the answer receipt uses a fresh workspace; a retry after it invokes only the judge. This proves recovery at those durable boundaries. The later 16K live retest verified all these boundaries, including grading-only recovery and completed-case skipping, as described above.

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

## Platform limits and scope

Windows is the validated process lane. The full Linux suite remains unverified: Ubuntu has SDK 10.0.112 and fails resolution of the repository's pinned 10.0.203; Ubuntu-24.04 has no `dotnet`. A standalone managed fixture ran directly on Ubuntu's installed .NET 10 runtime: the original path guard accepted a FIFO; the corrected guard rejects FIFOs, sockets, symlinks, and devices without opening them, while accepting regular files, directories, and missing paths. No SDK installation, global repair, or `global.json` change was made.

The endpoint, model IDs, credential-variable names, and bounded run scope were supplied and exercised. Secret values stay outside configuration and evidence. The endpoint returned and authenticated inference succeeded on the latest continuation. The first continuation then hit the fixed 4K agent output limit. The user-approved 16K retest completed every live recovery boundary; no credential change was needed. The full baseline, other datasets, and legacy-runner retirement remain outside this scope.

## Independent review and remediation

### Execution rulings

- Use the new benchmark test project's full suite for per-task GREEN and run solution-wide verification at final integration as the approved plan prescribes — production code is untouched and prior full-suite duration is substantial — cost if wrong: a cross-project build/test regression could be discovered later, so final solution gates remain mandatory before completion.
- Export one verified saved answer from the latest attempt per question to official-shaped answers.jsonl, retaining every older answer in immutable attempt evidence — duplicate question IDs from all attempts would make the official evaluator's input ambiguous; summary explicitly states this policy — cost if wrong: consumers wanting every historical answer must read attempt evidence instead of the official export.
- Preserve the original resolved configuration in a resumed immutable manifest while recording current resolved paths in each preflight — behavior and content fingerprints exclude relocation paths by design, so byte-identical relocated inputs can resume without rewriting provenance — cost if wrong: readers must inspect the latest preflight for current local paths.
- Fingerprint deployed runtime assemblies instead of relying only on the NuGet lock file — this captures the actual SDK and prompt-builder implementations used by an installed runner — cost if wrong: unrelated DLL changes beside the runner conservatively refuse resume.
- The original offline-only scope was superseded by the authorized bounded live proof above. The original smoke's scoring coverage remains incomplete. The separate 16K retest verifies all live recovery boundaries after the preserved transport and truncation failures; it does not replace or improve the original smoke score.
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

## 2026-10-13: completed seven-case smoke and derived judge scoring

The run in `artifacts/agent-bench/live-2026-10-12/smoke-16k-link-fix-run` completed in approximately 5 hours 2 minutes with both agent and judge output limits set to 16,384 tokens. It selected **7 of the 500 LongMemEval_S questions (1.4%)**, covering six question categories and an abstention case. This was the complete smoke selection, not the full benchmark. All seven questions produced answers with no execution failures. The fixed payload completed 30 link calls with no link errors; 22 errors occurred among 2,593 total tool calls.

The frozen original summary records five correct graded answers and two grading failures. Both failed judge replies began with a standalone verdict followed by an explanation. The approved parser policy, `standalone-opening-line-v1`, accepts a case-insensitive `yes` or `no` on the first nonblank line, optionally ending in one period. Explanations may follow on later lines. A later conflicting standalone verdict is rejected; verdict words embedded in prose are not interpreted. Same-line explanations and prefix matches such as `Yesterday` remain invalid.

Offline re-scoring with the compiled C# parser gives **6 correct / 7 graded (85.7%)**, with 100% grading coverage of the selected smoke cases. Case `00ca467f` becomes correct; abstention case `031748ae_abs` becomes incorrect. The other five verdicts remain correct. This small subset does not establish full-benchmark accuracy.

The separate derived report is `artifacts/agent-bench/live-2026-10-12/derived-scores/standalone-opening-line-v1.json`. It records original grades and outcomes, derived verdicts, policy identity, source-manifest hash, and parser source/assembly hashes. No new model calls were made, and no original grade, outcome, summary, manifest, or event file was rewritten. The frozen Release runner was not rebuilt.

All 1,589 files in the historical hash snapshot still match. A .NET enumeration additionally found eight existing version-metadata files at Windows paths of 260–263 characters that the earlier Python snapshot omitted. The supplemental source inventory records all 1,597 files, verified unchanged before and after re-scoring. The readable local re-scoring utility is retained under `artifacts/agent-bench/parser-v2-rescore/`.

Validation: the new regression cases first produced 10 expected failures against the old parser. After the change, all 48 focused rubric/grader tests and all 200 benchmark-project tests passed. The solution Debug build completed with zero warnings or errors. TRX files are retained under `artifacts/agent-bench/parser-v2-tests/`. The unrelated whole-solution test suite was not repeated for this benchmark-only change.
