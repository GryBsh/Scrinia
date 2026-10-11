# LongMemEval agent runner verification

Date: 2026-10-11. Live proof pending: the user requested offline validation only. No live inference or benchmark quality measurement was performed.

## Verified offline

The standalone runner uses Microsoft Agent Framework 1.24.0, MCP C# SDK 2.2.0, and real Scrinia 0.6.0 subprocesses. Scripted SDK model replies drive the tests; memory operations use actual discovered MCP tools, with no direct production memory-store references.

- Tasks 1–7: 156 tests passed, zero skipped, in the committed orchestration gate.
- Final end-to-end proof: 3 tests passed, zero skipped. The initial proof failed on missing recorded tool definitions; definitions and readable deployed runtime/product versions were added and the proof passed.
- Dedicated model-free payload: `dotnet publish src/Scrinia/Scrinia.csproj -c Release -o artifacts/agent-bench/scri` succeeded.
- Whole-solution Release build succeeded with zero warnings and zero errors.
- `Check-ReleaseContracts.ps1` passed: exact seven skills, canonical version 0.6.0, CLI/MCP metadata, and current workflow targets. Its optional-model warnings are expected for this model-free check.
- Full Release solution tests: running at the time of this interim record; final results will replace this line.

The end-to-end cases demonstrate an assistant-originated location, a corrected appointment, persisted recall across restarted processes, isolation of a second abstention case, tool-free judging, official-shaped JSONL, and summary totals. Scripted verdicts are plumbing assertions, not accuracy evidence.

The interruption cases deny a durable event append after a real memory write, or after a fully saved answer. Both leave prior evidence unchanged. A retry before the answer receipt uses a fresh workspace; a retry after it invokes only the judge. This proves recovery at those durable boundaries. A live process-interruption demonstration remains pending.

## Retained evidence

Local, ignored artifacts are under `artifacts/agent-bench/`:

- `offline-proof/continuity-*/`: synthetic input/configuration, manifest, full tool definitions and exchanges, answers, grades, and report for two cases.
- `offline-proof/before-answer-*/` and `after-answer-*/`: both preserved attempts and separate preflights for each recovery boundary.
- `nickd_XEX-BOX-UN_2026-10-11_11_55_26.trx`: the final three-test proof result.
- `solution-tests/*.trx`: full solution results.
- `scri/`: the separately published model-free payload.

Retained manifests record original temporary fixture paths. These copies are evidence for inspection; they are not ready-to-run live configurations. The model identity `reported-fixture-model` belongs to the scripted test double.

## Platform limits and next live step

Windows is the validated process lane. Linux remains unverified: Ubuntu has SDK 10.0.112 and fails resolution of the repository's pinned 10.0.203; Ubuntu-24.04 has no `dotnet`. No SDK installation, global repair, or `global.json` change was made.

For the bounded live proving run, provide agent/judge endpoint URLs and exact model IDs, then set `BENCH_AGENT_API_KEY` and `BENCH_JUDGE_API_KEY` (or choose different variable names in the config) in the runner's inherited environment. Restart the agent application if necessary. Secret values stay outside configuration and evidence.

Before inference, freeze and announce the lexicographically first non-abstention ID in each official category plus the first abstention ID, along with the dataset hash and ceilings. Complete that bounded run and a separate interruption/resume demonstration, inspect traces, and present the evidence for acceptance. The full baseline and additional datasets remain outside this scope.
