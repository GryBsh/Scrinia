# Supply chain & CI/CD hardening — design

**Date:** 2026-07-30
**Status:** Approved (design); implementation plan pending
**Stream:** 3 of 7 from the 2026-07-29 whole-codebase review remediation

## Context

Stream 3 eliminates the three vulnerable transitive dependencies flagged by the review
(and confirmed live by `NuGetAudit` errors during restore) and closes the CI/release/Docker
gaps. Current safe versions and compatibility were established by a dedicated research pass;
notably the SQLite fix is a **same-branch 2.1.x patch, not the breaking 3.x jump** the first
review pass suggested.

The repo has **no Central Package Management** and `Directory.Build.props` sets
`TreatWarningsAsErrors=true` for non-test/benchmark projects — which is why the audit's
`NU1902/NU1903` restore warnings become build-blocking errors today (the source of the
`-p:NuGetAudit=false` workaround).

## Group A — dependency CVE remediation (direct overrides)

Fix each vulnerable transitive package with a direct top-level `PackageReference` at the
fixed version, placed in the project that pulls it in. Central Package Management is **not**
adopted — three packages across two non-overlapping projects gain nothing from it and it
would add repo-wide plumbing (simplicity preferred).

| Package | Current | → Fixed | Advisory (severity) | Owning project | Parent (floor) |
|---|---|---|---|---|---|
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.11 | **2.1.12** | CVE-2025-6965 (critical) | `src/Scrinia.Server/Scrinia.Server.csproj` | `Microsoft.Data.Sqlite 10.0.0` (`>=2.1.11`) |
| `Microsoft.OpenApi` | 2.0.0 | **2.7.5** | CVE-2026-49451 (high) | `src/Scrinia.Server/Scrinia.Server.csproj` | `Microsoft.AspNetCore.OpenApi 10.0.0` (`>=2.0.0`) |
| `MessagePack` | 2.5.192 | **2.5.302** | CVE-2026-48515 + companions | `src/Scrinia.AppHost/Scrinia.AppHost.csproj` | `Aspire.Hosting.AppHost 13.1.2` → `StreamJsonRpc` (`>=2.5.192`) |

- **SQLite:** 2.1.12 bundles SQLite 3.53.3 (clears CVE-2025-6965 and the related
  CVE-2025-70873). `Microsoft.Data.Sqlite 10.0.0` floors at `>=2.1.11` with no upper bound,
  so the override resolves cleanly and drags `SQLitePCLRaw.lib.e_sqlite3 >=2.1.12` along.
  A 3.x jump is explicitly a breaking change per the EF Core team — avoided.
- **OpenApi:** 2.7.5 is a same-major (2.x) patch. The server uses only `AddOpenApi()` /
  `MapOpenApi()` — **no custom `IOpenApi*Transformer`** — so the transformer-shape changes
  across 2.x minors don't apply. Negligible risk.
- **MessagePack:** 2.5.302 (2.5.301 fixes the named CVE; 2.5.302 also clears a companion
  advisory). Same 2.5.x line, security-only.

**Success criterion:** the vulnerable-package audit no longer fires — `dotnet restore`
(and, where the Aspire workload is present, `dotnet build Scrinia.sln`) succeeds **without**
`-p:NuGetAudit=false`, and `dotnet list <project> package --vulnerable --include-transitive`
reports none of the three. The Server test suites still pass (SQLite data path and the
`/openapi` document build remain intact).

## Group B — CI/CD

### B1. CI runs all unit-test projects
`.github/workflows/ci.yml` tests only `Scrinia.Tests` and `Scrinia.Server.Tests`. Add the
three missing unit-test projects so all five run: `Scrinia.Merge.Tests`,
`Scrinia.Plugin.Embeddings.Tests`, `Scrinia.Retrieval.Benchmarks.Tests`. (`Scrinia.Benchmarks`
is a BenchmarkDotNet perf harness, not a unit-test project — excluded.) If a project
genuinely requires an external resource CI cannot provide, exclude it with a stated reason
in the workflow — that is correct scoping, not a deferral.

### B2. Release gated on tests
`.github/workflows/release.yml` publishes CLI/server/Docker artifacts with **no test gate**.
Add a `test` job (restore + build + run all five test projects) and make `build-cli`,
`build-server`, and `build-docker` depend on it via `needs: test` — no publish without a
green suite.

## Group C — Dockerfile hardening

**File:** `src/Scrinia.Server/Dockerfile`

### C1. Inherit central build settings
The restore stage copies `Directory.Build.targets` but not `Directory.Build.props`, so the
container build uses different version/analysis/warnings-as-errors settings than local/CI.
Add `COPY Directory.Build.props ./`. This must land **after** Group A — with the props file
present, the in-container build enforces `TreatWarningsAsErrors` (hence audit-as-error), so
it only stays green once the CVEs are fixed.

### C2. Verify model downloads
The build downloads three model files via `curl` from Hugging Face `resolve/main/` (a
mutable ref) with no integrity check. Pin each URL to a specific commit revision
(`resolve/<sha>/`) and verify each file against a known SHA-256 (`sha256sum -c`). The
implementer resolves the current commit SHA of each HF repo, downloads each file, computes
its SHA-256, and pins both.

## Ordering

A (deps) → C1 (Docker props) → B/C2. Deps first because they unblock the audit that C1's
in-container `TreatWarningsAsErrors` would otherwise trip.

## Testing / validation

- **A:** audit no longer fires without the flag; `--vulnerable` lists clean; Server suites green.
- **B:** each of the five `dotnet test <project>` commands runs green locally; workflow YAML is valid.
- **C2:** the pinned URL + `sha256sum -c` verification is run locally (download at the pinned
  SHA, confirm the recorded hash matches).
- **C1 / full Docker image:** the image build is heavy and may not run locally; its structure
  is verified and the release CI exercises it end-to-end. If `docker` is available locally,
  the implementer attempts the build.

## Non-goals (other streams)

Streams 4 (retrieval correctness), 5 (secrets & transport), 6 (web), 7 (docs) remain their
own specs. Streams 1 (containment & authz) and 2 (data integrity) are merged.

## Risks

- **A:** OpenApi transformer compat — confirmed none present; implementer still smoke-checks
  that `/openapi` builds. SQLite/MessagePack are same-line patches.
- **C1** could surface a container-only warning (different settings than local); the
  implementer builds the image (or, if docker is unavailable, reasons about it) before merge.
- **C2** hashes are computed at the pinned SHA and stable thereafter; if an HF repo lacks a
  stable revision, pin the newest commit SHA available and record it.
