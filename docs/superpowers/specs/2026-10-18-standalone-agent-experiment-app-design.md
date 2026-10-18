# Standalone agent experiment application

Date: 2026-10-18

Status: Design proposal based on the approved conversational sketch; implementation is not authorized by this document. Working name: Agent Experiment App. No final product name has been chosen.

## 1. Intent

Provide a standalone application that executes agent experiments from portable packages. Users supply instructions, tools, data, execution steps, measurement rules, and recovery policies. The application owns model execution, tool lifecycle, durable evidence, limits, recovery, resumability, and reporting.

The central abstraction is an experiment: what is being measured, under which conditions, and how observations become measurements. Generic means that the experiment can define the meaning of failures and the permitted response to them. It does not mean that every benchmark uses the same success criterion or recovery behavior.

The product serves tool developers, agent developers, and evaluators who currently build substantial execution infrastructure around individual benchmarks. Its potential value includes delivery, setup effort, local operation, understandable diagnostics, and total operating cost, as well as features.

The existing Scrinia benchmark is the source of operational experience and a first integration candidate. The application is a separate product with no core dependency on Scrinia, LongMemEval, or memory-specific phases.

**Current operational constraint:** leave the active LongMemEval benchmark running. This design work neither changes its frozen runtime nor starts experiments, migrates its evidence, or modifies its grades.

## 2. Decisions and proposed defaults

### Direction established with the user

- Standalone desktop application and CLI share one execution engine.
- Experiment packages supply the workflow and make measurement and recovery first-class, visible configuration.
- Users can use local or hosted models and attach MCP tools.
- State boundaries, independent evaluation, bounded recovery, durable evidence, and linked continuations are explicit.
- Different experiments may treat the same detected event as a measured failure, an invalid attempt, or an interruption.
- Delivery and cost matter independently of whether another framework has comparable features.

### Design calls proposed here

- Use a self-contained .NET host and the standard .NET agent SDK for the initial engine; retain SDK-owned tool dispatch. Windows is the initial desktop release target. Keep the engine and CLI portable for subsequent Linux/macOS validation.
- Use a versioned YAML manifest, JSONL case records, ordinary prompt files, and optional fixtures. A directory is the authoring format; an archive is the distribution format.
- First release executes local processes and local filesystem state. Hosted model endpoints are supported. Remote MCP services require declared state semantics; arbitrary remote state cannot be assumed resettable.
- Start with sequential case execution. Specify bounded concurrency as a later engine capability, without making it necessary for first-release completeness.
- Build a usable desktop workflow rather than requiring users to write .NET code. Provide extension contracts for benchmarks that exceed built-in capabilities.

The desktop toolkit, application name, packaging technology, and detailed visual styling remain builder decisions. License, commercial support, and any future paid hosting are separate product decisions before distribution; do not infer them from this repository's license. No license fee, pricing tier, or cloud service is promised by this design.

## 3. Product experience and delivery

Install the app, open an experiment package, bind its logical model/tool roles to local profiles, review the resolved experiment, and start a run. The app validates prerequisites and identifies missing dependencies before execution. The host includes its own runtime; MCP servers and benchmark-specific environments can still require separately installed software.

The desktop application has four primary views:

| View | Responsibilities |
| --- | --- |
| Experiment | Objective, cases, steps, state boundaries, model/tool bindings, measurement rules, recovery rules, and limits. Edit supported manifest fields through forms; retain a source view for advanced fields. |
| Run | Progress, current case/step, completed measurements, interruptions, recovery actions, usage, pause/resume, and stop. Show measurement coverage beside accuracy. |
| Investigate | Timeline of inputs, available provider-visible reasoning, responses, tool calls/results, observations, policy decisions, checkpoints, and evaluator evidence. No assumption that a provider exposes internal reasoning. |
| Compare | Compare resolved conditions, cohorts, scores, coverage, reliability, usage, and timing. Explain changed conditions before presenting pooled numbers. |

Before execution, show a plain-language summary of the resolved policies. For example: “Measure answer accuracy after learning historical conversations. Preserve tool files within each case, reset conversation context between sessions, retry a detected reasoning loop once, and score incorrect answers without retry.”

The CLI exposes equivalent validate, run, status, pause, resume, continue, inspect, and export operations. Command names are provisional. Desktop and CLI must not implement separate policy logic. A local worker owns execution so closing the desktop window does not silently kill a run. Both interfaces attach to that worker through a local control interface. A run has one writer/owner.

