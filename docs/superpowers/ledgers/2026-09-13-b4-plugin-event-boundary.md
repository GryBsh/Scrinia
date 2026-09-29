# B4 final verification ledger

Plan: `docs/superpowers/plans/2026-09-13-b4-plugin-event-boundary.md`
Spec: `docs/superpowers/specs/2026-09-13-b4-plugin-event-boundary-design.md`
Verification date: 2026-09-14
Task: 7 — verification and evidence gates

## Dependency and implementation evidence

- Accepted B3 dependency: `docs/superpowers/ledgers/2026-09-13-b3-final-acceptance.md`.
- B3 implementation/test boundary: `986fce0a^..1a10c0f77316fa0717c9ad4307a162a8176c1041`.
- B4 implementation/test commits, in order: `313efe9b`, `eced49f1`, `e6a0fa55`, `7b1ad962`,
  `ca841a6a`, `33accd4e`, `6d522cf3`, `935ea515`, `23352bbb`, `26b875f9`, `4cf59009`,
  `5b42c891`, `c4efe909`, `3aca3df2`, `b7105cb2`, `9a6d319d`, `e3904687`, `c738435d`,
  `b285cb64`, `73e5dd9d`, `f9ea92e3`.
- Task reports 1–6 record APPROVE dispositions. Task 2 deferred two minor coverage rows; Task 4
  deferred three minor findings. No Task 3, 5, or 6 findings were parked.

## Fresh verification evidence

All commands ran from the repository root. The previously recorded focused B4 lane passed **170
tests**; the fix-wave focused additions passed **4 tests**. Its filter
included contract, registry, adapter, recovery, observer, admission, durable contract, aggregate,
lifecycle, MCP facade, and CLI compatibility tests.

- B4 focused lane: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter
  "FullyQualifiedName~TransactionalParticipantContractTests|...|FullyQualifiedName~CliMultiMemoryTransactionTests"
  --no-restore -v:minimal` — **170 passed, 0 failed**; fix-wave focused filter — **4 passed, 0
  failed, 0 skipped**.
- B2/single-memory regressions: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter
  "FullyQualifiedName~Append|...|FullyQualifiedName~ScriniaMcp" --no-restore -v:minimal` —
  **965 passed, 0 failed**. The first 120-second run timed out without a result; the repeated run
  completed in 270.3 seconds.
- Full suite (authoritative post-fix run): `dotnet test Scrinia.sln --no-restore -v:minimal` —
  **3567 passed, 0 failed, 0 skipped** across three test projects. This named rerun supersedes the
  earlier 3563-pass summary; no skip classification is carried forward.
- Release build: `dotnet build Scrinia.sln -c Release -v:minimal` — **0 errors**, 3135 existing
  analyzer warnings.
- Self-contained trimmed publish with trim analysis enabled: `dotnet publish src/Scrinia/Scrinia.csproj
  -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true
  -p:SuppressTrimAnalysisWarnings=false -p:EnableTrimAnalyzer=true -v:minimal` — **passed**.
- Expected framework-dependent trimmed caveat: the same publish with `--self-contained false` —
  **failed as expected with NETSDK1102**, because trimmed framework-dependent publishing is refused.
- Whitespace: `git diff --check` — **clean** for the current worktree and B3/B4 inspected ranges.

## Audits

- Source generation: B4 durable serialization uses `MultiMemoryTransactionJsonContext`; relevant
  B4 facade/plugin serialization uses existing generated type metadata. No new serializer root was
  introduced.
- Public surface: focused facade/CLI tests passed; reflection/no-new-surface assertions remain
  green. No MCP/CLI command, response field, endpoint, or serializer root was added.
- Recovery ownership: `RecoveryDiscovery.cs` has no B4 recovery references; B4 recovery references
  remain in `MultiMemoryTransactionCoordinator`.
