# LongMemEval agent runner

Date: 2026-10-11

Status: Design agreed in conversation; written specification awaiting user review.

## Purpose and agreed scope

Measure how well an agent retains useful information through Scrinia and answers questions later. The benchmark should exercise the product through its normal MCP surface and use a standard .NET agent SDK for model interaction and tool execution.

The user approved these decisions:

- Start with cleaned LongMemEval-S; add the other datasets only after this complete flow is proven.
- Use one agent implementation for learning and answering, with fresh agent context for every historical session and for the final question. Only persisted Scrinia state carries forward.
- Use Microsoft Agent Framework, the official MCP C# SDK, and a configurable OpenAI-compatible endpoint/model.
- Keep experiment control in the runner: case selection, isolation, limits, scoring, and evidence capture.
- Preserve user and assistant turns, session dates, and question dates. Never expose evaluation labels or reference answers to the tested agent.
- Use LongMemEval's category-specific grading rules in a separate, tool-free judge call.
- Record configuration, fingerprints, messages, tool interactions, answers, grading, usage, and duration. Resume completed cases only under matching configuration; restart interrupted cases from empty state.

The concrete contracts below make those decisions implementable and are part of the written review. They do not authorize implementation before this specification and the subsequent implementation plan are reviewed.

## Current system and replacement boundary

The existing `benchmarks/Scrinia.Retrieval.Benchmarks` combines retrieval experiments, metadata generation, an agentic ingestion loop, custom provider protocols, manually copied tool schemas, retriever variants, and answerability judging. Its `LongMemEvalLoader` keeps user turns only, reads but does not propagate the question date, and filters records around retrieval evidence IDs. Its generic grader differs from LongMemEval's category-specific evaluator.

The replacement is a small standalone benchmark executable under `benchmarks/Scrinia.Agent.Benchmarks`, with a focused test project. It must not reference Scrinia Core, MCP implementation classes, or the embeddings implementation for memory operations. It launches the configured Scrinia executable and communicates through MCP.

The existing runner remains available for historical comparison and the deferred datasets while the new flow is proven. New agent benchmark work belongs in the new executable. This is a bounded transition, not a commitment to maintaining two competing agent runners. Retirement of old agent paths follows proof of the replacement; deletion of other dataset support is outside this first phase.

Do not carry across ranker tuning, reranker experiments, provider CLI wrappers, mechanical ingestion fallbacks, forced per-turn memory writes, citation-based retrieval scoring, or episode-response caches. Preserve useful parsing knowledge and fixtures, but do not reuse the lossy retrieval DTO as the agent's input contract.

Release-readiness work is parked. This design branch starts at `089c21b72d5ae374dfe5d5bb4279c2fe7d2827ba`, the final report commit above the validated product candidate. It does not merge, publish, or alter the retained release evidence.

## Architecture

| Component | Responsibility |
| --- | --- |
| LongMemEval reader | Validate input and produce chronological, role-preserving sessions plus separate evaluation data. |
| Case runner | Own case state, process lifetimes, budgets, fresh sessions, and transitions. |
| Agent adapter | Configure one SDK agent implementation and discover tools through MCP. |
| Grader | Apply the pinned LongMemEval rubric to a saved final answer, without tools or agent history. |
| Run recorder | Write manifests, event records, case outcomes, and aggregate reports; validate resume compatibility. |

These are responsibility boundaries, not a plugin framework. Begin with sequential cases and sequential learning sessions. No agent orchestration graph, runtime discovery of benchmark plugins, or parallel scheduling is needed.

The runtime flow is:

`validated case -> isolated workspace -> learning sessions -> fresh answering session -> saved answer -> judge -> case outcome`

The SDK owns the model/tool invocation loop and protocol adaptation. The runner observes that loop to capture events and enforce budgets; it does not recreate tool-call parsing or dispatch by tool name. Official SDK package versions are pinned by the implementation and recorded in run identity.

## Dataset contract

The initial input is a local copy of `longmemeval_s_cleaned.json` from `xiaowu0162/longmemeval-cleaned`. Record the file's SHA-256 and selected case IDs. Dataset download is a separate preparation action, not an implicit network operation during a run. LongMemEval-M, oracle inputs, LongMemEval-V2, and the legacy third-party 50/450 split are outside the initial default.

The default selection is all cases. An explicit ordered list of case IDs permits small reproducible proving runs. Reject duplicate or unknown selected IDs. Validate selected records before invoking models: required fields, unique identities, supported categories, nonempty conversations, aligned session/date/ID arrays, and parseable dates. Reject invalid input with a precise location rather than silently skipping it.