No mandatory account or vendor cloud is required for local execution. Local inference still incurs hardware and operating costs; hosted inference and optional external services retain their provider charges. The application reports measured usage and optionally estimated price using an identified price table. Unknown usage or price remains unknown, never zero.

## 4. Package and local binding boundary

```text
experiment-package/
  experiment.yaml
  README.md
  data/cases.jsonl
  prompts/learn.md
  prompts/answer.md
  evaluators/answer-rubric.md
  fixtures/initial-state/
```

The package is portable and treated as immutable input to a run. Outputs live in a separate run directory. Relative references resolve within the package. Dataset artifacts may be supplied separately for licensing or size reasons, but must be bound to an expected digest before execution. The package identifies its own license and any separate dataset/fixture requirements.

Case records have stable unique IDs, agent-visible inputs, evaluator-only references, and metadata. Execution steps cannot reference evaluator-only fields. The evaluator receives explicit captured outputs and reference fields; it does not inherit the agent's conversation or tool access by default. Packages may explicitly request trajectory or final-state evaluation.

Bindings are local profiles mapping logical roles to endpoints, model IDs, MCP launch commands or URLs, credential references, and storage locations. Secrets never appear in portable packages or exported resolved manifests. Nonsecret effective settings, component versions, tool catalogs, and available identities do appear in the run fingerprint. Provider aliases do not prove unchanged model weights; record that limitation.

Opening a package parses and validates it without executing bundled code. Execution requirements and declared commands appear in the run review. Extensions execute as explicit processes with declared inputs, outputs, deadlines, and capabilities. A local process is not advertised as a security sandbox. Arbitrary containers, remote fleets, and a package marketplace are outside the initial release.

## 5. Execution and state model

The hierarchy is experiment definition → resolved run → case → case attempt → step/episode → model request/tool invocation. Evaluations and measurements have their own IDs linked to the evidence they assess. A recovery request is not a new case or an extra scored sample.

Initial built-in steps are agent episode, bounded foreach, fixture initialization/reset, checkpoint, and evaluation. Foreach iterates a finite case input collection; the manifest is not an unrestricted scripting language. Steps have stable IDs and named captures. Custom adapters transform external datasets into case records; custom process extensions supply unusual evaluation or lifecycle behavior.

Three state lifetimes are independent:

1. Conversation context: fresh, carried between named steps, or restored from a checkpoint.
2. Tool process/connection: per episode or per case.
3. Tool environment state: isolated workspace/files, external resources, or fixture state.

Each case starts from its declared fixture. Cases never share mutable state by accident. Tool state can persist while conversation context is reset. A persistent MCP process can also retain state beyond its files; the lifecycle specification must account for it.

Checkpoints must cover every state component required for the permitted replay, including conversation, captured outputs, tool files, and process/external-state restoration where applicable. Initial checkpoint support covers local filesystem workspaces with no active writes, plus saved conversation context and captured outputs. In-memory or remote state requires a lifecycle adapter capable of restoring it; otherwise checkpoint-based replay is unavailable. Validate unsupported recovery actions before running.

A completed tool call is never replayed as part of retrying the following model request. A tool timeout or transport disconnect can follow a committed mutation. Such a call is uncertain, not automatically failed; repeating it requires an idempotency/reconciliation contract or restoration of a known state. No configuration can bypass this integrity constraint.

## 6. Measurement is an explicit contract

The measurement contract declares the subject, objective, unit of measurement, evaluator, admissibility rules, and metric aggregation. A subject may be a model, tool, agent configuration, or complete system. These labels describe intent; they do not automatically assign fault or turn recovery on.

Execution and measurement status are separate:

| Execution status | Meaning |
| --- | --- |
| Pending / running | Work has not started or is in progress. |
| Paused / interrupted | Work can potentially continue under the same conditions. |
| Finished | The declared execution or terminal policy has completed. |
| Needs review | Evidence or state is ambiguous; automatic continuation is prohibited. |

| Measurement status | Meaning |
| --- | --- |
| Pending | No terminal measurement decision yet. |
| Valid | An admissible score exists; this can be success, failure, or a continuous value. |
| Unmeasured | Policy exhausted or terminated without an admissible measurement. |
| Needs review | Admissibility or evaluation could not be resolved. |

An evaluator protocol failure is distinct from an evaluator returning a low score. Under the example tool-efficacy policy, a completed incorrect answer is a valid measurement of failure. Its incorrectness does not justify a retry.

Admissibility decisions must identify the policy and supporting observation. A reasoning-loop detector establishes repetition, not proof that the tool played no causal role. Experiments may exclude that generation from the primary efficacy measurement, while still reporting loop frequency by tool/version. Conclusions remain conditional on the chosen model, instructions, sampling, and recovery policy.

