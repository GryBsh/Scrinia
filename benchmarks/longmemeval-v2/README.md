# Scrinia adapter for LongMemEval V2

This adapter runs the pinned official evaluator with a Scrinia memory worker. The initial scored experiment is a fresh small-tier web and enterprise pair using one frozen operating point. V1 remains paused; its grades, binaries and stores are separate.

`configs/small.json` is a **draft preset**, not a runnable or live-verified configuration. Replace every `C:/REPLACE` path, supply an immutable dataset revision and GPT-5.2 endpoint, and provision the complete worker/MCP payloads before freezing. Declare the immutable model revision, hardware and effective reader thinking default from deployment evidence before freezing real data. It intentionally fails validation while these prerequisites remain unresolved. Keep credentials in environment variables; configuration stores their names only. Reusing `BENCH_AGENT_API_KEY` for controller and reader is explicit in the draft. The judge requires its own `GPT52_API_KEY` reference; an old local judge key establishes no GPT-5.2 access.

The controller and proposed reader are `unsloth/Qwen3.5-9B-MTP-GGUF:BF16` at `https://ai-srv-md.saga-augmented.ts.net/v1`. Model names are trimmed once before freezing. Both use temperature 0.6, top_p 0.95 and top_k 20. Controller output is 16,384 tokens; its timeout is 180 seconds per attempt, episode budget 300 seconds, request cap 40 and shared attempt cap five, with transport backoff 5/10/20/40 seconds and one response recovery. Reader output is 20,000 tokens with 200,000 memory-context tokens. Judge is GPT-5.2 with medium reasoning and 4,096 output tokens. Prompt and reader concurrency are one.

## Prepare offline

Use the existing isolated Python environment or provision one from `requirements.lock` and the processor dependency details in `upstream.lock.json`. The lock records the implementation environment; the frozen dependency manifest additionally hashes actual installed Python package/native/runtime bytes. CPU PyTorch wheel provisioning may require the official CPU wheel index. No model weights are needed for offline validation. The following checks use a restored full repository checkout. For an extracted source ZIP, follow [the archive restore/build/publish instructions](ARCHIVE_BUILD.md) for the included worker and Scrinia CLI graph.

```powershell
rtk proxy dotnet build Scrinia.sln --no-restore --nologo
rtk proxy dotnet test tests/Scrinia.LongMemEvalV2.Worker.Tests/Scrinia.LongMemEvalV2.Worker.Tests.csproj --no-restore --nologo
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 -m unittest discover -s benchmarks/longmemeval-v2/tests -p test_*.py
```

Capture the actual MCP catalog and full guide without inference. Save a UTF-8 request JSON with absolute `root` (a new directory), `executable` (the exact scri executable) and `prefixArguments` (normally `[]`). Use canonical Windows paths. The worker starts a fresh workspace/home and writes `tools.json`, `catalog.sha256` and `guide.txt`; it never creates a model client.

```powershell
rtk proxy benchmarks/Scrinia.LongMemEvalV2.Worker/bin/Debug/net10.0/scrinia-lme-v2-worker.exe --capture-catalog C:/absolute/capture-request.json
```

Update a copy of the paired preset with those paths, the pinned processor assets, complete published worker/MCP payload locations, immutable data root/revision and fresh per-domain run roots. The checked-in source archive manifest includes build inputs, embedded resources, runtime and adapter sources, including relevant dirty/untracked source. It excludes artifacts, caches and secret-like filenames. Review your source inventory before freezing. Keep data, output and frozen directories outside runtime payload directories. Freezing fingerprints dataset files without displaying questions or answers.

```powershell
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 benchmarks/longmemeval-v2/freeze_experiment.py --config C:/absolute/resolved-pair.json --output-dir C:/absolute/new-freeze
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 benchmarks/longmemeval-v2/preflight.py --frozen-experiment C:/absolute/new-freeze --offline
```

A freeze contains resolved per-domain configs, a paired config, source ZIP and source-file hashes, upstream hashes, dependency locks, complete runtime file hashes, processor/data hashes, estimates and a system description. Freeze directories must be new. The configured source_root must canonically match the checkout executing the adapter. The launcher verifies frozen inputs before and after a run; drift leaves an incomplete result. Packaging verifies them again. A prompt, binary, catalog, dataset, processor, image policy or model change requires a new freeze and a fresh pair. Never merge or resume graded queries across method versions.

Offline preflight checks the unchanged official reader request builder, image serialization and separate output limits. It reports live capabilities as **unverified**. The controller explicitly sends `chat_template_kwargs.enable_thinking=true`. At this pinned upstream revision the enabled-thinking reader does not transmit enable_thinking=true; its deployed server default must enable thinking. Do not patch the official request to force a new field.

## Verify live prerequisites

Live probes require explicit execution authorization, reachable endpoints and all named credentials. Starting a live preflight revokes earlier readiness before checking prerequisites; only a fully successful attempt atomically publishes new readiness. No live inference is part of the implementation checks. Supply a deployment attestation JSON with version 1, exact model/endpoint, immutable 40-hex `model_revision`, precision BF16, `mtp: true`, hardware, effective `context_tokens >= 220000`, `reader_thinking_default: true`, `verification_method`, and hashed local configuration/log evidence files (`evidence: [{path, sha256}]`). Its `applied_options` object must state temperature 0.6, top_p 0.95, top_k 20, controller_output 16384 and reader_output 20000. Inspect effective configuration/logs for ignored options, overrides and clamping before attesting.

```powershell
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 benchmarks/longmemeval-v2/preflight.py --frozen-experiment C:/absolute/new-freeze --live --deployment-attestation C:/absolute/deployment-attestation.json
```