Preserve every user and assistant turn in its original order within a session. Process sessions chronologically, retaining source order for equal dates. Preserve the source date text; use explicit invariant parsing for ordering and validation, never host-local timezone assumptions. Supply the question's date only during answering.

Agent-visible session data contains role-labelled content, the session date, and neutral ordinal labels. Replace source IDs with neutral labels because source identifiers may reveal which sessions contain evidence. Treat historical messages as a quoted transcript, not as new system/developer instructions.

Keep the following in a separate evaluation record inaccessible through the agent's inputs or tools: reference answer, `answer_session_ids`, `has_answer`, question type, abstention marker, original question ID, and other annotation fields. Learning also receives neither the future question nor its date. The harness retains mappings for diagnostics outside the MCP workspace.

Abstention cases remain valid even without retrieval evidence IDs. Do not inherit the old reader's evidence-ID filtering rule.

## Agent and memory lifecycle

Each case attempt owns a new workspace and a new `SCRINIA_HOME`. Neither can refer to the developer's workspace, memory, skills, registry, or user configuration. The configured Scrinia executable and dependencies are explicit inputs. Child process environment construction excludes inherited Scrinia configuration and unrelated credentials.

For every learning session:

1. Start `scri serve` against that attempt's workspace and isolated home.
2. Discover its tools using the official MCP client.
3. Create a fresh SDK agent session with fixed base instructions and the learning task.
4. Present only the current historical session with its date. Instruct the agent to use the product's guide and retain information it considers useful for future interactions.
5. Let the agent finish within the configured budgets, then close its MCP client and server process before advancing.

The next learning session can recover earlier information only through persisted Scrinia state. Restarting the MCP process as well as the agent session prevents ephemeral server state from carrying across sessions. SDK/provider conversation IDs and prior chat history are not reused.

There is no minimum memory-write count and no mechanical fallback. Choosing to retain nothing is a legitimate agent outcome; a transport failure is an execution failure. Do not impose a benchmark-specific memory schema or tell the agent which facts will be tested.

Answering uses the same agent implementation and base instructions, a fresh SDK session and MCP process, the same persisted workspace, the final question, and its date. It receives no raw historical transcript. It can use the same discovered tool surface, including writes; each case has only one scored question and its state never feeds another case. It should answer from remembered information and acknowledge insufficient information when appropriate.

Only Scrinia MCP tools are attached. No filesystem, shell, web, or dataset-reading tools are exposed. Trace and reference files live outside the MCP workspace. Record the discovered tool definitions and reject unexpected schema changes within one run.

The first proving configuration uses no optional embeddings plugin, avoiding accidental dependence on local model installations. Subsequent configurations may explicitly provision a plugin and its assets into the isolated home; their content hashes and configuration become part of run identity. Missing requested assets are a preflight error, never an implicit fallback. This does not add benchmark-specific retrieval modes.

## Configuration and budgets

Expose one execution path: `scrinia-agent-bench run --config <file>`. Resume uses that configuration plus `--resume <run-directory>`. The implementation plan defines the exact JSON schema and supplies an example; this specification fixes its required meaning.

Configuration identifies dataset and case selection, Scrinia executable/arguments and optional assets, agent endpoint/model, judge endpoint/model, sampling parameters, output directory, and explicit execution limits. Credentials are named environment-variable references, never literal values copied into run files.

Require positive limits for model turns per episode, output tokens per model request, episode duration, total case duration, and transient model-request retries. Validate these before starting. Do not equate the completion-token limit with a provider's context window. If an input cannot fit or the model truncates its response, record an incomplete episode; never silently trim transcript content or call it successful.

The same model configuration is used for learning and answering. The judge is separately configurable through the same SDK/provider path and may use the same model or a different one, always with separate context and no tools. Validate tool-calling support before a paid benchmark run. Provider-specific usage fields can be unavailable; represent that explicitly rather than inventing zero usage.

SDK retry behavior must be bounded and recorded. A normal tool error returned to the agent can be handled by the agent within its remaining budget; it does not automatically fail the case. Lost transport, uncertain write completion, exhausted limits, or an unrecoverable model failure terminate the attempt. The runner never blindly retries an uncertain tool write. An interrupted or failed learning/answering attempt must restart the case with a new empty workspace.

## Scoring and outcome semantics

Adapt and pin the official LongMemEval evaluator's category-specific prompt templates: extraction/multi-session, temporal reasoning, knowledge update, preference, and abstention. Record the upstream revision and local prompt hash. Preserve its rubric semantics rather than using the old generic grader. Report the actual judge model and settings; using the same rubric with a different judge does not establish identical published scores.

