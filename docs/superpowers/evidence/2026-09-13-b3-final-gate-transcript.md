# B3 final-gate verification transcript

Captured 2026-09-13 in the repository worktree, serially, with no command timeout. The command
lines and result lines below are copied from the raw stdout/stderr captures. The complete captures
were hashed at capture time; warning-heavy output is represented by its exact final result lines
and the complete-capture hash so this evidence file remains reviewable.

## Boundary

- Current `HEAD`: `5f17ae428212186e0cb8bb9ad8362b5d37caf3a1` (`docs: specify b4 plugin event boundary`).
- B3 base: `986fce0a` (`feat: define multi-memory transaction contracts`), parent
  `7274d6547338f9beaf507cbd3ea1eb9fc2efdfea`.
- B3 implementation/test range: `986fce0a^..1a10c0f77316fa0717c9ad4307a162a8176c1041`.
- `1a10c0f` is the final B3 implementation/test gate commit. `4a15e329` (evidence summary) and
  `63befd20` (path-inventory correction) are later documentation-only evidence commits; neither is
  part of the B3 implementation range. `5f17ae42` is the later B4 planning/specification commit.
- `git merge-base 1a10c0f7 HEAD` = `1a10c0f77316fa0717c9ad4307a162a8176c1041`.

## Commands and raw results

All elapsed values are enclosing-stopwatch values. `timeout=NO` is an explicit completion marker,
not an inferred result. Hashes are SHA-256 of the complete UTF-8 raw stdout/stderr capture,
including its metadata line.

### B3-focused tests

```text
$ dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MultiMemoryTransaction" --no-restore
Passed!  - Failed:     0, Passed:   105, Skipped:     0, Total:   105, Duration: 3 s - Scrinia.Tests.dll (net10.0)
[META exit_code=0 elapsed=00:00:16.9615852 timeout=NO]
```
Raw capture: 2,758 lines; SHA-256 `fe0974d77ad542e57a3cfe4ab6bdf39ac0e91fa6ee8672875d6cf0628e68c30b`.

### B2/regression lane

```text
$ dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append|FullyQualifiedName~WholeMemory|FullyQualifiedName~FileEntity|FullyQualifiedName~Recovery|FullyQualifiedName~Nmp2|FullyQualifiedName~Sidecar|FullyQualifiedName~Cli|FullyQualifiedName~ScriniaMcp" --no-restore
Passed!  - Failed:     0, Passed:   958, Skipped:     0, Total:   958, Duration: 1 m 56 s - Scrinia.Tests.dll (net10.0)
[META exit_code=0 elapsed=00:01:59.2473628 timeout=NO]
```
Raw capture: 16 lines; SHA-256 `bfe5f375040eda0a8145aa36ad68ea2bf0d7f37c0c251cb724f50735e2119d52`.

### Full solution tests

```text
$ dotnet test Scrinia.sln
Passed!  - Failed:     0, Passed:  272, Skipped:     0, Total:   272, Duration: 8 s - Scrinia.Retrieval.Benchmarks.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed: 3162, Skipped:    19, Total: 3181, Duration: 5 m - Scrinia.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    42, Skipped:     0, Total:   42, Duration: 2 s - Scrinia.Plugin.Embeddings.Tests.dll (net10.0)
[META exit_code=0 elapsed=00:05:10.0131685 timeout=NO]
```
Raw capture: 411 lines; SHA-256 `cf208b3c6cf67e9ddb32bd44d1e7ae53acf7d23d1cd7b6ab308d95be8425bdf2`.

### Release build

```text
$ dotnet build Scrinia.sln -c Release
    3086 Warning(s)
    0 Error(s)
Time Elapsed 00:00:04.81
[META exit_code=0 elapsed=00:00:05.2913466 timeout=NO]
```
Raw capture: 6,206 lines; SHA-256 `37273860fbfedf8dc8953dee874caca800c2a379b407f90f82cb1ed45964ed0b`.

### Trimmed publishes

```text
$ dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true
Scrinia -> src/Scrinia/bin/Release/net10.0/win-x64/publish/
[META exit_code=0 elapsed=00:00:10.2923516 timeout=NO]

$ dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained false /p:PublishTrimmed=true
error NETSDK1102: Optimizing assemblies for size is not supported for the selected publish configuration. Please ensure that you are publishing a self-contained app.
[META exit_code=1 elapsed=00:00:01.5294042 timeout=NO]
```
The first is the supported green self-contained result. The second is the expected framework-
dependent trimming refusal and is not counted as a regression.