For a single binary measurement per selected case, accuracy is successful valid measurements / all valid measurements. Coverage is cases with valid measurements / selected cases. End-to-end success is successful valid measurements / selected cases. Pending, unmeasured, and review counts accompany these values. An empty denominator yields unavailable, not zero. Continuous metrics explicitly declare their reducer and valid population.

Replicates are declared before execution with stable replicate IDs. Retries are attempts within a replicate, not additional replicates. Accept the first admissible result produced by the declared policy, never the highest-scoring attempt. Preserve every attempt and allow analysis of retry frequency and performance differences. Statistical confidence methods may be added later; no automatic claim of causal tool superiority is made from a single uncontrolled score.

## 7. Detection, interpretation, and recovery

These are separate contracts:

- **Detector:** emits a versioned observation with evidence references, scope, and parameters. Examples: endpoint unavailable, output truncated, repetitive reasoning, tool returned error, uncertain tool completion, evaluator response invalid.
- **Interpretation:** classifies the observation for this experiment, such as invalid generation, infrastructure interruption, measured failure, or uncertain state.
- **Recovery:** selects a bounded action and an exhausted disposition. Actions include retry request, restore and repeat episode, restore and repeat case, pause run, stop attempt, or none.

Profiles provide explicit starting configurations for tool efficacy, model reliability, or system reliability. They are expanded and versioned into the resolved manifest. Profile names never hide effective rules.

All observations produced by detectors for one completed response or execution transition form one decision batch. Finish detector evaluation and record that batch before choosing an action. Match the ordered rules once against the whole batch and select the first matching rule; execute at most one recovery or terminal action for that batch. For example, put the specific repeated-reasoning rule before a general output-truncation rule: a response containing both observations receives the loop rule's action, and the truncation observation cannot separately terminate it. Record every observation and the selected rule, including observations that did not determine the action. Separate later transitions receive separate decisions.

Validation rejects duplicate rule IDs and unreachable exact duplicates; the UI displays effective order and provides a preview for representative observation batches. Before rule matching, integrity gates override recovery: cancellation/deadline enforcement, failed durable evidence, uncertain tool state, identity drift, and unavailable required checkpoints prevent unsafe replay. The most restrictive applicable integrity gate wins. An abnormal batch with no matching rule stops the affected attempt as unmeasured with an explicit reason; ordinary tool error results remain available to the agent unless a policy says otherwise. Required-detector failure is itself an abnormal observation and cannot silently omit a validity check.

Each rule specifies its counter scope and maximum retries. Request recovery counters reset for a new logical model request; episode and case replay counters persist across their attempts. All actual requests and attempts consume declared cumulative budgets. Recovery does not reset deadlines, token budgets, or costs. A restart reloads counters from evidence. An operator can grant additional budget only through a linked continuation with changed conditions.

Request retry preserves the pre-request history and settings and excludes the discarded completion. It is permitted only when no tool invocation from that completion was dispatched. Restore-and-repeat actions require a committed checkpoint and verified restoration before execution. An action cannot silently fall back to a broader replay scope.

The initial detector can reuse the current runner's logic: identify substantial paragraph repetition in reasoning-only responses that exhausted the output limit. Detection happens after a nonstreaming response completes. Early streaming detection and model-specific detectors are later capabilities. No assumption that all models expose reasoning or use identical response formats.

## 8. Illustrative manifest

This is a proposed contract sketch, not a published schema. Required defaults such as state lifetimes, budget accounting, rule scope, and evaluator visibility must be expanded in the resolved run.