The judge sees only the question, reference answer or rubric, candidate final answer, and evaluation category needed to select the template. It receives no tools, persisted memory, or tested-agent transcript. Parse an unambiguous yes/no response; malformed output or exhausted retries is `grading_failed`, not an incorrect answer.

Retain separate case states: `pending`, `running`, `answered`, `correct`, `incorrect`, `execution_failed`, `grading_failed`, and `cancelled`. Correct abstention is scored under the official abstention rubric, not inferred merely from an empty response. Record the failed phase, cause, attempt, and any partial artifacts for operational failures.

Reports show selected case count, answered and graded counts, every failure count, and accuracy as correct/graded overall and per category. When coverage is incomplete, prominently label accuracy provisional and show graded/selected next to it; do not present a partial run as a completed benchmark. Also show correct/selected as the observed end-to-end success fraction, with ungraded and failed cases explicitly distinguished from judged-incorrect answers.

Record learning, answering, and grading token usage and elapsed duration separately, along with tool-call counts and errors. Unknown usage remains unknown. Dollar-cost estimation, retrieval ranking scores, automated model comparisons, and no-memory baselines are deferred.

## Evidence and resume

Each run directory contains an immutable resolved manifest, per-attempt event streams, saved final answers, raw judge responses and parsed grades, case outcomes, and a derived summary. Export successful answers in the official evaluator's `question_id`/`hypothesis` shape for independent evaluation.

The manifest includes dataset/selection hashes, effective instructions and grader hashes, tool schemas, SDK versions, actual model identities/settings, product source/build identity and executable/dependency fingerprints, optional plugin/assets, limits, and relevant configuration. Never record secret values. Capture messages and outputs visible through the SDK; do not claim access to hidden model reasoning.

Write an attempt's outcome atomically only after its required evidence is saved. An interrupted event stream is preserved but cannot supply a completed outcome. Preserve prior attempts; use new directories rather than overwriting or deleting their partial memory state. Cancellation stops and awaits only child processes owned by this run.

Resume checks behavioral fingerprints rather than the config file's path alone. A mismatch is an explicit refusal requiring a new run. Reuse fully completed `correct`/`incorrect` case results. A fully saved `answered` case can resume only its tool-free grading phase; a grading failure does not require repeating memory ingestion. Execution failures, cancelled learning/answering, and interrupted cases restart from empty state. Resume never replays stored tool writes into a partially mutated workspace.

No global response cache or cross-run memory reuse is introduced. Reports are rebuilt from retained case outcomes, not from counters that can drift across resumes.

## Proof and acceptance

Automated checks must establish:

- Dataset roles, chronology, dates, abstention cases, and selected IDs are preserved; malformed records fail visibly.
- No gold answers, evidence labels, identifying source markers, or future questions reach learning. No raw history reaches answering.
- Agent and MCP process state reset between episodes; persistent memory survives within a case and never crosses cases.
- Real MCP discovery and tool execution are exercised without copied schemas or direct memory-store calls.
- Limits, truncation, tool failures, model failures, judge failures, and cancellation produce the declared outcomes.
- Interrupted cases restart cleanly; completed answers can be regraded; fingerprint changes refuse resume.
- Rubric selection, abstention handling, strict verdict parsing, aggregate counts, and official answer export match the scoring contract.

Use deterministic SDK/model test doubles for runner failure paths and a real Scrinia process for a narrow MCP integration test. Do not require external model calls for the default test suite.

The live proving run uses an explicitly recorded small set of LongMemEval-S case IDs covering ordinary recall, multi-session reasoning, temporal reasoning, knowledge updates, preferences, assistant-originated information, and abstention. Keep that set fixed before inspecting answers. Inspect traces to demonstrate actual tool use and absence of state/label leakage, then deliberately interrupt and resume a bounded run to validate recovery.

Proof means the runner completes these operations correctly and accounts for every selected case. There is no minimum accuracy target for harness acceptance. Once that proof is accepted, a full dataset run establishes the initial quality baseline. Live runs consume the user's configured inference resources and occur as an explicit execution step, not while preparing this design.

## References

- [LongMemEval dataset and evaluation contract](https://github.com/xiaowu0162/LongMemEval)
- [Official LongMemEval QA evaluator](https://github.com/xiaowu0162/LongMemEval/blob/main/src/evaluation/evaluate_qa.py)
- [Microsoft Agent Framework MCP integration](https://learn.microsoft.com/en-us/agent-framework/agents/tools/local-mcp-tools)
- Existing implementation: `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LongMemEvalLoader.cs`, `Agent/AgenticIngestHarness.cs`, `Agent/AgenticIngestPrompts.cs`, and `Judge/JudgePrompts.cs`.
