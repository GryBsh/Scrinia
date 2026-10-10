# Scrinia release readiness

**UNVERIFIED** — assessed 2026-10-10T20:46:18.6475913+00:00. Release target is product/test commit **0db7e7a9900b52cdffc75ce4960aac54e7f6b1fe**, stable **scrinia 0.6.0**, intended tag **v0.6.0**. Comparison foundation is a3dbc5fe1ee378f0c4d53d9833d13080815848a8. This report's later documentation commit is not a newly validated product candidate. Scope is all units in [RELEASE-REQUIREMENTS.md](RELEASE-REQUIREMENTS.md).

Windows and Linux execution outcomes below are measured at that exact clean detached source, SDK **10.0.203** / runtime **10.0.7**, process **X64**: Windows 10.0.26340 / win-x64 and Ubuntu 24.04.5 LTS / linux-x64. No macOS osx-arm64 lane, exact-SHA eligible seven-artifact remote aggregate, release tag/run authorization or destination ownership evidence exists. These unresolved requirements prevent READY. No requirement is waived or marked N/A.

The one fresh managed package is **3918006 bytes**, SHA256 **98bb80ecfb64c53b783eaed8e48f57b233a83d2b82a354cdc78b5b2983723ab5**, below the 128 MiB receiving bound. Nuspec names this exact source commit; tool settings expose only scri. Both strict native lanes consume these identical bytes. Packages, manifests, complete logs and actual TRXs are in [docs/reviews/release-readiness/20261010-final-0db7e7a9](docs/reviews/release-readiness/20261010-final-0db7e7a9/portable-transfer-summary.json).

Complete owned native checkout/output retention is **573376430 bytes**, SHA256 **f4ae492c9e8b8e6c59da2ffbdd73107151bb2678edc30d52bf0c96f345834368**, independently matched between native source and retained destination. Native archive artifact transfers likewise match source/destination hashes.

## Requirement results

| ID | Classification | Status | Evidence / remaining action |
| --- | --- | --- | --- |
| RR-BLD-01 | Required | PASS | Fresh nonincremental build, exact SDK/SHA; build.outcome.json and complete log. |
| RR-BLD-02 | Required | PASS | 0 warnings/errors; reviewed narrow suppression policy. |
| RR-TST-01 | Required | PASS | Unfiltered final solution: 5409 passed / 0 failed / 58 skipped across 3 actual TRXs; actual-trx-summary.json/full-skip-reasons.txt. |
| RR-TST-02 | Required | PASS | Approved isolation/guards, final full suite; native historical set 5 passed / 0 failed / 1 expected Windows-harness skip. |
| RR-CI-01 | Required | PASS | Tracked topology/source inventory, configured jobs and complete Check-ReleaseContracts. |
| RR-CI-02 | Required | UNKNOWN | Fresh Windows/Linux local gates recorded; no actual osx-arm64 lane or eligible exact-SHA remote seven-artifact aggregate/run. Obtain native macOS and exact run evidence. |
| RR-CI-03 | Required | PASS | Exactly seven disk/documented/packaged resources, complete contracts and package-resources.json. |
| RR-CI-04 | Required | PASS | Actual CS1591 diagnostic build; XML emission/API coverage remains unconfigured. |
| RR-PKG-01 | Required | PASS | Fresh stable scrinia 0.6.0 nuspec exact source SHA; sole scri command; single-source contracts. |
| RR-PKG-02 | Required | PASS | 52 managed ZIP entries / seven approved skills; no native/model/workspace/retired payload; actual immutability checks and three fresh native FIFO/socket refusal cases. |
| RR-PKG-03 | Required | PASS | One fresh pack, 3918006 bytes, SHA256 98bb80ecfb64c53b783eaed8e48f57b233a83d2b82a354cdc78b5b2983723ab5; both native lanes consume exact bytes. |
| RR-PKG-04 | Required | PASS | Strict Windows trimmed plain/embeddings publishes and six native source/hash/loader proofs. |
| RR-PKG-05 | Required | PASS | Strict native Linux real Git checkout, native modes/archive/hash/loader/setup/lifecycle; full retained root verified. |
| RR-PKG-06 | Required | UNKNOWN | No actual macOS osx-arm64 execution evidence; run the exact candidate/package native lane. |
| RR-PKG-07 | Required | PASS | Both local lanes complete 32-stage package/extracted lifecycle: update, both stdio, real shared operations, installed plugin, immutability/uninstall. |
| RR-PKG-08 | Required | PASS | Strict Windows ZIP exact members/native hashes, extraction/invocation/setup and manifest. |
| RR-DOC-01 | Required | PASS | Reviewed bundled version/RID guidance and actual package/extracted plugin discovery; Unix companion guidance corrected. |
| RR-DOC-02 | Required | PASS | Supplied scoped recovery/documentation review and corrected living source; uncertainty retention distinguished from agent context restore. |
| RR-REC-01 | Required | PASS | Accepted scoped tracking/config admission, compensation/fault/restart/journal review; corresponding final default tests. |
| RR-REC-02 | Required | PASS | Accepted bounded bundle/file-entity caller/admission/atomicity/retention review; corresponding final default tests. |
| RR-COMP-01 | Required | PASS | Accepted strict integrity/body scanner review; final default malformed/empty/LF/CRLF chunk and bundle tests. |
| RR-API-01 | Required | PASS | Public IMemoryStore base-to-product unchanged, tracked transport/package review, measured workspace/shared local stdio handshakes. |
| RR-REL-01 | Candidate | UNKNOWN | Stable local identity is consistent; exact commit/Actions/tag/release/NuGet queries establish no eligible remote run or destination authorization. Resolve owner/run/tag/environment approvals. |
| RR-REL-02 | Candidate | PASS | Approved nested version/RID plugin layout implemented in publisher/runtime/docs and demonstrated by actual Windows/Linux setup; macOS remains PKG-06. |
| RR-ADV-01 | Advisory | PASS | Current advisory response across 14 projects has zero reported vulnerabilities; no native/unknown vulnerability certification. |
| RR-ADV-02 | Advisory | PASS | Supplied scoped docs/pipeline/UX polish reviews, accepted five-finding remediation; zero new scoped findings. |
| RR-ADV-03 | Advisory | PASS | Reviewed monotonic optional-CR scanner and final regression coverage; no performance benchmark or latency threshold claimed. |

