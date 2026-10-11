# LongMemEval agent benchmark

This runner measures an agent learning through Scrinia and later answering from its persisted memory. It uses Microsoft Agent Framework 1.24.0 and the official MCP C# SDK 2.2.0. The SDK owns tool dispatch. Each historical session and final question gets a fresh agent and `scri serve` process; only the case's on-disk memory carries forward. A separate judge has no tools or learning history.

Start with cleaned LongMemEval-S. The [retrieval runner](../Scrinia.Retrieval.Benchmarks/README.md) remains available for historical comparisons and other datasets. [Verification evidence](../../docs/benchmarks/longmemeval-agent-runner-proof.md) distinguishes offline plumbing checks from the pending live proving run.

## Prepare a bounded run

1. Obtain a local copy of `longmemeval_s_cleaned.json` from [the cleaned dataset](https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned). The runner does not download datasets. Do not substitute an oracle file, LongMemEval-M/V2, or the old 50/450 retrieval split.
2. Publish a dedicated Scrinia payload from the repository root:

   ```powershell
   dotnet publish src/Scrinia/Scrinia.csproj -c Release -o artifacts/agent-bench/scri
   dotnet build benchmarks/Scrinia.Agent.Benchmarks/Scrinia.Agent.Benchmarks.csproj -c Release
   ```

3. Copy [the example configuration](examples/longmemeval.local.json) and set its paths, endpoints, model IDs, and nonempty `caseIds`. Paths resolve relative to the configuration file; `scrinia.executable` resolves relative to `payloadDirectory` when it is not absolute. Use `scri.exe` on Windows and `scri` on Linux/macOS. Alternatively, use an absolute `dotnet` host path and a single `prefixArguments` entry pointing to the payload's `scri.dll`.
4. Set the environment variables named by the configuration in the runner's parent environment. The example names are `BENCH_AGENT_API_KEY` and `BENCH_JUDGE_API_KEY`. Both must be nonempty. For a local server without authentication, use a non-secret placeholder accepted by that server. Never put tokens in the JSON file. If launching through an agent app, restart it after changing its inherited environment.

`benchmark-agent`, `benchmark-judge`, and `localhost:1234` are illustrative values, not installed aliases or detected services. The agent endpoint must support chat tool calls, temperature, and output-token limits. The judge endpoint must support tool-free chat with its configured options. Preflight requires the agent to successfully call the real `guide` tool and the judge to reply `yes` without tools.

For the first live proof, keep `workspaceConfig` empty and `homeSeedDirectory` null. Choose the lexicographically first non-abstention ID in each of the six official categories, plus the first abstention ID; deduplicate and freeze that list before inspecting model answers. Retain the dataset hash from the resulting manifest. The example deliberately contains an invalid placeholder ID so it cannot accidentally run all cases. An empty `caseIds` array selects the full dataset and should be used only for an explicitly scoped baseline.

## Execute and resume

From the repository root:

```powershell
dotnet run --project benchmarks/Scrinia.Agent.Benchmarks -c Release --no-build -- run --config benchmarks/Scrinia.Agent.Benchmarks/examples/longmemeval.local.json
```

The configuration's output directory belongs to one run. Resume the same run after interruption:

```powershell
dotnet run --project benchmarks/Scrinia.Agent.Benchmarks -c Release --no-build -- run --config benchmarks/Scrinia.Agent.Benchmarks/examples/longmemeval.local.json --resume benchmarks/Scrinia.Agent.Benchmarks/runs/longmemeval-smoke
```

Local input fingerprints are checked before any resume inference. Each invocation repeats bounded capability preflight and records its overhead. Changes to configuration, selected cases, dataset bytes, executable/dependencies, optional assets, prompts, tools, or reported model identity refuse continuation. Byte-identical relocated files can resume; current resolved paths are recorded in the new preflight, while the original manifest remains unchanged. A reported model alias does not prove unchanged provider weights; unavailable model identity is recorded explicitly.

