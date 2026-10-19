# LongMemEval V2 Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Run Scrinia as a multimodal memory backend under the unchanged official LongMemEval-V2 evaluator and produce a reproducible submission package.

**Architecture:** A registered Python Memory adapter invokes an owned .NET worker for learning and retrieval. The worker uses the existing Microsoft agent SDK and real Scrinia MCP tools. The official Python harness retains final answering, grading, timing, and reporting.

**Tech Stack:** .NET 10; existing Microsoft.Agents.AI.OpenAI 1.24.0 and ModelContextProtocol 2.2.0; Python 3.11; upstream LongMemEval-V2; existing xUnit test conventions; Python unittest for adapter tests.

**Spec:** ../specs/2026-10-19-longmemeval-v2-integration-design.md

## Global Constraints

- Keep V1 paused. Preserve its frozen binaries, configuration, stores, event streams, grades, and linked reports. Never run a live V1 test as a regression check.
- Pin upstream 2cc8c540bdb87fe6761629b585e727e1c4704520. Pin dataset and model processor revisions before real data use; never use a moving revision in a frozen experiment.
- Keep official harness, reader prompts, metrics, judge parser, and submission utilities unchanged. Register the external adapter before invoking the harness in the same Python process.
- Internal controller: unsloth/Qwen3.5-9B-MTP-GGUF:BF16; endpoint https://ai-srv-md.saga-augmented.ts.net/v1; image support required. Initial sampling: temperature 0.6, top_p 0.95, top_k 20, thinking enabled.
- Reader: same Qwen deployment proposed, independently configured with the upstream preset: thinking enabled, temperature 0.6, top_p 0.95, top_k 20, 20,000 output and 200,000 memory-context tokens. Judge: gpt-5.2, medium reasoning, 4,096 output tokens. No V1 16K reader override or V1 verdict parser.
- Initial controller: 16,384 output tokens; 180-second network timeout per attempt; 300-second episode wall time; 40 total model requests; five attempts per logical request shared across transport and one response recovery. Existing cancellation semantics and 5/10/20/40-second transport backoff stay intact.
- One prompt worker and reader concurrency one initially. Small tier, both domains, one operating point. No online learning from queries or cross-question conversations.
- All query startup, cleanup, evidence assembly, and retries occur within memory.query timing.
- Real V2 answers/cases are not development fixtures. Track any accidental exposure. Never combine grades across changed method versions.
- Use source-generated JSON for .NET protocol and runtime types. Preserve unrelated dirty working-tree files. All shell commands use rtk; examples below assume checkout root.
- Keep credentials outside source, logs, packages, and protocol payloads. Configuration contains environment-variable names only. No live inference or submission as part of offline implementation.

## Review Focus

1. A shared memory is queried twice: the second query must not see the first question, answer, or scratch state. Test in Tasks 4 and 5.
2. A tool commits but its response is lost: never repeat the mutation or publish uncertain learning. Test in Tasks 1 and 3.
3. A screenshot path changes, escapes its root, or is missing: reject it rather than silently returning text-only evidence. Test in Tasks 2 and 4.
4. Cancellation occurs during model backoff or child shutdown: stop owned processes and retain an auditable incomplete result. Test in Tasks 1, 2, and 5.
5. A server ignores images, thinking configuration, model selection, or context capacity: fail live preflight before scored evaluation. Test request construction offline in Task 6 and actual behavior in its separately authorized live stage.

## File and dependency map

- benchmarks/Scrinia.Agent.Runtime/: shared SDK episode execution, model observation/recovery, MCP ownership, small runtime contracts and JSON context.
- benchmarks/Scrinia.Agent.Benchmarks/: existing V1 dataset, prompts, runner, grading, manifests, and reports remain here; callers delegate to the shared runtime.
- benchmarks/Scrinia.LongMemEvalV2.Worker/: versioned file protocol, V2 instructions, source-backed learning and retrieval, evidence validation.
- benchmarks/longmemeval-v2/: Python registration, worker ownership, snapshot lifecycle, launcher, presets, freeze/package utilities, synthetic fixtures.
- tests/Scrinia.LongMemEvalV2.Worker.Tests/: worker contracts, scripted providers, real-MCP integration.
- Existing tests/Scrinia.Agent.Benchmarks.Tests/: V1 compatibility gate.

