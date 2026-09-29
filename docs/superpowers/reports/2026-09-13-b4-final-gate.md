# B4 final gate report

Status: verification lanes pass; scope disposition requires orchestrator attention.

The previously recorded fresh B4 focused lane passed 170 tests. The fix-wave focused additions
passed 4 tests; the B2/single-memory regression lane passed 965 tests.
The authoritative post-fix full-suite run, `dotnet test Scrinia.sln --no-restore -v:minimal`, passed
3567 tests with zero failures and zero skips across three test projects. Release build completed with
zero errors (existing analyzer warnings).
Self-contained win-x64 trimmed publish passed with trim analysis enabled. Framework-dependent
trimmed publish failed with the expected `NETSDK1102` refusal.

The source-generation, public-surface, recovery-owner, deferred-work, whitespace, and staged-path
audits found no implementation defect. B3 remains the sole lock owner, decision writer, recovery
authority, and aggregate producer; observers remain observers; no new MCP/CLI surface was added.

Independent final review disposition: **PASS for behavior and verification; HOLD for exact path
inventory review**. The reviewed B4 observer hardening commit `e3904687` also changed
`src/Scrinia.Core/MaintenanceEventSink.cs`, `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`,
and `src/Scrinia/Services/CoreEmbeddingEventHandler.cs`; those paths are outside the plan's exact
production path block. They are documented in the plan-owned ledger as a concern. No source/test
fix was justified, and no staging or commit occurred.

`PathRouter.cs` is also outside the exact production path block, but its four-line `local-topic:`
alias is required to preserve B3 legacy scope-to-directory compatibility and is accepted as a
necessary one-file support seam. See the plan-owned ledger for complete commit, command, and
inventory references.

The fix wave adds fixed-detail handling for unclassified participant exceptions and preserves retry
attempt/elapsed metadata plus the complete evidence triple through adapter rehydration and durable
round trips. No public MCP/CLI surface was added.

## Final rerun after HEAD `8f2f6a17`

The final handle-less recovery fix remains present and is covered by the B4 focused lane. The serial
B4 focused lane passed **186/186**. The serial B2/single-memory lane failed **1/969**:
`TransactionalParticipantAdapterTests.Recovery_state_preserves_retry_metadata_after_lifecycle_update`
observed `RetryElapsedMilliseconds` of `4` instead of `0`; an isolated rerun reproduced the same
failure with `5`. The implementation derives elapsed metadata from monotonic callback time, so a
zero-delay retry policy does not imply zero elapsed time. No source or test was changed.

The exact full command `dotnet test Scrinia.sln` exited 0: Scrinia.Tests 3257 passed/19 skipped,
Scrinia.Plugin.Embeddings.Tests 43 passed, and Scrinia.Retrieval.Benchmarks.Tests 272 passed.
Release build passed with 13 projects and 0 errors (3141 existing warnings); trim-analysis-enabled
self-contained publish passed; framework-dependent trimmed publish failed as expected with
`NETSDK1102`. Source-generated, public-surface, deferred-work, recovery-owner, whitespace, and
staged-inventory audits were recorded in the plan-owned ledger.

Independent review disposition: **APPROVE for implementation behavior and scope**. Final gate:
**HOLD** pending orchestrator disposition of the B2 lane failure; B4 is therefore **not complete**.

## Final gate rerun at HEAD `f26d6943` (2026-09-14)

The prior elapsed assertion was corrected on HEAD `f26d6943` (`test: avoid flaky retry elapsed
assertion`). All required verification was rerun serially; no source or test files were modified.

| Command | Exit | Result |
|---|---:|---|
| `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TransactionalParticipantContractTests|FullyQualifiedName~TransactionalParticipantRegistryTests|FullyQualifiedName~TransactionalParticipantAdapterTests|FullyQualifiedName~TransactionalParticipantRecoveryTests|FullyQualifiedName~TransactionalObserverBoundaryTests|FullyQualifiedName~MultiMemoryTransactionContractTests|FullyQualifiedName~MultiMemoryTransactionAdmissionTests|FullyQualifiedName~MultiMemoryTransactionLifecycleTests|FullyQualifiedName~MultiMemoryTransactionRecoveryTests|FullyQualifiedName~MultiMemoryTransactionFacadeTests|FullyQualifiedName~CliMultiMemoryTransactionTests" --no-restore -m:1 -v:minimal` | 0 | 186 passed, 0 failed, 0 skipped |
| `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append|FullyQualifiedName~WholeMemory|FullyQualifiedName~FileEntity|FullyQualifiedName~Recovery|FullyQualifiedName~Nmp2|FullyQualifiedName~Sidecar|FullyQualifiedName~Cli|FullyQualifiedName~ScriniaMcp" --no-restore -m:1 -v:minimal` | 0 | 969 passed, 0 failed, 0 skipped; duration 2m02s |
| `dotnet test Scrinia.sln --no-restore -m:1 -v:minimal` | 0 | Scrinia.Tests 3257 passed/19 skipped, embeddings 43 passed, retrieval 272 passed; 3572 passed, 0 failed, 19 skipped total |
| `dotnet build Scrinia.sln -c Release --no-restore -m:1 -v:minimal` | 0 | 13 projects, 0 errors, 3141 existing warnings |
| `dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true --no-restore -o publish/b4-task7-self-contained` | 0 | Self-contained trimmed publish passed |
| `dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishTrimmed=true --no-restore -o publish/b4-task7-framework-dependent` | 1 | Expected caveat: `NETSDK1102` refuses trimmed framework-dependent publishing |
| `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore -m:1 --filter "FullyQualifiedName~SourceGenerated|FullyQualifiedName~Surface|FullyQualifiedName~RegisteredFormats|FullyQualifiedName~NoObsolete|FullyQualifiedName~AddressSurface" -v:minimal` | 0 | 151 passed, 0 failed, 0 skipped |
| `git diff --check` | 0 | Clean |

Source-generated/public-surface/deferred-work/recovery-owner audits found no B4 serializer root,
new public MCP/CLI surface, B4 recovery in `RecoveryDiscovery.cs`, or B4-specific deferred/unsafe
shape. The source scan found only existing unrelated serializers, `exception.Message` handling,
and general TODO/comment occurrences. The final handle-less recovery fix is approved for behavior
and scope; the elapsed-test ruling is accepted as a test-contract correction, not a production
regression.

Final exact VCS state: HEAD is `f26d6943dbac9677fdd140ac84c96a5887fbaae0`; staged inventory is
empty. Existing unrelated modified paths are `.superpowers/sdd/progress.md`,
`docs/superpowers/ledgers/2026-09-04-task9-shared-classifier-registry.md`, `task-3-report.md`,
`task-4-report.md`, `task-7-report.md`, and `task-9-report.md`. Existing unrelated untracked paths
are `.agents/`, `.codex/`, `task-1.2-report.md`, `task-2-review-package.md`, `task-2.1-report.md`,
`task-2.2-report.md`, `task-3-review-package.md`, `task-3.2-report.md`, `task-5-restore-adapter-report.md`,
`task-5.2-report.md`, `task-6-file-entity-adapter-report.md`, and `task-8-report.md`. These were
preserved and not staged. **Final disposition: B4 complete.**
