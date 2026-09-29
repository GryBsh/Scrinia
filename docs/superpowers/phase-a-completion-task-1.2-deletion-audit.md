# Phase A completion — Task 1.2 deletion audit

## Result

The deletion audit contains 47 paths. Nine historical report/package objects were restored as
tracked files; the remaining 38 paths are the explicitly retired merge surface. No user source or
test work was deleted. The nine substantive report/package files are present in the index and their
object IDs and SHA-256 values are recorded below.

## 47-path inventory

`70e1831` is the source revision for every entry. A dash in the SHA-256 column means the path was
intentionally retired and therefore has no restored working-tree bytes.

| Path | Disposition | Source object | SHA-256 if restored |
|---|---|---|---|
| `src/Scrinia.Core/Merge/KeywordJaccard.cs` | intentionally retired merge surface | `de8e776e3e9f48f3ffbf8fe652c89014165eed9b` | — |
| `src/Scrinia.Core/Merge/SidecarAdmission.cs` | intentionally retired merge surface | `823d94c8aebe3058f246cdb99bfed30f517eef2a` | — |
| `src/Scrinia.Core/Merge/SidecarAttributeMerge.cs` | intentionally retired merge surface | `d10fa46a71ce6723d763c589c9f2ce5826fe14be` | — |
| `src/Scrinia.Core/Merge/SidecarKeywordMerge.cs` | intentionally retired merge surface | `be5d1d7c039387ae420ece7592e7294f2f6ee997` | — |
| `src/Scrinia.Core/Merge/SidecarTermFrequencyMerge.cs` | intentionally retired merge surface | `d99eb7e499d3078a81deb2c263c31a8adb803913` | — |
| `src/Scrinia.Core/Merge/SidecarTimestampMerge.cs` | intentionally retired merge surface | `838dda9a9d4a81f7d0098d62ca1215acc7a2a443` | — |
| `src/Scrinia.Mcp/skills/merge-safety.md` | intentionally retired merge surface | `3111afe7757321ea43166a405a40d0f813084300` | — |
| `src/Scrinia.Merge/GitConfigRepair.cs` | intentionally retired merge surface | `6a6977a9389ad5b11234507140de2504b63831db` | — |
| `src/Scrinia.Merge/KeywordJaccard.cs` | intentionally retired merge surface | `3565cfb77d0481ded1e0abd1bd6a6ee084d280d6` | — |
| `src/Scrinia.Merge/MergeConfig.cs` | intentionally retired merge surface | `15f235ede04d3e077bac56add43591727bfbd3f1` | — |
| `src/Scrinia.Merge/MetaJsonMerger.cs` | intentionally retired merge surface | `e909052a5cc120518e0d4b9e859a8476681a46ed` | — |
| `src/Scrinia.Merge/Nmp2ConflictHandler.cs` | intentionally retired merge surface | `940f43d12a6d2ee5932ce75db1ff51dc6a9ebc0f` | — |
| `src/Scrinia.Merge/Program.cs` | intentionally retired merge surface | `0ae7390dd93ae1a67cb0b96e4f1f155d774d6980` | — |
| `src/Scrinia.Merge/Scrinia.Merge.csproj` | intentionally retired merge surface | `6e60d96fc5bc89f5b8117b3137a1c3b95e529d61` | — |
| `src/Scrinia.Merge/SidecarAttributeMerge.cs` | intentionally retired merge surface | `3d3abb9b9e952ca8e99ebf37807a2f083495d83e` | — |
| `tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs` | intentionally retired merge surface | `af2920e8fac9e87b14b325fdf8444c7e157fc66e` | — |
| `tests/Scrinia.Merge.Tests/GitMergeHarness.cs` | intentionally retired merge surface | `54366aa1e84fb248b41dadb560db18f52ce0c8a4` | — |
| `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs` | intentionally retired merge surface | `8e4abeb8b2d0475c385042214c0d74ff76d986c2` | — |
| `tests/Scrinia.Merge.Tests/KeywordJaccardParityTests.cs` | intentionally retired merge surface | `215331bb9910ad8b50e9d58083dd30ca17383fd8` | — |
| `tests/Scrinia.Merge.Tests/KeywordJaccardTests.cs` | intentionally retired merge surface | `484af2a21000f34c856707d6773c4f7ee31e3cb1` | — |
| `tests/Scrinia.Merge.Tests/MergeConfigTests.cs` | intentionally retired merge surface | `f2bc906583ed1e978338bf4f970bc5b16dd7e462` | — |
| `tests/Scrinia.Merge.Tests/MetaJsonMerger3WayTests.cs` | intentionally retired merge surface | `ab98fb82fb946d6c5e11e975553c7580b7930a6d` | — |
| `tests/Scrinia.Merge.Tests/MetaJsonMergerTests.cs` | intentionally retired merge surface | `c60494aeb8c51ec90cc4f1a3a5aea2a91e43dff8` | — |
| `tests/Scrinia.Merge.Tests/Nmp2ConflictHandlerTests.cs` | intentionally retired merge surface | `96b8caad54b058d3de38d3c5402c2b5f47f5c1aa` | — |
| `tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj` | intentionally retired merge surface | `c4949808f10861068f12088b690b98a2f66d89bb` | — |
| `tests/Scrinia.Merge.Tests/SidecarAdmissionParityTests.cs` | intentionally retired merge surface | `8d3fcc039e0ebc27f65e13588852d889a6eb8508` | — |
| `tests/Scrinia.Merge.Tests/SidecarAttributeMergeParityTests.cs` | intentionally retired merge surface | `c55fbdab37a7228d75cdaf68587bb6618fd704e8` | — |
| `tests/Scrinia.Merge.Tests/SidecarKeywordMergeParityTests.cs` | intentionally retired merge surface | `b00ac18fe29bfa0c56ff29ab2916c6d1b46cd845` | — |
| `tests/Scrinia.Merge.Tests/SidecarTermFrequencyMergeParityTests.cs` | intentionally retired merge surface | `7834fe69a047c36759fc272f4643796049367d51` | — |
| `tests/Scrinia.Merge.Tests/SidecarTimestampMergeParityTests.cs` | intentionally retired merge surface | `50fb3aa09ad67a06ec26f38f6113f6a30ad727ad` | — |
| `tests/Scrinia.Merge.Tests/xunit.runner.json` | intentionally retired merge surface | `8fb464f682ecabe7528cd1702f0795f8c8d38d0a` | — |
| `tests/Scrinia.Tests/Golden/__snapshots__/reconcile-scan.yaml` | intentionally retired merge surface | `e8a80a5a5b8b13046149a7ebe14462ef1df493d9` | — |
| `tests/Scrinia.Tests/MergeConfigRoundTripTests.cs` | intentionally retired merge surface | `750e8d6afe44c38150e2b3151f6ee1132f5a0cda` | — |
| `tests/Scrinia.Tests/MetaMergeSymmetricParityTests.cs` | intentionally retired merge surface | `a78ef757b05bf7a923917e6ee856ddef92d10e75` | — |
| `tests/Scrinia.Tests/ReconcileConflictCopyTests.cs` | intentionally retired merge surface | `136253080eb02717932eb5b69f3accbae1613e64` | — |
| `tests/Scrinia.Tests/ReconcileDurableFormatTests.cs` | intentionally retired merge surface | `248385eb34ae0e5b9707616bfa4c2e1399cc064a` | — |
| `tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs` | intentionally retired merge surface | `08596a8998759e2eca4a56efb71984e820d42327` | — |
| `tests/Scrinia.Tests/ReconcileScanTests.cs` | intentionally retired merge surface | `0734c88a294b1506b650f359783790ea7a04f6ff` | — |
| `task-2-report.md` | restored required historical artifact | `797e1c46d2433a3600479a8547b6f31fc499743d` | `403cb239cdbc86afc517b41d479e624a3cd43cadac86715cf3de143289f9b8b7` |
| `task-3-report.md` | restored required historical artifact | `6fb88595d77c38ef390aef88870bc567652352ae` | `6315c1fa36e9aacb52f7835dace185df83983a4b3c3c9a45cfd86d58b47c0f01` |
| `task-4-report.md` | restored required historical artifact | `dffc6cbcdc857a93e4362f425803722321ff5e8e` | `d8470774f64cd559c5f52433bec0b46ca3a0837650716a0ac28fd321929b363f` |
| `task-5-report.md` | restored required historical artifact | `6b9ffd372bdd7f4a229593b49dfab64b0a98f6d4` | `548dfba81221a4a17f76f9b5f385c2a7e96a7e6f48b2dd738d05c1c8afc19125` |
| `task-5-review-package.md` | restored required historical artifact | `e02502a3c75382da191499796c1e44bfaf073948` | `77dbec2a8b95bed2f16feb93e59a50a2acc96a51aa2d02d10af17a844ead479e` |
| `task-6-report.md` | restored required historical artifact | `f78ad21aff227cf4fdf7b8517581fb654ccc3824` | `23081fb65b02af19f4b13e31c69f818119d131e529fdd6face61771f12f57e67` |
| `task-10-report.md` | restored required historical artifact | `63419b1e47005443eeec087632c9d3ae962bc55b` | `35ab060003710c371b10094d9db707f8ee03134eb2f6d704a5585214c2fc475c` |
| `task-10-review-package.md` | restored required historical artifact | `63bc734570d2a33be4827f3f82874ed77cc1e79f` | `add5730f71a24c11d1a7662f42048dfae68c2bfbcacbd8ac44cb564d86ef5671` |
| `task-11-report.md` | restored required historical artifact | `0b4435fad93e37dcfab2793125a3144dc0d8427e` | `0a534555ff1b352c6c25f82aedb7e3678dd397feab367f9772f4fb2c31343404` |