Self-contained raw capture: 15 lines; SHA-256 `62c53586543e7431eaf057f83d9a2847deb6573f0ea4a2ad9da8117719c9b6c4`.
Framework-dependent raw capture: 11 lines; SHA-256 `7ef0c6aedbbde9115484978890311f82e94e3e62bf0cfdef686bae3811db94f4`.

### Diff, status, and staged inventory

```text
$ git diff --check
[META exit_code=0 elapsed=00:00:00.2902799 timeout=NO]

$ git diff --cached --name-status
[META exit_code=0 elapsed=00:00:00.2571792 timeout=NO]

$ git status --short
 M .superpowers/sdd/progress.md
 M docs/superpowers/ledgers/2026-09-04-task9-shared-classifier-registry.md
 M task-4-report.md
 M task-7-report.md
 M task-9-report.md
?? .agents/
?? .codex/
?? task-1.2-report.md
?? task-2-review-package.md
?? task-2.1-report.md
?? task-2.2-report.md
?? task-3-review-package.md
?? task-3.2-report.md
?? task-5-restore-adapter-report.md
?? task-5.2-report.md
?? task-6-file-entity-adapter-report.md
?? task-8-report.md
[META exit_code=0 elapsed=00:00:00.2924164 timeout=NO]
```
No staged paths were present. Capture hashes: diff-check `f1004f83817aba8d555ff212ee2a222c2b0b8b7b9e753a78749597bc228abf2`, staged `b864c29fac14db9b5b172c2a95c5710b004d2e5fa557cbce525c7f243eea9ac1`, status `e7796f9c968f76e02c8a60622bc0bd0ba993548f4350485de0827f41ac2d603d`.

After adding this transcript, the independent review, and the ledger update, a fresh serial
`git diff --check && git status --short && git diff --cached --name-status` remained clean for
whitespace and showed the three new documentation paths plus the pre-existing unrelated worktree
paths above; the staged inventory remained empty. These three documentation paths are intentionally
unstaged for the orchestrator: `docs/superpowers/evidence/2026-09-13-b3-final-gate-transcript.md`,
`docs/superpowers/reviews/2026-09-13-b3-final-gate-review.md`, and
`docs/superpowers/ledgers/2026-09-13-b3-final-acceptance.md`.

## Skipped-test classification

The 19 skips are intentional environment/model-asset or fixture-reader exclusions, not B3 tests:

- `BertTokenizerTests`: `TokenizeRaw_FiltersUnknownTokens`, `Encode_TruncatesToMaxLength`,
  `FromVocabFile_LoadsTokens`, `VocabSize_ReturnsPositiveCount` — model vocabulary asset lane (4).
- `PublishManifestTests`: `PublishSh_RejectsReorderedHeaderThroughTheProductionReader`,
  `PublishSh_RejectsMalformedFixtureThroughTheProductionReader`,
  `PublishSh_AcceptsAValidManifestThroughTheProductionReader` — publish-manifest fixture lane (3).
- `Model2VecProviderTests`: `EmbedAsync_IsDeterministic`, `SimilarTexts_HaveHigherSimilarity`,
  `EmbedAsync_OutputIsL2Normalized`, `EmbedAsync_ReturnsCorrectDimensionVector` — model asset lane (4).
- `UnigramTokenizerTests`: `SemanticSimilarity_WorksCorrectly`, `LoadsVocab`,
  `Model2VecProvider_LoadsMiniLmDistillation`, `TokenizesSimpleText` — model asset lane (4).
- `Model2VecLazyLoadTests`: `DimensionsAndSignature_AreAvailableWithoutMaterializing`,
  `FirstEmbed_MaterializesTheMatrix_AndTheSecondDoesNot`,
  `DeferredLoad_ProducesTheSameVectorAsAWarmedProvider`,
  `Load_DoesNotAllocateTheEmbeddingMatrix` — model asset/lazy-load lane (4).

## Scope inventory

The B3 implementation/test inventory is exactly the 19 paths recorded in the acceptance ledger at
`docs/superpowers/ledgers/2026-09-13-b3-final-acceptance.md`, and is independently confirmed by
`git diff --name-status 986fce0a^..1a10c0f7`. No production or test path was changed by this
documentation-only repair.

## Review

Independent review: `docs/superpowers/reviews/2026-09-13-b3-final-gate-review.md`.