- Deferred-work/unsafe-shape search found only pre-existing general `BackgroundWork`/embedding
  deferral comments and unrelated serializers. No B4 `TODO`, `TBD`, `NotImplementedException`,
  object payload, exception-message classifier, or post-commit rollback path was found.
- Vulkan observer exclusion and observer continuation are covered by the focused lane and the
  embeddings observer tests recorded by Task 5.

## Exact path inventories

- Current unstaged inventory: `.superpowers/sdd/progress.md`, the listed legacy reports/ledger,
  `.agents/`, `.codex/`, and the other pre-existing root reports shown by `git status --short`.
- Current staged inventory: **empty**.
- B3 range inventory was recorded by `git diff --name-only 986fce0a^..1a10c0f77316fa0717c9ad4307a162a8176c1041`.
- B4 range inventory was recorded by `git diff --name-only ea1f17074d8d9bd5669bc36cf691b9c40fd087ad..HEAD`.
  The range contains the implementation/test paths listed by the plan plus the observer-support
  paths `src/Scrinia.Core/MaintenanceEventSink.cs`, `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`,
  `src/Scrinia/Services/CoreEmbeddingEventHandler.cs`, and `src/Scrinia.Core/PathRouter.cs`.

## Gate disposition

Execution and behavior gates pass. The three observer-support paths above remain a scope-ledger
concern: they were changed by the reviewed B4 hardening commit `e3904687` but are not present in the
plan's exact production path block. `PathRouter.cs` is accepted separately: its four-line
`local-topic:` alias is required for B3 legacy local-topic scope compatibility, and the cost is one
additional Core production path outside the block. The final fix wave also hardens participant
exception privacy and preserves retry/evidence recovery state across restart. No files were staged
or committed by Task 7.

## Final verification rerun after HEAD `8f2f6a17` (2026-09-14)

- B4 focused lane: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TransactionalParticipantContractTests|FullyQualifiedName~TransactionalParticipantRegistryTests|FullyQualifiedName~TransactionalParticipantAdapterTests|FullyQualifiedName~TransactionalParticipantRecoveryTests|FullyQualifiedName~TransactionalObserverBoundaryTests|FullyQualifiedName~MultiMemoryTransactionContractTests|FullyQualifiedName~MultiMemoryTransactionAdmissionTests|FullyQualifiedName~MultiMemoryTransactionLifecycleTests|FullyQualifiedName~MultiMemoryTransactionRecoveryTests|FullyQualifiedName~MultiMemoryTransactionFacadeTests|FullyQualifiedName~CliMultiMemoryTransactionTests" --no-restore -v:minimal` — **186 passed, 0 failed, 0 skipped**.
- B2/single-memory regression lane: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append|FullyQualifiedName~WholeMemory|FullyQualifiedName~FileEntity|FullyQualifiedName~Recovery|FullyQualifiedName~Nmp2|FullyQualifiedName~Sidecar|FullyQualifiedName~Cli|FullyQualifiedName~ScriniaMcp" --no-restore -v:minimal` — **failed: 1 failed, 968 passed, 0 skipped, 969 total**. Failure: `TransactionalParticipantAdapterTests.Recovery_state_preserves_retry_metadata_after_lifecycle_update`, expected `RetryElapsedMilliseconds == 0`, observed `4`. Isolated rerun reproduced it with `5`; no source or test was modified.
- Full solution: `dotnet test Scrinia.sln` — **exit 0**; Scrinia.Tests **3257 passed, 0 failed, 19 skipped**, embeddings tests **43 passed**, retrieval tests **272 passed**.
- Release build: `dotnet build Scrinia.sln -c Release -v:minimal` — **exit 0**, **13 projects, 0 errors, 3141 warnings** (existing analyzer/preview-SDK warnings).
- Self-contained trimmed publish with trim analysis enabled — **exit 0**, publish succeeded.
- Expected framework-dependent trimmed publish with `--self-contained false` — **exit 1 as expected**, `NETSDK1102`.
- Source-generated/public-surface/deferred-work/recovery-owner audit found no B4 serializer root, public aggregate MCP/CLI surface, B4 recovery in `RecoveryDiscovery.cs`, or B4-specific deferred/unsafe shape. Existing unrelated `exception.Message`, `TODO`, and serializer occurrences remain outside B4-specific findings.
- `git diff --check` — **exit 0**, clean. Exact final status retains the existing unrelated modified/untracked files; staged inventory is **empty**. HEAD is `8f2f6a17 fix: preserve handleless b4 recovery`.