## Nine substantive report/package files

The complete set is present byte-for-byte as the tracked blobs at `HEAD` (the working tree may
apply the repository’s line-ending filter). `task-3-report.md` is intentionally left untouched if
user work has changed its working-tree bytes.

| Path | HEAD object | SHA-256 of tracked bytes |
|---|---|---|
| `task-2-report.md` | `797e1c46d2433a3600479a8547b6f31fc499743d` | `403cb239cdbc86afc517b41d479e624a3cd43cadac86715cf3de143289f9b8b7` |
| `task-3-report.md` | `6fb88595d77c38ef390aef88870bc567652352ae` | `6315c1fa36e9aacb52f7835dace185df83983a4b3c3c9a45cfd86d58b47c0f01` |
| `task-4-report.md` | `dffc6cbcdc857a93e4362f425803722321ff5e8e` | `d8470774f64cd559c5f52433bec0b46ca3a0837650716a0ac28fd321929b363f` |
| `task-5-report.md` | `6b9ffd372bdd7f4a229593b49dfab64b0a98f6d4` | `548dfba81221a4a17f76f9b5f385c2a7e96a7e6f48b2dd738d05c1c8afc19125` |
| `task-5-review-package.md` | `e02502a3c75382da191499796c1e44bfaf073948` | `77dbec2a8b95bed2f16feb93e59a50a2acc96a51aa2d02d10af17a844ead479e` |
| `task-6-report.md` | `f78ad21aff227cf4fdf7b8517581fb654ccc3824` | `23081fb65b02af19f4b13e31c69f818119d131e529fdd6face61771f12f57e67` |
| `task-10-report.md` | `63419b1e47005443eeec087632c9d3ae962bc55b` | `35ab060003710c371b10094d9db707f8ee03134eb2f6d704a5585214c2fc475c` |
| `task-10-review-package.md` | `63bc734570d2a33be4827f3f82874ed77cc1e79f` | `add5730f71a24c11d1a7662f42048dfae68c2bfbcacbd8ac44cb564d86ef5671` |
| `task-11-report.md` | `0b4435fad93e37dcfab2793125a3144dc0d8427e` | `0a534555ff1b352c6c25f82aedb7e3678dd397feab367f9772f4fb2c31343404` |