Completed correct/incorrect cases are skipped. A durably saved answer resumes with grading only in a new attempt. Interrupted learning or answering restarts the case with an empty workspace and home, incurring fresh inference work. Old evidence and partial memory remain untouched. No stored tool write is replayed into an interrupted workspace. Ctrl+C cancels current work and waits for owned process cleanup; forced termination leaves partial evidence for the next invocation.

## Limits and cost accounting

| Setting | Meaning |
| --- | --- |
| `agent.maxOutputTokens`, `judge.maxOutputTokens` | Maximum requested output tokens per model request; not a context-window or input-token limit. |
| `maxModelTurnsPerEpisode` | Maximum actual model requests per learning, answering, or judging episode, including retries. |
| `maxModelAttempts` | Maximum attempts for one transient model request, within the episode request ceiling. |
| `episodeSeconds` | Deadline for each episode; case execution also bounds process startup/cleanup. |
| `caseSeconds` | Deadline spanning learning, answering, grading, and retries in one attempt. |

Cases and historical sessions run sequentially. HTTP 408, 429, 500, 502, 503, and 504 can receive bounded retries; hidden provider SDK retries are disabled. Ordinary tool error results return to the agent. A lost or uncertain tool completion stops the attempt. Transcript content is never silently shortened to fit a model.

These are request, token-output, and time ceilings, not a dollar budget. Long transcripts can consume substantial input tokens. Reports include failed attempts and every resume preflight, with learning, answering, and judging usage separated. Missing provider usage stays `null`/unknown, including a request whose response was lost. A grading-only retry receives a new bounded allowance and contributes additional cost.

## Evidence and results

The output directory contains:

- `manifest.json`: immutable configuration and dataset/product/runtime/prompt fingerprints, selected IDs, catalog hash, and reported model identities.
- `preflight/000001/...`: separately numbered capability checks and their isolated probe workspaces.
- `cases/000001/attempts/000001/`: isolated workspace, home, and evidence for one case attempt.
- `evidence/events.ndjson`: durable SDK-visible requests/responses, discovered tools, tool intents/results, process diagnostics, and phase timings. It does not expose hidden model reasoning.
- `evidence/answer.json` and `answer.saved.json`: immutable answer and receipt proving its event prefix was durably written.
- `evidence/grade.json` and `outcome.json`: raw/parsed grading result and final outcome bound to the event stream.
- `summary.json`: latest case states, overall/category accuracy, coverage, all attempt costs, and separate preflight costs.
- `answers.jsonl`: one verified answer from the latest attempt per question in the official evaluator's `question_id`/`hypothesis` shape, including incorrect and ungraded answers. Older answers remain in attempt evidence.

Accuracy is correct/graded. Coverage is graded/selected. End-to-end success is correct/selected. Incomplete coverage is prominently marked provisional; zero graded cases yields unknown accuracy. Operational failures remain distinct from judged-incorrect answers. The pinned [LongMemEval rubric and attribution](Evaluation/UPSTREAM.md) preserve category-specific prompts; only an unambiguous `yes` or `no` is accepted. Malformed judge output is a grading failure. A different judge model does not imply scores comparable with published results.

| Exit code | Meaning |
| --- | --- |
| `0` | Every selected case was graded; accuracy may be zero. |
| `1` | Incomplete execution or a run-stopping persistence/drift failure. Inspect latest outcomes and retained events. |
| `2` | Invalid inputs or incompatible preflight. No scored case was started by that invocation. |
| `130` | Cancelled by the caller. |

Failure records distinguish phase deadlines, exhausted requests, provider failures, incomplete responses, MCP disconnection, uncertain tool completion, persistence failure, identity drift, and grading failure. Persistence or identity failures stop the run; ordinary case failures allow independent later cases to continue. Cases never reached remain pending.

## Offline verification

```powershell
dotnet test tests/Scrinia.Agent.Benchmarks.Tests/Scrinia.Agent.Benchmarks.Tests.csproj
```

Tests use scripted SDK responses and actual Scrinia subprocesses. No inference credentials or live model service are required. They cover parsing and annotation isolation, real memory persistence, fresh process/context boundaries, independent judging, budgets, durable evidence, and resume behavior. They establish harness behavior, not model quality. A bounded live proof and live interruption/resume demonstration remain required before baseline acceptance or migration of other datasets.