### Independent review disposition

Independent review of the HEAD implementation and final handle-less recovery fix: **APPROVE for implementation behavior and scope**. Final Task 7 gate disposition: **HOLD**, because the required B2 regression lane has one reproducible timing-contract assertion failure despite the full solution invocation exiting 0. B4 is not complete until that discrepancy is resolved or explicitly accepted by the orchestrator.

## Authoritative final rerun at HEAD `f26d6943` (2026-09-14)

The elapsed assertion was corrected by HEAD `f26d6943` (`test: avoid flaky retry elapsed assertion`).
The rerun was serial and changed no source or test files.

- B4 focused lane: exact focused filter above — **186 passed, 0 failed, 0 skipped**.
- B2/single-memory regression lane: exact filter above — **969 passed, 0 failed, 0 skipped**;
  duration **2m02s**.
- Full `dotnet test Scrinia.sln --no-restore -m:1 -v:minimal`: Scrinia.Tests **3257 passed,
  19 skipped**, plugin embeddings **43 passed**, retrieval benchmarks **272 passed**;
  **3572 passed, 0 failed, 19 skipped** total.
- Release build: `dotnet build Scrinia.sln -c Release --no-restore -m:1 -v:minimal` — **13
  projects, 0 errors, 3141 existing warnings**.
- Self-contained trimmed win-x64 publish — **passed**.
- Framework-dependent trimmed publish — **failed as expected with `NETSDK1102`**.
- Source-generated/public-surface audit tests — **151 passed, 0 failed, 0 skipped**.
- Source-generated/public-surface/deferred-work/recovery-owner audit — **no B4-specific finding**;
  unrelated serializers, `exception.Message`, TODO/comment occurrences remain outside B4 scope.
- `git diff --check` — **exit 0, clean**.
- Exact final VCS state: HEAD `f26d6943dbac9677fdd140ac84c96a5887fbaae0`; staged inventory
  **empty**; all pre-existing modified/untracked paths listed in the report were preserved.

Elapsed-test ruling: elapsed retry metadata is monotonic callback timing, so a zero-delay retry
does not imply a zero elapsed value. The old exact-zero assertion was a flaky test contract and
not a B4 production regression. Final handle-less recovery approval remains **APPROVE for
implementation behavior and scope**. **Final Task 7 disposition: APPROVE; B4 is complete.**

## Authoritative final rerun at HEAD `3cb30c9f` (2026-09-14)

- DurableFile root cause/fix: retry cleanup could leave destination-length-sensitive temporary
  files behind; the fix performs cleanup from `finally` and uses short unique same-directory
  temporary siblings.
- Full `dotnet test Scrinia.sln --no-restore`: **3261 passed, 19 skipped, 0 failed** (3280 total).
- Release build: `dotnet build Scrinia.sln -c Release --no-restore`: **13 projects, 0 errors, 3144
  warnings** (existing analyzer/preview-SDK warnings).
- Self-contained trimmed win-x64 publish with `-p:PublishTrimmed=true`: **passed**.
- Framework-dependent trimmed publish with `--self-contained false`: **failed as expected with
  `NETSDK1102`**.
- DurableFile/A182 focused lane: **19 passed, 0 skipped, 0 failed**.
- Exact B4 regression lane: **186 passed, 0 skipped, 0 failed**.
- Exact B2 regression lane: **969 passed, 0 skipped, 0 failed**.
- `git diff --check`: **passed**.
- No source or test files were modified, and no files were staged or committed.