```yaml
schema: experiment/v1
id: memory-tool-efficacy
measurement:
  subject: tool:subject
  objective: Answer accuracy after learning historical conversations
  unit: case
  primary_metric: answer_accuracy
cases:
  source: data/cases.jsonl
  id_field: id
models:
  actor: {binding: local-agent}
  evaluator: {binding: local-judge}
tools:
  subject: {binding: memory-server, protocol: mcp}
state:
  conversation: fresh_per_episode
  tool_process: per_episode
  tool_storage: persist_within_case
  between_cases: reset_to_fixture
  fixture: fixtures/initial-state
steps:
  - id: learn
    foreach: case.inputs.sessions
    agent:
      model: actor
      instructions: prompts/learn.md
      input: item.transcript
      tools: [subject]
  - id: answer
    agent:
      model: actor
      instructions: prompts/answer.md
      input: case.inputs.question
      tools: [subject]
    capture: answer
  - id: grade
    evaluator:
      kind: model_rubric
      model: evaluator
      rubric: evaluators/answer-rubric.md
      input: captures.answer
      reference: case.references.expected_answer
policies:
  - id: loop-recovery
    on: repeated_reasoning/v1
    scope: model_request
    interpretation: invalid_generation
    recovery:
      action: retry_request
      maximum_retries: 1
      require: no_tools_dispatched_from_completion
    exhausted: unmeasured
  - id: endpoint-recovery
    on: endpoint_unavailable/v1
    scope: model_request
    interpretation: infrastructure_interruption
    recovery:
      action: retry_request
      maximum_retries: 4
      backoff_seconds: [5, 10, 20, 40]
    exhausted: pause_run
limits:
  model_request_seconds: 180
  episode_seconds: 300
  case_active_seconds: 3600
  model_requests_per_episode: 40
  attempts_per_model_request: 5
report:
  metrics: [answer_accuracy, measurement_coverage,
            interruption_rate, recovery_success_rate,
            total_tokens, elapsed_time]
```

Model-reliability experiments can replace loop recovery with a measured-failure rule and no retry. Such a rule must name the measurement and explicit failure score; interpreting an event as failure does not invent a score for an arbitrary evaluator. Full-system experiments may permit recovery and include its overhead in the primary success criterion.

## 9. Budgets, pause, and worker lifecycle

Specify request timeouts, active episode/case limits, request/attempt ceilings, optional token ceilings, and an optional run-wide elapsed deadline. In-flight requests are bounded even while a pause is pending. Provider hidden retries must be disabled or accounted for explicitly.

Graceful pause stops scheduling new work and pauses at the next declared safe boundary. A separate “interrupt now” command cancels active work and records whether a checkpoint supports resume; uncertain tool state becomes needs review. Paused time is excluded from active-work limits but included in reported wall-clock elapsed time and any explicitly selected absolute deadline. Active-work consumption is cumulative across recoveries and worker restarts.

Cancellation, process termination, or machine restart preserves completed evidence. Resume validates the frozen configuration and available state. Where in-flight continuation cannot be established, apply the declared interruption policy rather than assuming the last request never happened.

## 10. Evidence, continuation, and comparison

A run bundle contains the resolved manifest, input/component hashes, environment metadata, append-only event records, checkpoint references, captures, evaluator outputs, measurement decisions, and derived reports. Evidence references connect every observation, decision, attempt, and score. Durable event ordering precedes actions that depend on that event. A corrupted or incomplete tail cannot be silently promoted into a committed outcome.

Capture provider-visible request/response and tool evidence subject to a declared recording policy. Credential fields and authentication headers are excluded. Tool content can contain sensitive user data; local evidence is private by default and exports support explicit redaction. Record omitted fields so reduced evidence is not presented as complete.

Exact resume keeps experiment identity and counters. Changing prompts, sampling, tools, evaluators, detectors, recovery policies, or effective budgets creates a linked continuation. The app previews which completed measurements would be retained and which unfinished cases would be rerun. Retention is explicit and does not imply scientific equivalence between conditions.

Linked reports count each selected case/replicate once under the user's retention selection, preserve all source evidence and cost, and show cohorts separately. A pooled figure is labeled mixed-condition. Regrading creates a new evaluation record against preserved captures; it never overwrites an original grade. Manual review decisions append a reason and provenance rather than deleting observations.

Compare reports expose differences in model, tool, prompts, dataset, evaluator, detector, policy, and budget. Fully matching conditions can be grouped automatically; differing conditions require explicit comparison and clear labels. Unknown model identity and missing usage remain visible limitations.

## 11. Architecture and extension boundaries

- **Package compiler:** validates manifests, case IDs, typed references, evaluator-only fields, rule scopes, budgets, required capabilities, and immutable inputs; resolves profiles into an execution specification.
- **Engine:** schedules steps, tracks state and counters, runs the standard agent SDK, and enforces integrity gates.
- **Model and tool adapters:** implement provider calls, MCP lifecycle, and observation without embedding benchmark scoring.
- **Measurement service:** runs evaluators, determines admissibility through policies, and computes declared metrics.
- **Recovery service:** selects and executes permitted actions using checkpoint and lifecycle capabilities.
- **Evidence store:** journals events and committed outcomes, fingerprints inputs, and validates resume/continuation.
- **Local worker/control interface:** coordinates exclusive run ownership and desktop/CLI operations.
- **Desktop, CLI, and report renderer:** share the compiled specification and engine services.