Tasks execute in order. Do not move the V1 grader or dataset into the shared library.

### Task 1: Extract a behavior-preserving SDK and MCP runtime

**Files**
- Create benchmarks/Scrinia.Agent.Runtime/Scrinia.Agent.Runtime.csproj, RuntimeContracts.cs, RuntimeJsonContext.cs, RuntimeAgentEpisode.cs, RuntimeEnvironment.cs, Properties/AssemblyInfo.cs.
- Move Agent/ModelClientFactory.cs, ObservedChatClient.cs, ModelRequestRecovery.cs, ReasoningLoop.cs, EpisodeBudget.cs and Runtime/ScriniaMcpSession.cs, OwnedMcpProcess.cs, ToolCatalog.cs from the V1 project into the runtime, preserving file basenames.
- Extract InputDiagnostics, UnixPathType, and PathSafety from V1 Configuration into the runtime. Move ModelSettings, RunLimits, ScriniaSettings, AttemptPaths, UsageTotals, IRunEvents, BenchmarkFailureCode and BenchmarkFailureException into RuntimeContracts.cs; preserve their existing namespace/signatures for V1 source compatibility.
- Modify V1 Agent/AgentEpisode.cs, Configuration/RunConfiguration.cs, Configuration/ConfigurationReader.cs, Recording/RunRecords.cs, Runtime/AttemptWorkspace.cs, Runtime/CaseRunner.cs, project references and Scrinia.sln.
- Test existing AgentEpisodeTests.cs, ObservedChatClientTests.cs, ReasoningLoopRecoveryTests.cs, TruncatedToolCallRecoveryTests.cs, ScriniaMcpSessionTests.cs; add RuntimeEpisodeTests.cs in the V1 test project.

**Interfaces**
- Preserve IAgentEpisode and existing V1 EpisodeInput/EpisodeResult via a thin V1 facade.
- New RuntimeEpisodeRequest(string Phase, string Instructions, IReadOnlyList<ChatMessage> Messages, IReadOnlySet<string> AllowedToolNames, bool EnableResponseRecovery, bool? EnableThinking, Action<string, AIFunctionArguments>? ValidateToolCall).
- New RuntimeAgentEpisode.RunAsync(RuntimeEpisodeRequest input, ScriniaMcpSession session, ModelSettings settings, RunLimits limits, IRunEvents events, CancellationToken ct): Task<EpisodeResult>. Move EpisodeResult to runtime too.
- RuntimeEnvironment.Create(AttemptPaths paths): Dictionary<string,string?> replaces the environment-only dependency from OwnedMcpProcess to AttemptWorkspace. V1 AttemptWorkspace.CreateEnvironment delegates to it.

- [ ] Add failing RuntimeEpisodeTests asserting explicit instructions, an image-containing ChatMessage, exact allowed-tool catalog, and no V1 prompt selection. Assert unknown allowed tool names fail before inference, and a tool rejected by ValidateToolCall is never dispatched or counted as completed.
- [ ] Run rtk dotnet test tests/Scrinia.Agent.Benchmarks.Tests/Scrinia.Agent.Benchmarks.Tests.csproj --filter FullyQualifiedName~RuntimeEpisodeTests; expect missing runtime API failures.
- [ ] Extract the listed components. Preserve current public names initially; update only moved internals' source-generation context references. RuntimeJsonContext covers Tool, UsageTotals, string, and dictionary payloads. Add internal visibility for both benchmark projects and their test assemblies where required.
- [ ] Move the SDK tool-loop body into RuntimeAgentEpisode. Replace the phase-string recovery condition with the explicit policy flag. The V1 facade supplies its existing instructions, all existing tools, and unchanged recovery behavior. Avoid concurrent tool invocation. Invoke ValidateToolCall before each dispatch. Add an optional thinking argument to the observer/request factory: V2 sends chat_template_kwargs.enable_thinking=true; V1 passes null and retains its byte-equivalent request options. Do not add a new serialized property to V1 RunConfiguration merely to support V2.
- [ ] Extend image evidence recording: preserve media type, SHA-256 and byte count for image DataContent, with bytes retained in the owned source/image sidecar; reject remote image URIs in V2 and avoid copying base64 into each event. Assert synthetic images survive request construction and can be verified from saved evidence.
- [ ] Add combined-budget regression: transport failure, recoverable bad completion, then transport failures consume the same five attempts; at most one response recovery occurs. Assert each attempt consumes the 40-request episode budget and cancellation during backoff emits no later request. Reuse existing uncertain-tool-completion and shutdown tests.
- [ ] Run the complete V1 benchmark test project. Expect all tests passing; do not update frozen live artifacts. Build the solution.
- [ ] Commit only extraction files and tests: refactor: share benchmark agent runtime without changing V1 behavior.

