# LongMemEval agent runner verification

Date: 2026-10-11. Live proof pending: the user requested offline validation only. No live inference or benchmark quality measurement was performed.

## Verified offline

The standalone runner uses Microsoft Agent Framework 1.24.0, MCP C# SDK 2.2.0, and real Scrinia 0.6.0 subprocesses. Scripted SDK model replies drive the tests; memory operations use actual discovered MCP tools, with no direct production memory-store references.

- Tasks 1–7: 156 tests passed, zero skipped, in the committed orchestration gate.
- Final offline gate after review fixes (`c2502fc`): 166/166 passed, zero skipped, in 4m28s. Live proof remains pending; this closes only the authorized offline portion of Task 8.
- Final end-to-end proof: 3 tests passed, zero skipped. The initial proof failed on missing recorded tool definitions; definitions and readable deployed runtime/product versions were added and the proof passed.
- Dedicated model-free payload: `dotnet publish src/Scrinia/Scrinia.csproj -c Release -o artifacts/agent-bench/scri` succeeded.
- Whole-solution Release build succeeded with zero warnings and zero errors.
- `Check-ReleaseContracts.ps1` passed: exact seven skills, canonical version 0.6.0, CLI/MCP metadata, and current workflow targets. Its optional-model warnings are expected for this model-free check.
- Full Release solution test run: core 5,071 passed, 58 optional-model/platform skips, one release-inventory failure; retrieval 272/272 passed; embeddings plugin 65/65 passed; initial agent runner 159/159 passed. The single inventory failure was corrected. The final Release rerun of its complete class passed 54 tests with 14 platform skips. Unchanged core tests were not repeated after that targeted test-only correction.
- Final Release solution build after remediation: zero warnings and zero errors. The standalone Unix path proof also passed against these Release artifacts.

The end-to-end cases demonstrate an assistant-originated location, a corrected appointment, persisted recall across restarted processes, isolation of a second abstention case, tool-free judging, official-shaped JSONL, and summary totals. Scripted verdicts are plumbing assertions, not accuracy evidence.

The interruption cases deny a durable event append after a real memory write, or after a fully saved answer. Both leave prior evidence unchanged. A retry before the answer receipt uses a fresh workspace; a retry after it invokes only the judge. This proves recovery at those durable boundaries. A live process-interruption demonstration remains pending.

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

## Platform limits and next live step

Windows is the validated process lane. The full Linux suite remains unverified: Ubuntu has SDK 10.0.112 and fails resolution of the repository's pinned 10.0.203; Ubuntu-24.04 has no `dotnet`. A standalone managed fixture ran directly on Ubuntu's installed .NET 10 runtime: the original path guard accepted a FIFO; the corrected guard rejects FIFOs, sockets, symlinks, and devices without opening them, while accepting regular files, directories, and missing paths. No SDK installation, global repair, or `global.json` change was made.

For the bounded live proving run, provide agent/judge endpoint URLs and exact model IDs, then set `BENCH_AGENT_API_KEY` and `BENCH_JUDGE_API_KEY` (or choose different variable names in the config) in the runner's inherited environment. Restart the agent application if necessary. Secret values stay outside configuration and evidence.

Before inference, freeze and announce the lexicographically first non-abstention ID in each official category plus the first abstention ID, along with the dataset hash and ceilings. Complete that bounded run and a separate interruption/resume demonstration, inspect traces, and present the evidence for acceptance. The full baseline and additional datasets remain outside this scope.

## Independent review and remediation

### Execution rulings

- Use the new benchmark test project's full suite for per-task GREEN and run solution-wide verification at final integration as the approved plan prescribes — production code is untouched and prior full-suite duration is substantial — cost if wrong: a cross-project build/test regression could be discovered later, so final solution gates remain mandatory before completion.
- Export one verified saved answer from the latest attempt per question to official-shaped answers.jsonl, retaining every older answer in immutable attempt evidence — duplicate question IDs from all attempts would make the official evaluator's input ambiguous; summary explicitly states this policy — cost if wrong: consumers wanting every historical answer must read attempt evidence instead of the official export.
- Preserve the original resolved configuration in a resumed immutable manifest while recording current resolved paths in each preflight — behavior and content fingerprints exclude relocation paths by design, so byte-identical relocated inputs can resume without rewriting provenance — cost if wrong: readers must inspect the latest preflight for current local paths.
- Fingerprint deployed runtime assemblies instead of relying only on the NuGet lock file — this captures the actual SDK and prompt-builder implementations used by an installed runner — cost if wrong: unrelated DLL changes beside the runner conservatively refuse resume.
- Live model quality and live process interruption remain pending — the user explicitly restricted this stage to offline execution; scripted tests establish harness behavior, not model quality — cost if wrong: deployment confidence awaits a separately authorized bounded live run.
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