## Required object checks

- `git cat-file -e 90827cd:failure-classification.md` exits non-zero: **no such object**. The
  baseline revision contains no `failure-classification.md` object.
- `git rev-parse 70e1831:failure-classification.md` returns
  `554b87f4f2d61302dcf52bb4ed92445989d5445a`.
- `git rev-parse HEAD:failure-classification.md` returns the same object ID, proving the restored
  content matches `70e1831` exactly.

## Deletion and status evidence

Command: `git status --porcelain -uall` (deletion rows counted by status code `D`).

Output summary: **38 modified/deleted-surface paths are intentionally absent; deletion count in
the current worktree is 0.** The current worktree has unrelated/user modifications and untracked
append work; those entries were not removed.

Command: `git diff --name-only --diff-filter=D 70e1831 HEAD | wc -l`

Output: `38` (the intentional retired merge surface listed above).

## Exact verification commands and outputs

- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~NoObsoleteAddressSurfaceTests --no-restore` — **PASS**, 7 passed, 0 failed (2,565 existing build warnings; the two invalid absent-file allowlist entries were removed).
- `dotnet build Scrinia.sln --no-restore` — **PASS**, 13 projects, 0 errors, 2,566 existing
  warnings in the current checkout.
- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` — **2,252 total: 2,244 passed, 0 failed,
  8 skipped** at the authoritative Task 11 verification revision.
- `dotnet test Scrinia.sln` — **2,683 total: 2,675 passed, 0 failed, 8 skipped** at the
  authoritative Task 11 verification revision.