### Task 2: Establish the Python adapter and worker protocol

**Files**
- Create benchmarks/longmemeval-v2/bridge.py, scrinia_memory.py, protocol.schema.json, upstream.lock.json, requirements-dev.txt, tests/test_bridge.py, tests/test_memory_contract.py, tests/fixtures/fake_worker.py.
- Create benchmarks/Scrinia.LongMemEvalV2.Worker/Scrinia.LongMemEvalV2.Worker.csproj, Program.cs, Protocol/WorkerRequest.cs, WorkerResult.cs, WorkerJsonContext.cs.
- Create tests/Scrinia.LongMemEvalV2.Worker.Tests/ with ProtocolTests.cs and the repository's existing xUnit/package conventions.

**Interfaces**
- Python ScriniaMemory(Memory), memory_type = scrinia; insert(trajectory), query(query, query_image=None), configure_runtime(**kwargs), _save_backend(output_dir), _load_backend(input_dir).
- Python WorkerBridge.run(operation: str, request: dict, cancel_event) -> dict.
- Worker CLI: dotnet scrinia-lme-v2-worker.dll --request <absolute-json> --result <absolute-json>.
- JSON protocol version 1: operation is learn, retrieve, or validate-snapshot; operationId is a random opaque ID; configuration and owned paths are separate fields. Learn carries a normalized trajectory manifest; retrieve carries question text and optional image reference. Results carry operationId, status, snapshot receipt or evidence selection, and event-log path. Unknown fields and versions fail.
- No fields for gold answer, dataset question ID, category, final-reader response, or grader configuration.
- Define typed LearnRequest (normalized trajectory manifest, prior snapshot path, staging path, controller configuration), RetrieveRequest (snapshot path, question text, optional question image reference, controller configuration), and ValidateSnapshotRequest (snapshot path and expected fingerprint). LearningReceipt binds source/previous/new snapshot hashes and committed operation ID. Add these types to WorkerJsonContext. Python camelCase fields match the source-generated C# payloads and schema fixtures exactly.

- [ ] Write fake-worker tests for request/result ID mismatch, nonzero exit, missing/partial output, unknown operation/version, and rejection of forbidden fields. A schema fixture must deserialize identically in Python and source-generated .NET.
- [ ] Run rtk proxy python -m unittest discover -s benchmarks/longmemeval-v2/tests -p test_bridge.py; expect failures before bridge implementation.
- [ ] Implement owned subprocess invocation without shell=True. A dedicated stdin control line containing a cancel operation cancels the worker; EOF also cancels. The worker links this with operation cancellation and disposes its MCP child. Allow 30 seconds for cleanup before terminating only the owned process tree and recording failure. Never accept a result before successful process exit and output validation.
- [ ] Register the adapter externally and create a pinned upstream checkout under ignored artifacts during implementation. Do not edit tracked upstream files. Configure unique per-instance workspace/home directories from an explicit run root; do not depend on upstream's backend-specific path injection.
- [ ] Add tests for cancellation during a fake request, owned-child exit, unique instances, no credential values in protocol, and registration through upstream build_memory. Use fake operations until later tasks provide learn/retrieve behavior.
- [ ] Run both Python contract tests and rtk dotnet test tests/Scrinia.LongMemEvalV2.Worker.Tests/Scrinia.LongMemEvalV2.Worker.Tests.csproj; expect passing schema, ownership, and registration tests.
- [ ] Commit: feat: add official V2 memory adapter and worker protocol.

