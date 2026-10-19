# Scrinia integration with LongMemEval V2

Date: 2026-10-19
Status: Design with confirmed controller choice; implementation plan requested. Product implementation and V2 inference have not begun.
Working checkout: C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia

## Purpose and scope

Evaluate Scrinia through LongMemEval-V2's official harness, reader, judge, and submission tooling. Scrinia owns memory acquisition and evidence retrieval. The benchmark owns question selection, final answering, scoring, and metric computation.

The user requested this integration and paused the original LongMemEval run. Its verified checkpoint is 135/500 graded, 107 correct, 28 incorrect, with 365 unfinished. Preserve all V1 artifacts; neither import those grades into V2 nor resume that run during this work.

Start with one operating point on the small tier, covering both domains and every required question. Medium and additional operating points follow only after the integration is proven. This tier choice is a proposed implementation decision, not a requirement supplied by the user.

## Official protocol boundary

Pin upstream commit 2cc8c540bdb87fe6761629b585e727e1c4704520, observed on 2026-10-19, and the downloaded dataset revision. Recheck the submission contract before a final run. Changing the pin requires reviewing the differences and recording a new experiment identity.

The [leaderboard contract](https://github.com/xiaowu0162/LongMemEval-V2/blob/main/leaderboard/README.md) requires paired complete web and enterprise results, a Qwen3.5-9B reader, a GPT-5.2 judge, system/code artifacts, and accuracy plus memory-query latency. Packaging validation is a necessary check, not a guarantee of organizer acceptance.

Use the upstream [run configuration](https://github.com/xiaowu0162/LongMemEval-V2/blob/main/evaluation/run_eval.py) as the reader/judge preset: reader temperature 0.6, top_p 0.95, top_k 20, thinking enabled, 20,000 completion tokens and 200,000 memory-context tokens; judge gpt-5.2, medium reasoning, 4,096 completion tokens. Do not carry over V1's 16K limit or verdict parser. Record effective flags, model identity, precision/quantization, serving configuration, hardware, and provider. Verify the endpoint actually supports images and the configured context; reject a silent server clamp or model substitution. Start reader concurrency at one and disclose it. At the pinned revision, enabled reader thinking relies on the serving default; the harness only sends an explicit thinking override on its disabled branch. Verify the server default rather than changing the official request builder.

Keep evaluation/harness.py, qa_eval_metrics.py, reader prompts, scoring, timing, and leaderboard utilities unchanged. The official [harness](https://github.com/xiaowu0162/LongMemEval-V2/blob/main/evaluation/harness.py) supports shared and distinct histories, save/load for shared histories, cancellation, and a timed query boundary. Use one prompt-building worker initially. Do not add Scrinia to upstream parallelism allowlists as part of the first integration.

## Architecture

Use a Python memory adapter and a separate .NET worker. This preserves the existing SDK tool loop without replacing the official evaluator.

1. A small Python launcher registers Scrinia, materializes inputs using upstream helpers, and enters the official harness.
2. The adapter implements the upstream Memory interface.
3. A .NET worker runs learning/retrieval episodes through Microsoft.Agents.AI with the real Scrinia stdio MCP server.
4. The adapter returns validated text/image evidence.
5. The official reader produces the answer; the official evaluator scores it and writes submission artifacts.

The [Memory contract](https://github.com/xiaowu0162/LongMemEval-V2/blob/main/memory_modules/memory.py) requires insert(trajectory), query(question, query_image), and registration under a distinct memory_type. Implement save/load and runtime cancellation hooks too. The bridge receives only inserted trajectories, question text, optional question image, and opaque invocation identifiers. Gold answers, question IDs/categories, evaluator configuration, and the complete question file never enter the worker protocol or agent context.

Keep the adapter outside the upstream checkout. Import it to register memory_type scrinia, then invoke the official harness in-process with the normal arguments. Use upstream materialization functions to create runtime_inputs rather than reimplementing selection. The upstream launcher has a fixed method list, so do not masquerade as codex or patch scoring to obtain registration.

One worker process handles one operation and exits, owning its MCP child. Commands are learn, retrieve, and validate-snapshot, with versioned JSON input/output files and separate event logs. This simple ownership model avoids an idle daemon without a close hook. Query timing includes process startup, MCP startup, retrieval, retries, evidence materialization, and shutdown. Do not shift query work into constructors or post-query hooks.

Reuse the existing agent/model client, observation, cancellation, and MCP ownership code through a narrowly extracted runtime library. Preserve V1 callers and tests. New V2 episodes accept explicit instructions, tools, and multimodal messages; they must not accidentally select AgentInstructions.Answer or the V1 grader. Avoid creating the previously discussed generic benchmark product in this project.

## Memory construction and isolation

insert receives only the trajectory supplied by the harness. Normalize it with the pinned upstream trajectory helper. Preserve goal, outcome, URL, ordered actions, accessibility text, state indices, original steps, and screenshot references. The [trajectory utilities](https://github.com/xiaowu0162/LongMemEval-V2/blob/main/memory_modules/trajectory_store.py) already handle public/legacy trajectory forms and screenshot resolution; the adapter adds content hashes and ownership checks.

Maintain an immutable source store for inserted trajectories only. Seed source text into Scrinia as independently retrievable, source-addressed chunks so learning summaries never become the sole surviving evidence. Let the learning agent add descriptions, findings, corrections, and procedure notes through public MCP tools. Every derived note references its source trajectory and state range. The agent receives no shell or arbitrary filesystem tools.

Partition oversized trajectories in source order using a frozen token budget, retaining state identifiers and explicit fragment labels. Never silently drop an oversized state or screenshot. Tokenization, partition limits, image limits, model input limits, and prompts must be explicit configuration validated before indexing; synthetic tests determine safe defaults before freezing the experiment.

For the first version, publish each successful trajectory insertion atomically: stage a copy of the previous complete workspace, perform the insertion, validate evidence and source references, then replace the active snapshot. Keep the previous snapshot on cancellation or failure. An insertion ledger binds ordinal, trajectory content and image hashes, configuration, and resulting snapshot hash. Identical committed insertions are idempotent; conflicting content is an error. Do not replay a potentially committed remember call after an uncertain MCP completion.

Each Memory instance gets a unique workspace/home/evidence directory under a configured run root. Shared histories reuse their completed snapshot; distinct histories never see one another. Query work uses fresh conversations and only read operations: guide/restore plus search, list, recall, and facts as needed. No remember, link, forget, or compaction during retrieval. Hash authoritative memory state before/after retrieval to detect unintended mutation; exclude declared derived caches from that comparison.

No cross-question learned strategy, answer cache, or grade feedback in this initial method. Save/load bundles the committed memory, source catalog, required image assets, insertion ledger, and configuration fingerprints. Loading rejects mismatches and missing evidence. Resume indexing only against an identical ordered history and frozen configuration, never by question identity.

## Images and evidence

Support images as a first-class transport capability, even though Scrinia stores text. Store image hashes and stable source references beside the memory; do not encode screenshot bytes into memory prose. Resolve only references belonging to the inserted history. Return selected original screenshots together with associated source text to the official reader.

The retrieval controller receives the question image when present. Learning receives the source screenshots under the same frozen batching policy as trajectory text. Derived descriptions of images are marked as such and remain linked to originals. A structured evidence selection contains memory paths/chunk indices and source-state references; the adapter verifies them and materializes the actual content. The controller cannot inject arbitrary filesystem paths or substitute invented quotes.

Do not make the controller's final answer the reader's context. Return retrieved notes and verifiable source evidence, with dated disagreements intact. Evidence ordering and any method-level selection limit are frozen; the official harness still applies its own context budget. An empty search is valid empty evidence, while missing files and invalid references are integration errors.

The user selected Qwen3.5-9B with image support for the internal learning/retrieval controller, served as unsloth/Qwen3.5-9B-MTP-GGUF:BF16 at https://ai-srv-md.saga-augmented.ts.net/v1. Trim the supplied trailing whitespace from the model ID. Use temperature 0.6, top_p 0.95, top_k 20, and thinking enabled as the initial controller preset. The same deployment is the proposed reader endpoint, with the separate official reader settings above; judge remains GPT-5.2. Record the MTP/BF16 deployment explicitly. Model availability was supplied by the user; actual image handling, reported identity, and context capacity still require live preflight. Do not silently omit images or add a captioning model.

## Measurement and recovery

The official harness owns final answers and scores. The memory module never sees the grade and never selects a retry because an answer was incorrect.

Retain tested controller recovery behavior: at most five total provider attempts per logical request, shared by transport and response recovery. The one allowed response retry, shared by reasoning-loop detection and truncated, undispatched tool-call recovery, consumes the next attempt from those same five; it never resets the allowance. Every attempt also consumes one of the episode-wide 40 model requests. Transport backoff uses the failed attempt ordinal (5, 10, 20, then 40 seconds); a response recovery has no added backoff. Retries preserve the request and remaining budget. Enable these policies explicitly for V2 learning and retrieval rather than relying on the V1 phase name string answer. Never redispatch uncertain mutations. Keep raw tool-call arguments, parse failures, detector evidence, timing, model identity, and attempt counts; exclude credentials.

Initial controller limits are proposed as 16,384 output tokens, a 180-second SDK network timeout for each individual provider attempt, 300 seconds wall time per bounded learning batch/retrieval episode including all attempts and backoff, and 40 model requests including retries. The episode deadline and explicit user cancellation always take precedence; 180 seconds is not a fresh logical-request budget spanning all attempts. These are method settings, not V2 protocol requirements. Validate them with synthetic workloads before freezing; trajectory-level duration is derived from the number of batches rather than V1's case deadline.

Recovery exhaustion, evidence corruption, missing images, or cancellation fail explicitly; do not report them as successful empty retrieval. A scoring run that aborts remains incomplete. Preserve its artifacts and distinguish an operational restart from a reportable completed run. No custom merging of partial graded results or replacing the official reader/judge retry policies.

All retrieval retries and evidence work occur inside memory.query. Build time, controller tokens/cost, storage, failure counts, and recovery counts are supplemental metrics; report them alongside, without substituting for, the official score.

## Reproducibility and acceptance

Freeze upstream/data revisions, source and binaries, MCP catalog and guide, all prompts, controller/reader/judge configurations, recovery limits, tokenization, image policy, source ordering, and memory snapshots. A change produces a new experiment identity. Credentials are environment references only, separately scoped for controller, reader, and judge.

Develop with synthetic web/enterprise-style histories and fabricated questions, not real V2 answer inspection. Tests include dated corrections, ambiguous premises, multi-step workflows, image-only facts, long accessibility trees, shared-history isolation, source-reference fidelity, endpoint failures, uncertain writes, and cancellation. Any real V2 question inspected during development is logged as exposed; a later full run does not erase that exposure.

Validation proceeds in this order:

1. Offline Python contract tests with a fake worker and tiny synthetic dataset; validate registration, both lifecycle modes, images, persistence, and input isolation.
2. Offline .NET tests with scripted providers and the real MCP transport; preserve existing V1 regression coverage.
3. End-to-end synthetic harness execution, including the unchanged reader request builder, scorer interfaces, failure behavior, and artifact layout.
4. Separately authorized live synthetic proof using the selected controller, actual Qwen3.5-9B reader, and actual GPT-5.2 judge. Verify identity, image handling, budgets, and request capture. Estimate build/query cost before a full run.
5. Freeze the complete method, run all required small-tier questions in both domains, and validate the official package. Do not tune against failures mid-run.
6. Prepare SYSTEM_DESCRIPTION and a single reproducible source archive for the upstream package builder, including adapter, worker sources, dependency locks, source provenance, and build instructions. Check organizer expectations for a multi-file .NET method if the required code artifact format remains ambiguous. Submission itself is a separate user action.

## Delivery sequence and proposed files

This is a design-level work breakdown; the detailed test-first implementation plan follows design approval.

- Python adapter and launcher: benchmarks/longmemeval-v2/scrinia_memory.py, bridge.py, run_scrinia.py, upstream.lock.json, tests/test_memory_contract.py.
- .NET worker: benchmarks/Scrinia.LongMemEvalV2.Worker/ with Protocol, Learning, Retrieval, and Evidence components; tests/Scrinia.LongMemEvalV2.Worker.Tests/.
- Reusable runtime: benchmarks/Scrinia.Agent.Runtime/ containing only the shared SDK/MCP/observation seams; migrate existing runner references without changing V1 behavior.
- Snapshot/evidence validation and configuration: colocated with their owning adapter/worker component, with fake-provider tests for cancellation and retry ambiguity.
- Experiment presets, freeze manifest, offline fixtures, reproducible packaging, and operator guide under benchmarks/longmemeval-v2/.

The builder may choose internal type names and file splits, Python test tooling, and atomic file helpers consistent with the repo. Do not introduce a new public Scrinia tool solely for this adapter.

## Decisions needed before live execution

Verify the selected Qwen3.5-9B deployment supports images and the required context. Supply a GPT-5.2 endpoint/credential reference; BENCH_JUDGE_API_KEY is not assumed to authenticate OpenAI. Confirm available capacity, estimated cost, and run authorization before live evaluation.

No dataset download, dependency installation, product code changes, live model calls, or benchmark submission occurred while preparing this design.

## Builder review

A fresh builder review confirmed the external registry approach and found no additional architecture blocker. Its retry-budget question was resolved against ObservedChatClient.cs and ModelClientFactory.cs: shared attempt count, per-attempt network timeout, and unchanged episode deadline are now explicit above. The user subsequently selected Qwen3.5-9B with image support and asked to continue planning. No implementation has begun.