Custom evaluator, detector, and lifecycle extensions use a versioned process protocol with structured input/output and explicit timeout/error behavior. Pin executable artifacts and protocol versions in run evidence. Extension exceptions become typed observations, not successful empty output. The builder chooses storage encoding and IPC details while preserving these contracts.

LongMemEval-specific dataset mapping, learning instructions, session iteration, and rubric live in its package/adapter. Scrinia-specific workspace setup and preflight guide calls live in its tool binding/lifecycle adapter. The generic engine contains no Learn/Answer enum, required guide tool, or assumption that all measurements are binary answer grades.

## 12. Initial scope and acceptance criteria

The first release includes the desktop workflow, equivalent CLI, local worker, package validation, model/MCP bindings, sequential multi-step execution, local fixture isolation, measurement/recovery policies, supported checkpoint replay, durable resume, linked continuations, and local reports/exports. A LongMemEval package exercises the real integration. Small synthetic packages exercise different interpretations of the same event; a second production benchmark is useful validation, not a prerequisite for accepting the product direction.

Defer distributed scheduling, hosted accounts/billing, marketplace discovery, arbitrary browser/computer benchmark environments, universal remote-state snapshots, model training, and a general visual workflow programming language.

Acceptance evidence must demonstrate:

1. The same observation yields a bounded retry in a tool-efficacy package and a valid failure score in a model-reliability package without engine code changes.
2. A completed incorrect answer is retained; retry policy cannot silently select a better grade.
3. Recovering a model request preserves prior successful tool calls and all usage; uncertain mutations cannot be automatically replayed without the required capability.
4. Checkpoint replay restores all declared state or refuses to continue. Independent conversation/process/storage lifetimes behave as specified.
5. Crashes, pauses, resumes, and repeated invocations cannot duplicate committed measurements or reset consumed budgets.
6. Linked continuation retains selected completed measurements, retries unfinished cases, and visibly separates changed conditions.
7. Invalid evaluator output is distinguishable from a valid negative grade. Agent execution cannot access evaluator-only references through the package binding API.
8. Desktop and CLI produce equivalent resolved specifications and outcomes for deterministic fixtures.
9. Reports expose valid score population, coverage, unmeasured/review counts, recovery overhead, and unknown usage.
10. A clean supported host can install and run a packaged experiment with documented external prerequisites and no mandatory service account.

## 13. Existing alternatives and positioning

Inspect AI is an MIT-licensed Python evaluation framework with local execution, MCP integration, configurable error handling, and resumption. Harbor is an Apache-2.0-licensed framework with local Docker and optional cloud sandbox execution. Both already make substantial execution infrastructure reusable. Free/self-hosted operation and retry support alone are not established differentiators.

The proposed value is an accessible standalone application and portable experiment contract that exposes measurement validity and recovery semantics alongside ordinary execution settings. Lower setup/maintenance effort, local-model ergonomics, inspectable evidence, and delivery may matter as much as feature novelty. These are product hypotheses to evaluate, not claims that existing frameworks lack equivalent extension capabilities.

Sources checked during the design discussion on 2026-10-18:

- [Inspect repository and MIT license](https://github.com/UKGovernmentBEIS/inspect_ai)
- [Inspect error and recovery policies](https://inspect.aisi.org.uk/handling-errors.html)
- [Inspect MCP integration](https://inspect.aisi.org.uk/tools-mcp.html)
- [Harbor repository, installation, local/cloud execution, and Apache-2.0 license](https://github.com/harbor-framework/harbor)
- [Harbor task packaging](https://docs.harborframework.com/tasks/overview)

## 14. Planning handoff

This document specifies the proposed product and contracts, not implementation tasks. After design approval, create an implementation plan for a separate application/repository boundary. Decide the delivery toolkit and distribution/license choices before release work. Reuse existing runner code only after checking its coupling and license; do not destabilize or replace the active benchmark as part of extraction.

Keep a record of design assumptions and builder-check findings with this document. Do not interpret approval to write the design as approval to implement, migrate active runs, change sampling, or launch comparison experiments.

### Builder check

A fresh software-engineering reviewer found one material ambiguity: multiple observations from one response could select conflicting actions. Section 7 now defines one observation batch and one ordered policy decision per completed response/transition, with integrity gates taking precedence. This is an author-resolved engine semantic, not an additional user requirement. The reviewer found no other questions requiring clarification before planning.

The [UX polish review](../../reviews/ux-polish/2026-10-18-agent-experiment-design.md) identified one misleading retry sentence and two jargon-heavy phrases. All three were corrected during document preparation; the original review is preserved.