## Executed checks and artifacts

Commands/arguments, identities, clean postchecks, timings and full logs are retained per stage as *.outcome.json/stdout.log/stderr.log. All available checks ran after the corrected commit; earlier focused composites or source review were not substituted for this unfiltered suite.

The mandatory full suite totals **5409 passed / 0 failed / 58 skipped**: Scrinia.Tests **5072 / 0 / 58**, Scrinia.Plugin.Embeddings.Tests **65 / 0 / 0**, and Scrinia.Retrieval.Benchmarks.Tests **272 / 0 / 0**. All three summaries are Completed; total actual result records are **5467**.

| Stage | Exit | Seconds | Started UTC | Ended UTC |
| --- | --- | --- | --- | --- |
| audit | 0 | 8.768 | 2026-10-10T20:05:34.0428295Z | 2026-10-10T20:05:42.8097651Z |
| build | 0 | 46.246 | 2026-10-10T20:02:39.8331351Z | 2026-10-10T20:03:26.0781244Z |
| contracts | 0 | 3.869 | 2026-10-10T20:40:54.0193162Z | 2026-10-10T20:40:57.8870340Z |
| full | 0 | 2067.045 | 2026-10-10T20:04:55.1455079Z | 2026-10-10T20:39:22.1896931Z |
| identity | 0 | 0.538 | 2026-10-10T20:02:36.4289200Z | 2026-10-10T20:02:36.9662077Z |
| linux | 0 | 392.112 | 2026-10-10T20:04:55.0474471Z | 2026-10-10T20:11:27.1593201Z |
| pack | 0 | 18.332 | 2026-10-10T20:03:33.6507240Z | 2026-10-10T20:03:51.9821119Z |
| windows | 0 | 148.874 | 2026-10-10T20:41:38.4247224Z | 2026-10-10T20:44:07.2978779Z |
| xml | 0 | 45.633 | 2026-10-10T20:39:42.5027219Z | 2026-10-10T20:40:28.1354867Z |