### Task 3: Build complete source-backed memory snapshots

**Files**
- Create Python trajectory_input.py, snapshots.py, batching.py, tests/test_snapshots.py, tests/test_batching.py.
- Create worker Learning/TrajectoryLearner.cs, Learning/SourceSeeder.cs, Learning/LearningInstructions.cs, Evidence/SourceCatalog.cs, Evidence/SnapshotValidator.cs.
- Add worker LearningTests.cs and SnapshotValidationTests.cs.

**Interfaces**
- normalize_trajectory(trajectory: dict, asset_root: Path) -> dict uses pinned upstream prepare_trajectory_insert; retain original ordering and references.
- SnapshotStore.insert(trajectory_manifest: dict, configuration_hash: str, bridge: WorkerBridge, cancel_event) -> dict publishes a receipt only after completed validation.
- TrajectoryLearner.LearnAsync(LearnRequest request, CancellationToken ct): Task<LearningReceipt>.
- SourceCatalog maps each opaque source ID to trajectory/state/fragment indices, canonical text hash, screenshot hashes, and owned relative paths.
- SourceSeeder writes raw source text through remember with deterministic source paths and required descriptions. Learning can append derived notes in a separate namespace, never replace immutable source paths.

- [ ] Add tests asserting every source state/fragment remains reconstructible, original order and dates survive, images are retained, identical committed insertion is idempotent, and conflicting insertion content fails.
- [ ] Add crash/cancellation tests before publication, during result persistence, and after publication but before acknowledgment. Restart recognizes only a verified committed receipt; otherwise retain the previous snapshot. Never replay a possibly completed tool call.
- [ ] Run the relevant Python and .NET tests; expect missing snapshot/learning behavior failures.
- [ ] Implement immutable snapshot generations plus a same-directory atomic current-manifest replacement. Stage a copy of the preceding completed workspace, make bounded additions, stop MCP, validate the new state, then publish the manifest. Retain old generations until verified cleanup. Store an insertion ledger and configuration hash in each generation.
- [ ] Implement deterministic batching. Resolve and pin a Qwen-compatible processor during implementation; record its revision in the lock. Initial proposed caps: 24,576 serialized input tokens, 16,384 output tokens, at most four images per learning batch, and 4,096 text tokens per source fragment. Count prompt/tools and image preprocessing in input estimates, and require enough served context for input plus output. Split oversized text with source offsets; reject an image that cannot fit by itself. No silent truncation. These initial caps may change only using synthetic evidence before experiment freeze.
- [ ] Seed source chunks via MCP, then run the learning agent on ordered batches with explicit source citations. Validate every derived note's source references. Use RuntimeEpisodeRequest.ValidateToolCall to restrict learning writes to the derived-note namespace; reject compact/forget/link or other mutations that could alter immutable sources. Only the deterministic host SourceSeeder writes source memories. Use source-backed descriptions, not reference answers.
- [ ] Test a lost remember response leaves the old committed snapshot usable and records uncertainty; test cancellation between batches never publishes a partial trajectory.
- [ ] Run Python snapshot/batching tests and worker learning/snapshot tests; expect all passing. Commit: feat: index V2 trajectories into validated Scrinia snapshots.

### Task 4: Retrieve verified multimodal evidence

**Files**
- Create worker Retrieval/EvidenceRetriever.cs, Retrieval/RetrievalInstructions.cs, Evidence/EvidenceSelection.cs, Evidence/EvidenceMaterializer.cs.
- Implement retrieve in worker Program.cs and adapter query.
- Add worker RetrievalTests.cs, EvidenceMaterializerTests.cs; Python tests/test_query_isolation.py.