The synthetic probe uses newly generated text/nonces, image-only digits, an actual isolated MCP list call through the controller, exact returned model identity checks and context-marker probes targeting 24,576 controller input tokens and 200,000 reader input tokens. Served context capacity of at least 220,000 tokens is separately operator-attested. It also probes GPT-5.2. Wire checks, empirical image/tool/context results and operator-attested server settings are recorded separately. HTTP 200 alone proves no capability. Missing/unknown/conflicting evidence, model drift, omission, truncation or failed image/context answers fails readiness. Provider usage remains unknown when absent; no usage or cost is invented.

Review the exact resolved configs, source manifest, exposure log and system description before authorizing a scored run. Pin real data without inspecting answers for development. Record any accidental exposure. Populate hardware and deployment provenance from evidence. The unchanged evaluator and package utilities stay at commit `2cc8c540bdb87fe6761629b585e727e1c4704520`; processor `Qwen/Qwen3.5-9B` stays at `c202236235762e1c871ad0ccb60c8ee5ba337b9a`.

## Run and package after authorization

Each output directory is new. Start with both domains at the same frozen configuration. No retained V1 grades enter the pair. Query startup, validation, retries, evidence assembly and cleanup remain inside official query timing.

```powershell
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 benchmarks/longmemeval-v2/run_scrinia.py --frozen-experiment C:/absolute/new-freeze --domain web --output-dir C:/absolute/new-web-run
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 benchmarks/longmemeval-v2/run_scrinia.py --frozen-experiment C:/absolute/new-freeze --domain enterprise --output-dir C:/absolute/new-enterprise-run
rtk proxy artifacts/longmemeval-v2/venv/Scripts/python.exe -X utf8 benchmarks/longmemeval-v2/package_submission.py --frozen-experiment C:/absolute/new-freeze --web-run C:/absolute/new-web-run --enterprise-run C:/absolute/new-enterprise-run --output-root C:/absolute/new-package-root
```

Packaging delegates completeness checks, aggregation, score computation and archive creation to official step 1 and step 2. Failed cases must remain in per-question outputs. Both domains must be complete and match frozen provenance. Packaging creates local files only; it neither uploads nor submits anything. The official utility accepts one code file; whether organizers accept a ZIP containing this multi-file .NET implementation remains an open format question. Local validation does not establish organizer acceptance.

Each run writes `measurements.jsonl` and `estimates.json` from observed adapter insert/query calls, including failed calls; durations include internal retries. Attempts count these complete calls. Provider usage and cost remain null when unknown. Fake-peer timings do not estimate Qwen speed or pricing. A freeze also summarizes any supplied calibration measurements in its `estimates.json`. Each entry has phase (build/query), seconds, success, provider (real/fake) and nullable cost_usd. Empty measurements produce unknown estimates. Fake-provider timings establish no real-model performance/cost. Reuse verified snapshots only within the documented save/load contract; uncertain writes refuse publication. Crash/cancellation tests do not establish host or power-loss durability.

## Storage and saved-history contract

Storage policy v1 limits each content chunk to 7,900 UTF-16 code units, below the public recall content budget of 7,992 including its chunk label. The host deterministically splits complete serialized source records and original-trajectory JSON at Unicode scalar boundaries; joining the chunks reproduces the original string, including escaping and metadata. Source IDs, fragment boundaries, source associations and screenshot bytes remain unchanged. Snapshot validation checks every stored chunk, and source materialization verifies every chunk through public recall before returning original evidence. Derived notes use explicit content-array chunks: each element must be nonblank and within the same limit, or the guard rejects the call before dispatch. The host does not rewrite model arguments.

Every controller response, including the first response of each learning batch and query, must report the configured model identity exactly. Missing, substituted or changed identities fail before model-directed tool dispatch. Raw response events remain available for diagnosis; failed learning does not publish a snapshot.

Saved generations carry an explicit ordered history of trajectory IDs and logical source hashes, including screenshot hashes. Runtime relocation is allowed when those identities and the frozen method match. For launcher load/reuse, the adapter derives the expected history from the materialized haystack and source trajectories before the official harness loads memory. Permutations, missing or additional trajectories, and changed source text or images fail before a query/provider request.

After successful insertion, retention keeps the current generation and its immediate predecessor. Before retiring older generations, the adapter flushes the current payload, metadata, directory entries and published pointer to durable storage. If a flush fails or the filesystem does not support it, insertion remains committed and all generations are retained until a later successful barrier. Older committed generations are retired only after their recorded hashes and history validate and active readers release their leases. Uncertain operations, corrupt evidence, and legacy generations without retirement tickets are preserved. Saved bundles are independent copies. This bounds normal retained generation storage; each insertion still copies the current cumulative snapshot, and small coordination files remain. Stop older adapter processes before upgrading because they do not hold reader leases. Benchmark execution remains governed by the unchanged official harness.

Direct callers of the upstream load API must supply a requested Scrinia configuration with memory_params.expected_history as an ordered array, for example [{"id":"trajectory-id","sourceHash":"<64 lowercase hex characters>"}]. Derive these values from the actual intended trajectories with trajectory_input.normalize_trajectory; a saved configuration alone is not an expected-history binding. Calling load_memory without this independent binding fails. The binding stays enforced on loaded queries. Older bundles without the current method/storage policy and ordered-history evidence are refused, not silently migrated. No question IDs, gold answers or question metadata are included in the worker history inputs.

A lost acknowledgment after current-generation publication may leave an intent marker. Under the snapshot lock, insertion/save retires only a marker whose filename, trajectory ID, source hash, operation ID and generation exactly match the verified current committed receipt and pointer. Unproven, conflicting or forged markers remain and block saving; learning is never replayed to resolve uncertainty.