| Actual TRX lane / file | Summary | Passed | Failed | NotExecuted |
| --- | --- | --- | --- | --- |
| full-trx / nickd_XEX-BOX-UN_2026-10-10_16_05_34.trx | Completed | 5072 | 0 | 58 |
| full-trx / nickd_XEX-BOX-UN_2026-10-10_16_38_53.trx | Completed | 65 | 0 | 0 |
| full-trx / nickd_XEX-BOX-UN_2026-10-10_16_39_09.trx | Completed | 272 | 0 | 0 |
| linux-native6-trx / _xex-box-un_2026-10-10_16_10_04.trx | Completed | 5 | 0 | 1 |
| linux-carrier-regression/trx / _xex-box-un_2026-10-10_16_43_44.trx | Completed | 3 | 0 | 0 |

Actual UnitTestResult outcomes are counted; Counters.notExecuted is not trusted. Full-suite skips and their measured reasons are retained in full-skip-reasons.txt and actual-trx-summary.json. Linux CloseAndExitAsync is the expected skip: its fixture explicitly uses powershell.exe and cmd.exe/.cmd; private pwsh is present, and this Windows process harness does not run on Linux. The readable cross-platform C# ProcessRunner/Staging tests remain covered by the final Windows full suite; earlier focused Windows/native proofs remain retained historical evidence. The strict native Linux lifecycle separately measures its actual process/setup/handshake paths; it is not a native unfiltered unit suite.