**Interfaces**
- EvidenceRetriever.RetrieveAsync(RetrieveRequest request, CancellationToken ct): Task<EvidenceSelection>.
- EvidenceSelection contains ordered memory references (path and one-based chunk indices) and source references (source ID plus zero-based state index). It contains no final answer and no arbitrary image path.
- EvidenceMaterializer.MaterializeAsync(EvidenceSelection selection, SourceCatalog catalog, ScriniaMcpSession session, CancellationToken ct) produces ordered text/image items for the Python adapter to return under the official contract.

- [ ] Add failing tests for question-image forwarding, real source text materialization, original image selection, fabricated reference rejection, missing/mutated images, and path traversal/reparse-point escape.
- [ ] Assert retrieval's tool catalog contains only the chosen read operations, no mutation tools. Two queries must start independent conversations; neither can alter authoritative memory or read prior query logs.
- [ ] Run worker retrieval tests and Python query-isolation tests; expect failures before implementation.
- [ ] Implement retrieval with explicit evidence-selection instructions. Gather actual memory content through recall rather than trusting the controller's quoted text. Materialize source text and images from the validated catalog; hash-check before return. Preserve disagreements and context labels.
- [ ] Return an empty list only for a valid empty selection. Malformed selection, unavailable assets, or exhausted recovery raises an integration error; never transform them into a successful empty list.
- [ ] Keep model/tool attempts, validation, materialization and shutdown inside query. Before/after hashes cover authoritative store contents; explicit derived-cache paths are excluded. Initially use a per-query copy of the committed workspace to contain any incidental writes; copying is timed as query work. No question caching.
- [ ] Verify loop/truncated-call recovery also operates in V2 retrieve despite its different phase name, with the shared budget from Task 1.
- [ ] Run both test suites; expect passing evidence/isolation/recovery tests. Commit: feat: return source-verified text and image evidence for V2.

### Task 5: Run the unchanged evaluator against synthetic fixtures

**Files**
- Create Python run_scrinia.py, experiment_config.py, tests/test_official_harness.py, tests/fixtures/synthetic_web/, tests/fixtures/synthetic_enterprise/.
- Complete adapter save/load and configure_runtime.
- Create tests/fixtures/fake_openai_server.py for deterministic reader/judge protocol responses.

**Interfaces**
- run_scrinia.py --upstream-root <path> --config <experiment.json> --output-dir <new-directory>.
- Experiment schema contains domain, tier, upstream/data paths, controller/reader/judge settings, method fingerprint, limits, and asset/run roots. Both domains are run explicitly; submission mode forbids limit or question-ID selection.
- The launcher calls upstream public-data materialization helpers, registers Scrinia, and invokes the unmodified harness entry point with normal arguments. Preserve upstream output filenames.

- [ ] Write integration tests for shared-history reuse, distinct histories, save/load relocation, missing assets, fingerprint mismatch, and cancellation propagation. Use tiny invented histories and questions with images; neither real V2 rows nor V1 failure cases are fixtures.
- [ ] Assert fake reader calls have the upstream prompt and options, no MCP tools, and actual materialized evidence. Assert judge calls use the upstream evaluator and receive no controller-specific parser.
- [ ] Run rtk proxy python -m unittest discover -s benchmarks/longmemeval-v2/tests -p test_official_harness.py; expect integration failures before wiring.
- [ ] Implement the launcher and save/load bundle with relative asset paths. Forward only opaque query invocation metadata. The bridge must never receive runtime question files, gold answers, or evaluation specs.
- [ ] Compare hashes of upstream evaluator files before/after execution. Assert official query latency includes an injected worker delay, retry delay, and cleanup delay. Check required official artifacts and completeness behavior without fabricating a public benchmark score.
- [ ] Test interruption produces an incomplete directory that cannot be packaged; do not merge partial grades or implement resume by skipping previously graded queries.
- [ ] Run all adapter tests, the worker test project, and the V1 benchmark test project. Expect all passing; build the solution. Commit: feat: integrate Scrinia with the official V2 evaluator.

### Task 6: Freeze, preflight, and package the experiment

**Files**
- Create Python freeze_experiment.py, preflight.py, package_submission.py, tests/test_experiment_freeze.py.
- Create configs/small.json, README.md, SYSTEM_DESCRIPTION.template.md, requirements.lock and source archive manifest under benchmarks/longmemeval-v2/.
- Record implementation validation under artifacts/longmemeval-v2/validation/; exclude artifacts/secrets from commits.

