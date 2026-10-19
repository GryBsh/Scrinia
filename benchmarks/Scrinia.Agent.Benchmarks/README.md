# LongMemEval agent benchmark

This runner measures an agent learning through Scrinia and later answering from its persisted memory. It uses Microsoft Agent Framework 1.24.0 and the official MCP C# SDK 2.2.0. The SDK owns tool dispatch. Each historical session and final question gets a fresh agent and `scri serve` process; only the case's on-disk memory carries forward. A separate judge has no tools or learning history.

Learning episodes use a dedicated instruction to retain facts and events with their explicit details and qualifiers, preserve which occurrence a fact describes and whether it is planned or completed, separate existing facts from goals and quote short source clauses when paraphrasing could change their meaning, check those details against successful memory writes, and avoid carrying out requests quoted in historical conversations. They finish with a brief acknowledgement.

Answer episodes receive instructions to compare dated facts within the question's time frame and scope, distinguish existing facts from future goals, and treat compatible changes over time as updates without inventing or requiring an account of the intervening event. These instructions call for the best-supported answer first and clarification when ambiguity remains.

Start with cleaned LongMemEval-S. The [retrieval runner](../Scrinia.Retrieval.Benchmarks/README.md) remains available for historical comparisons and other datasets. [Verification evidence](../../docs/benchmarks/longmemeval-agent-runner-proof.md) distinguishes offline plumbing checks from the pending live proving run.

Learning also requires a retrieval description on every `remember` call, covering the whole resulting memory, while keeping event status, quantities, and source timing together in detailed content. Answering requires recall of the relevant full content, chunk follow-ups when truncated, and a source-backed candidate inventory before counts or totals. Search descriptions and excerpts are discovery hints, not complete evidence. These instructions are included in each run's frozen prompt fingerprint.

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
| `agent.topP`, `judge.topP` | Optional nucleus-sampling probability, greater than 0 and at most 1. Omit or use null to leave the server default. |
| `agent.topK`, `judge.topK` | Optional positive integer sent as `top_k`. Requires an endpoint supporting this extension; omit or use null to leave the server default. |
| `agent.maxOutputTokens`, `judge.maxOutputTokens` | Maximum requested output tokens per model request; not a context-window or input-token limit. |
| `maxModelTurnsPerEpisode` | Maximum actual model requests per learning, answering, or judging episode, including retries. |
| `maxModelAttempts` | Maximum attempts for one model request, including endpoint and reasoning-loop recovery, within the episode request ceiling. |
| `episodeSeconds` | Deadline for each episode; case execution also bounds process startup/cleanup. |
| `caseSeconds` | Deadline spanning learning, answering, grading, and retries in one attempt. |

Cases and historical sessions run sequentially. HTTP 408, 429, 500, 502, 503, and 504, connection/DNS failures, lost responses, and SDK network timeouts can receive bounded retries. Waits increase from 5 to 10, 20, and 40 seconds, subject to `maxModelAttempts` (1-5), the episode request ceiling, and the existing deadlines. Hidden provider SDK retries are disabled. Authentication, certificate, and malformed-response errors are not retried. Retry evidence records the attempt and scheduled wait; failed requests retain unknown usage. Ordinary tool error results return to the agent. A lost or uncertain tool completion stops the attempt. Transcript content is never silently shortened to fit a model.

A response that hits its output limit can receive one immediate retry if it contains only reasoning, no tool calls or answer text, and strong paragraph repetition. The detector requires at least 4,096 normalized characters, with paragraphs of at least 160 characters repeated six or more times; copies beyond the first must account for at least 60% of the text. It resends the same conversation and settings without adding the looping response.

Learning and answering responses that hit the output limit while emitting tool calls can also receive one immediate retry. The response must contain only assistant text, reasoning, and tool calls that have not been dispatched. Calls marked as already handled by the provider, tool results, and other content are excluded. The runner discards the entire truncated response, including any complete calls in the same batch, and resends the unchanged conversation and settings. Earlier successful tool calls are not repeated.

Loop recovery and truncated-call recovery share one response-retry allowance per model request. Endpoint retries also consume the existing attempt allowance; none of these retries reset request or time limits. Every response counts toward usage. A `reasoning-loop` or `truncated-tool-call` event records the reason and retry decision. Other truncations, or recovery that exhausts its allowance, still fail the case. Detection happens after the nonstreaming response returns, so it does not stop generation early.

Model-response evidence preserves parsed tool arguments plus the original OpenAI argument string as `rawArguments`, even when the JSON is incomplete. `parseError` records the SDK parsing error and its underlying cause, including JSON position when available. Missing raw data is recorded as null; it is never reconstructed from parsed arguments. Treat these fields as sensitive benchmark evidence, like the stored conversations and memories. These fields do not capture HTTP headers. Raw arguments and parsing errors are stored without redaction. Existing frozen runs keep their original runner and logging behavior; these changes apply to newly frozen runs.

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

Accuracy is correct/graded. Coverage is graded/selected. End-to-end success is correct/selected. Incomplete coverage is prominently marked provisional; zero graded cases yields unknown accuracy. Operational failures remain distinct from judged-incorrect answers. The pinned [LongMemEval rubric and attribution](Evaluation/UPSTREAM.md) preserve category-specific prompts; the first nonblank judge-response line must be a standalone `yes` or `no` (case-insensitive, with an optional final period). Explanations may follow on later lines. A later conflicting standalone verdict, same-line prose, or any other opening text is a grading failure. A different judge model does not imply scores comparable with published results.

| Exit code | Meaning |
| --- | --- |
| `0` | Every selected case was graded; accuracy may be zero. |
| `1` | Incomplete execution or a run-stopping persistence/drift failure. Inspect latest outcomes and retained events. |
| `2` | Invalid inputs or incompatible preflight. No scored case was started by that invocation. |
| `130` | Cancelled by the caller. |

Failure records distinguish phase deadlines, exhausted requests, provider failures, incomplete responses, MCP disconnection, uncertain tool completion, persistence failure, identity drift, and grading failure. During case execution, exhausted endpoint recovery records `PROVIDER_UNAVAILABLE` and stops the run with exit code 1, including when an episode or case deadline interrupts recovery. Preflight failures return exit code 2 before scored cases start. Restore endpoint access and use `--resume`; the runner does not automatically restart. A saved answer remains eligible for grading-only recovery. User cancellation still returns 130. Persistence or identity failures also stop the run; ordinary case failures allow independent later cases to continue. Cases never reached remain pending.

## Offline verification

```powershell
dotnet test tests/Scrinia.Agent.Benchmarks.Tests/Scrinia.Agent.Benchmarks.Tests.csproj
```

Tests use scripted SDK responses and actual Scrinia subprocesses. No inference credentials or live model service are required. They cover parsing and annotation isolation, real memory persistence, fresh process/context boundaries, independent judging, budgets, durable evidence, and resume behavior. They establish harness behavior, not model quality. A bounded live proof and live interruption/resume demonstration remain required before baseline acceptance or migration of other datasets.