Of the Windows **58** actual skips, **16** require optional model/vocabulary files and **42** require native Unix/case-sensitive/platform behavior. Final Windows actual PASS explicitly includes EmbeddingsBundleStagingTests.Runner_RejectsIncompleteOutputAfterSuccessfulParentExit (the readable C# process replacement) and GlobalToolInstallTests.CloseAndExitAsync_KillsAndAwaitsTimedOutProcessTree. Exact historical antivirus heuristics/source-path denial authority remain unproven; no security configuration changed.

The controller authorized a separate bounded supplemental exact-SHA native run of only NonRegularCarriersAreRejectedBeforeOpening: **3 passed / 0 failed / 0 skipped**. Its three FIFO/socket negative cases were skipped on Windows; this run directly covers that Important fix without rerunning or changing the completed strict lane. Distinct owned scratch identity, exact filter/command, source hashes, actual TRX and verified same-invocation root archive are retained under linux-carrier-regression. No broader 58-skip expansion was requested or performed.

Supplemental complete root archive: **265169136 bytes**, SHA256 **3fa8d910bf3efeec36c74fcfbae1b60759035edf4aae33da23f9d175306499aa**, source/destination verified.

Windows and Linux lane summaries retain **43 / 43 outcomes**, including **32 / 32 lifecycle stages** respectively: isolated lower-version install/update, workspace/shared local stdio, real shared calls, installed plugin handshake, package/extracted setup, six native loader probes, immutability and uninstall. Exact artifact names, lengths/hashes, members/modes and provenance remain in native lane manifests/evidence. These checks establish the measured Windows/Linux environments; macOS lifecycle remains UNKNOWN under RR-PKG-06/RR-CI-02. These checks are not real GPU quality/inference or native vulnerability certification.

| Artifact | Bytes | SHA256 |
| --- | --- | --- |
| scrinia-0.6.0-linux-x64-embeddings.tar.gz | 54252225 | af4130bf87ccd201db8877724045f7b361e63572c58cfe6cf0557f964d7a6370 |
| scrinia-0.6.0-linux-x64-plain.tar.gz | 9506763 | 252331df547f8fff07546b75e45986a6bcd78b6e65de097ad08c20d2514b3df3 |
| scrinia-0.6.0-win-x64-embeddings.zip | 63780733 | 2fdf4e51adbb812029f2f87fe1ecfadf360445fb065c1a712c71736aca99f075 |
| scrinia-0.6.0-win-x64-plain.zip | 21731600 | d73e0f125ea3100e82490017d7322023df4f5ef836e701acad4226e39922a134 |
| scrinia.0.6.0.nupkg | 3918006 | 98bb80ecfb64c53b783eaed8e48f57b233a83d2b82a354cdc78b5b2983723ab5 |

## Reviewed changes, reconciliation and preserved attempts

The controller supplied whole-branch review and one consolidated five-finding fix review: original **0 Critical / 2 Important / 3 Minor**, all five accepted findings addressed, **zero new scoped findings**. Fixes cover shared-search overflow, Unix nonregular carriers, bounded NuGet receiving transport, Unix companion guidance and exact rendered release metadata. UX polish reported zero issues. The later fixture-only review accepted four WorkspaceSetup.Configure additions with zero findings. These are supplied review results, distinct from executor-run gates; review reports and tokens are retained under supplied-task-reports and assessment-reviewer-inventory.csv.

Original fourteen logical inputs are reconciled by original/final length, SHA256 and actual remediation commits: **seven byte-identical / seven deliberate amendments**. [input-behavior-reconciliation.md](docs/reviews/release-readiness/20261010-final-0db7e7a9/input-behavior-reconciliation.md), active-input-reconciliation.csv and active-input-remediation.diff record preserved behavior. CRLF chunk compatibility and its original regression remain; task-first help, defaults and two local stdio surfaces remain. Original runtime data, 85-entry retired-byte archive, stash, denied-source checkout and unrelated plans remain preserved outside candidate staging.

The earlier 462117cb candidate's native strict lane passed but its native-six run recorded **4 Passed / 1 Failed / 1 NotExecuted**: MoveFailure read WorkspaceSetup.Root without configuring the fresh fixture. Its Windows full run was explicitly superseded and canceled after **1140.53 s**, exit -1, with no completed TRX; it is **ABORTED**, never PASS. The separate native reproduction at original source recorded fresh single **RED 1 Failed**, corrected single **GREEN 1 Passed**, and corrected full ModelRelocationTests **13 Passed**. Its archive hash is 73dd2efa7a00b3f321adc0cc385107f7943bc19da5c71ba24b135baab93e3ede. Original failure/aborted logs remain under historical-462117cb; reproduction logs/TRXs under fixture-reproduction. The correction was committed before all gates above restarted. No historical failure/antivirus denial causation is inferred.

Original assessment bytes were preserved: RELEASE-READINESS.md 18833 bytes / SHA256 879665c3a2afed15384cdad15e7e4680107fc9fc2ed37d6b994b6e7c8e81b4a1; RELEASE-REQUIREMENTS.md 20848 bytes / SHA256 c93a72e0e9900d25f8be446f15115b64853bafd89cea80715c254b3b15e6885e. Historical source maps and plans are not current gate evidence.

## Remaining evidence and next actions

Run native osx-arm64 validation on the exact product SHA with this package hash, retain complete plain/embeddings lifecycle/artifact evidence, and obtain the exact seven-artifact aggregate from a successful eligible validation run. Read-only external checks on 2026-10-10 observed no exact commit/Actions run, no candidate tag/release or public NuGet package, main not protected/no applicable rules, and checks unavailable. Full responses and exit outcomes are retained; absence establishes neither ownership nor permission. Identify the authorized release owner and verify exact tag/run/environment attestations and destination rights before any publication. Task 6's earlier authorized local merge remains part of the preserved foundation. Subsequent remediation remains on the recovery branch; this final assessment performed no additional merge and no push, dispatch or publication.

API XML coverage remains unconfigured beyond the actual CS1591 diagnostic. Optional GPU/model quality, long benchmarks and unsupported architectures were not run; no new gates are invented. Raw Core exact-byte primitives are not intrinsically NMP validators; the reviewed facade validates, and no active production ImportTopicEntries caller was found. General transaction/marketplace/Markdown indexing/HTTP/public API expansion remain outside this remediation. Checksum/provenance proof is bounded to the tested payload, not authenticity certification.

Evidence under docs/reviews is ignored. Transfer the portable manifest's complete subset with these root documents; missing evidence must be reread/rerun rather than assumed. Candidate bundles, complete owned native checkout archives and private pinned runtimes remain under the original SDD evidence authority. The retired archive, primary checkout and stash, and blocked checkout remain at their original locations documented by the supplied authority and foundation provenance inventories. retained-authority-not-copied.csv fingerprints selected execution-tree files omitted from the portable subset; it does not inventory those separately preserved runtime, Git or archive objects. No retained authority was deleted. Personal execution paths in raw logs are diagnostic context, not shared recipes. The final detailed controller report is task-16-report.md in the original SDD, with evidence-manifest.csv. Root audit and report-only commit remain separate controller work.