**Interfaces**
- freeze_experiment.py --config <path> --output-dir <new-directory> writes resolved config, file/model/data fingerprints, source archive and dependency locks.
- preflight.py --frozen-experiment <path> --offline or --live.
- package_submission.py --frozen-experiment <path> --web-run <path> --enterprise-run <path> delegates validation and package generation to the official utilities.

- [ ] Add failing tests for changed prompt/binary/catalog/dataset/processor/image policy; missing required credential-variable names; changed model identity; wrong reader/judge names; incomplete domains; and omitted failed cases.
- [ ] Implement separate controller/reader/judge settings. Reuse BENCH_AGENT_API_KEY for controller and proposed reader only if configured; require an explicit GPT-5.2 credential reference. Never assume the old local judge key works for GPT-5.2.
- [ ] Implement offline protocol checks for the controller explicit thinking=true option, image serialization, and independent controller versus reader output limits. Preserve the official reader request exactly: at this upstream revision the enabled-thinking path relies on the server default rather than transmitting enable_thinking=true. Record and verify that server default during preflight; do not patch the reader to force a new request field. Model ID whitespace is normalized once and the exact resolved value is frozen. Report capability checks as unverified until a live probe succeeds.
- [ ] Implement a synthetic live probe with text and image-dependent questions, real tool calls, context/capacity verification, and model identity checks. A silent unsupported parameter or image omission fails preflight; do not silently alter settings. Reader thinking must be enabled in the deployed server default because the unchanged official request does not force it. This stage needs endpoints/credentials and execution authorization when reached.
- [ ] Produce cost/runtime estimates from synthetic build and query measurements, including unsuccessful attempts; unknown usage stays unknown. Keep the full V1 run paused.
- [ ] Generate the source archive and system description with MTP/BF16 deployment, prompts, recovery, hardware, limits, exposure log, and code/data provenance. Invoke official step-1/step-2 packaging rather than recreating its score. Treat multi-file source-archive acceptance as an organizer-format question if documentation does not resolve it.
- [ ] Run all offline suites and documentation/config validation. Require a .NET quality review for runtime/worker changes and the repository UX review for operator docs at implementation time. Commit: feat: freeze and package reproducible V2 experiments.
- [ ] Stop before a full scored run or submission. Report offline results, unresolved live prerequisites, and the exact frozen configuration for user review.

## Final validation and execution handoff

A completed implementation must show: unchanged upstream evaluator hashes; passing V1/worker/Python tests; synthetic image-only success; isolation and uncertain-write tests; cancellation with no surviving owned children; validated save/load; and official-package rejection of incomplete inputs. Live image/model/context claims require live evidence and must not be inferred from a model name.

The first scored experiment is a fresh small-tier web plus enterprise pair using one frozen configuration. No retained V1 grades enter it. A model, prompt, policy, or payload change creates a new experiment. Before running, obtain the GPT-5.2 connection details and verify the local Qwen deployment supports the official reader's context requirement.

Recommended execution: native in this session using superpowers:executing-plans, with the task checks above and an independent final code review. This keeps the tightly coupled runtime/protocol extraction in one working context. Subagent-driven execution remains an option if the user prefers per-task independent review.

## Plan self-review

- Spec coverage: protocol boundary and models (Tasks 2, 5, 6); runtime reuse/recovery (Task 1); source-preserving learning and snapshots (Task 3); images/evidence/isolation (Task 4); official lifecycle/reporting (Task 5); freeze and reproducibility (Task 6).
- Each review-focus failure has an owning test above. The unknown endpoint capabilities are explicit live checks, not implementation assumptions.
- Shared interfaces are defined once and used by dependent tasks. No V1 dataset/scorer dependency enters the worker.
- Proposed batching caps and per-query snapshot copies are initial method choices, frozen only after synthetic validation. They must not be silently changed during scored evaluation.
- This document plans work only. No tests, dependency installation, product implementation, or live V2 calls were performed while writing it.
