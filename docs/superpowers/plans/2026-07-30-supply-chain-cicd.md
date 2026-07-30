# Supply chain & CI/CD hardening — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Remove the three dependency CVEs (so `NuGetAudit` passes without the workaround), gate the release on tests, run all unit-test projects in CI, and harden the Dockerfile.

**Architecture:** Direct transitive-override `PackageReference`s at the fixed versions (no Central Package Management). Add missing CI test projects and a release test gate. Copy `Directory.Build.props` into the Docker build and pin+checksum model downloads.

**Tech Stack:** .NET 10 / NuGet, GitHub Actions YAML, Docker multi-stage, curl + sha256sum.

## Global Constraints

- **Build/test:** these fixes REMOVE the need for `-p:NuGetAudit=false`. Validate BOTH with and without the flag as noted per task. Never rely on `-p:NuGetAudit=false` to hide a real audit failure after Group A.
- `src/` projects are `TreatWarningsAsErrors=true` (via root `Directory.Build.props`); test/benchmark projects are exempt.
- No Central Package Management — versions live in individual `.csproj`.
- Conventional-commit messages. End every commit message with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Do not touch / never stage** the user's uncommitted working-tree files: `.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `src/Scrinia.Mcp/prompts/guide.md`. Stage only each task's own files.
- Work from repo root: `C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia`
- Defer nothing beyond the spec's stated non-goals; a final-review finding is fixed in-branch.

## File Structure
- Modify `src/Scrinia.Server/Scrinia.Server.csproj` + `src/Scrinia.AppHost/Scrinia.AppHost.csproj` (T1)
- Modify `src/Scrinia.Server/Dockerfile` (T2)
- Modify `.github/workflows/ci.yml` + `.github/workflows/release.yml` (T3)

**Order:** T1 first (unblocks the audit). T2, T3 independent of each other, both after T1.

---

### Task T1: Dependency CVE remediation (transitive overrides)

**Files:**
- Modify: `src/Scrinia.Server/Scrinia.Server.csproj`
- Modify: `src/Scrinia.AppHost/Scrinia.AppHost.csproj`

**Interfaces:** none (build/dependency change). No unit test — validation is the audit-clean restore/build + the `--vulnerable` listing + the existing suites.

- [ ] **Step 1: Add the Server overrides**

In `src/Scrinia.Server/Scrinia.Server.csproj`, in the existing package `ItemGroup` (the one with `Microsoft.Data.Sqlite`), add:

```xml
<!-- Transitive-override security fixes: pin the patched versions of packages pulled in
     via Microsoft.Data.Sqlite / Microsoft.AspNetCore.OpenApi. -->
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.12" /> <!-- CVE-2025-6965 -->
<PackageReference Include="Microsoft.OpenApi" Version="2.7.5" />             <!-- CVE-2026-49451 -->
```

- [ ] **Step 2: Add the AppHost override**

In `src/Scrinia.AppHost/Scrinia.AppHost.csproj`, in the `ItemGroup` with `Aspire.Hosting.AppHost`, add:

```xml
<!-- Transitive-override security fix: CVE-2026-48515 (MessagePack via StreamJsonRpc). -->
<PackageReference Include="MessagePack" Version="2.5.302" />
```

- [ ] **Step 3: Verify the audit is cleared (the load-bearing validation)**

Run each and record output:

```bash
# Restore now succeeds WITHOUT the workaround (previously failed on the audit):
dotnet restore src/Scrinia.Server/Scrinia.Server.csproj
dotnet restore src/Scrinia.AppHost/Scrinia.AppHost.csproj
# No vulnerable packages remain for the three CVEs:
dotnet list src/Scrinia.Server/Scrinia.Server.csproj package --vulnerable --include-transitive
dotnet list src/Scrinia.AppHost/Scrinia.AppHost.csproj package --vulnerable --include-transitive
```

Expected: restores succeed with no `NU1902/NU1903` errors; the `--vulnerable` listings show **none** of `SQLitePCLRaw.lib.e_sqlite3`, `Microsoft.OpenApi`, `MessagePack` (other unrelated advisories, if any, are out of scope — note them, don't fix here). If the Aspire workload is not installed and `AppHost` restore fails for a NON-audit reason, note it and rely on the `--vulnerable` listing + the removed audit error as the proof for MessagePack.

- [ ] **Step 4: Confirm no build/runtime regression**

```bash
dotnet build src/Scrinia.Server/Scrinia.Server.csproj -c Release   # NO -p:NuGetAudit=false — must be 0 warnings/0 errors
dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false
```

Expected: Server builds clean **without** the audit flag (proves the audit-as-error is gone), and the Server suite passes (SQLite data path + `/openapi` build intact). Also smoke-check the OpenApi document endpoint builds (the suite's `OpenApiTests` covers this — confirm it's green).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Scrinia.Server.csproj src/Scrinia.AppHost/Scrinia.AppHost.csproj
git commit -m "fix(deps): pin patched SQLitePCLRaw/OpenApi/MessagePack to clear CVEs"
```

---

### Task T2: Dockerfile hardening (Directory.Build.props + model checksums)

**Files:**
- Modify: `src/Scrinia.Server/Dockerfile`

**Interfaces:** none. Validation is local (props copy is structural; model checksums verified by download+`sha256sum -c`; full image build if docker is available).

- [ ] **Step 1: Copy `Directory.Build.props` into the restore stage**

In `src/Scrinia.Server/Dockerfile`, immediately after the line `COPY Directory.Build.targets ./` (line 3), add:

```dockerfile
COPY Directory.Build.props ./
```

(This makes the container build inherit the central version/analysis/warnings-as-errors — safe only now that T1 cleared the audit.)

- [ ] **Step 2: Resolve pinned revisions + compute checksums**

For each of the three model files, resolve the HF repo's current commit SHA and compute the file's SHA-256. Run (adjust as needed):

```bash
# Example for one file — repeat for all three:
curl -fSL -o /tmp/model.safetensors "https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/main/model.safetensors"
sha256sum /tmp/model.safetensors
# Resolve the repo's current commit SHA (HF API):
curl -fsSL "https://huggingface.co/api/models/grybsh/m2v-MiniLM-L6-v2" | grep -o '"sha":"[a-f0-9]*"'
```

Files: `grybsh/m2v-MiniLM-L6-v2` → `model.safetensors`, `vocab.txt`; `second-state/All-MiniLM-L6-v2-Embedding-GGUF` → `all-MiniLM-L6-v2-Q8_0.gguf`. Record the resolved SHA(s) and each file's sha256.

- [ ] **Step 3: Pin the URLs to the revision + verify checksums**

Replace the `model-download` stage's `curl` commands so each downloads from `resolve/<commit-sha>/` (not `main`) and verifies its checksum. Pattern:

```dockerfile
RUN mkdir -p /models/m2v-MiniLM-L6-v2 && \
    curl -fSL -o /models/m2v-MiniLM-L6-v2/model.safetensors \
      "https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/<SHA>/model.safetensors" && \
    echo "<sha256>  /models/m2v-MiniLM-L6-v2/model.safetensors" | sha256sum -c - && \
    curl -fSL -o /models/m2v-MiniLM-L6-v2/vocab.txt \
      "https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/<SHA>/vocab.txt" && \
    echo "<sha256>  /models/m2v-MiniLM-L6-v2/vocab.txt" | sha256sum -c -
RUN curl -fSL -o /models/all-MiniLM-L6-v2-Q8_0.gguf \
      "https://huggingface.co/second-state/All-MiniLM-L6-v2-Embedding-GGUF/resolve/<SHA>/all-MiniLM-L6-v2-Q8_0.gguf" && \
    echo "<sha256>  /models/all-MiniLM-L6-v2-Q8_0.gguf" | sha256sum -c -
```

Use the real SHAs/hashes from Step 2. (`sha256sum -c` exits non-zero on mismatch, failing the build — the intended integrity gate.)

- [ ] **Step 4: Validate**

Verify the checksum logic actually holds by running the pinned download + `sha256sum -c` for at least one file locally (must print `OK`). If `docker` is available, run `docker build -f src/Scrinia.Server/Dockerfile -t scrinia-test .` and confirm it reaches at least past the `model-download` stage (a full build is heavy; getting past model-download + restore validates C1+C2). If docker is unavailable, state that and rely on the local checksum verification + structural review.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Dockerfile
git commit -m "fix(docker): inherit Directory.Build.props; pin+checksum model downloads"
```

---

### Task T3: CI runs all test projects + release test gate

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/release.yml`

**Interfaces:** none. Validation: each `dotnet test <project>` runs locally; YAML is valid.

- [ ] **Step 1: Confirm the added test projects run clean locally**

```bash
dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false
dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj -p:NuGetAudit=false
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj -p:NuGetAudit=false
```

Expected: all pass. If `Scrinia.Plugin.Embeddings.Tests` (or another) needs an external resource CI can't provide (e.g. a downloaded model), note exactly what and either (a) confirm CI already provides it, or (b) exclude that project in the workflow with an inline comment stating why. Do not silently drop coverage.

- [ ] **Step 2: Add the three missing test steps to `ci.yml`**

After the existing `Test (Scrinia.Server.Tests)` step, add (mirroring the existing step style; drop `-c Release`/`--no-build` only if a project isn't part of the Release solution build):

```yaml
      - name: Test (Scrinia.Merge.Tests)
        run: dotnet test tests/Scrinia.Merge.Tests/ --no-build -c Release
      - name: Test (Scrinia.Plugin.Embeddings.Tests)
        run: dotnet test tests/Scrinia.Plugin.Embeddings.Tests/ --no-build -c Release
      - name: Test (Scrinia.Retrieval.Benchmarks.Tests)
        run: dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/ --no-build -c Release
```

(If any project is excluded per Step 1, omit its step and leave the inline reason.)

- [ ] **Step 3: Add a `test` job + `needs:` gate to `release.yml`**

Add a `test` job at the top of `jobs:` that checks out, sets up .NET 10, restores, builds `-c Release`, and runs the five test projects (mirror `ci.yml`'s steps). Then add `needs: test` to each of `build-cli`, `build-server`, and `build-docker` so no artifact publishes without a green suite. Example `test` job:

```yaml
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
          include-prerelease: true
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test tests/Scrinia.Tests/ --no-build -c Release
      - run: dotnet test tests/Scrinia.Server.Tests/ --no-build -c Release
      - run: dotnet test tests/Scrinia.Merge.Tests/ --no-build -c Release
      - run: dotnet test tests/Scrinia.Plugin.Embeddings.Tests/ --no-build -c Release
      - run: dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/ --no-build -c Release
```

Then on each existing build job add the line `needs: test` (at the job level, alongside `runs-on`/`strategy`).

- [ ] **Step 4: Validate the YAML**

Confirm both workflow files parse as valid YAML (e.g. `python -c "import yaml,sys; yaml.safe_load(open(sys.argv[1]))" .github/workflows/ci.yml` for each, or an equivalent linter). Confirm the `needs: test` was added to all three build jobs and the `test` job's test list matches CI's.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ci.yml .github/workflows/release.yml
git commit -m "ci: run all unit-test projects; gate release publish on tests"
```

---

## Final verification

- [ ] Deps audit-clean without the workaround:

```bash
dotnet restore src/Scrinia.Server/Scrinia.Server.csproj && dotnet restore src/Scrinia.AppHost/Scrinia.AppHost.csproj
dotnet list src/Scrinia.Server/Scrinia.Server.csproj package --vulnerable --include-transitive
dotnet list src/Scrinia.AppHost/Scrinia.AppHost.csproj package --vulnerable --include-transitive
```

- [ ] All five test projects green (`-p:NuGetAudit=false` still fine to pass explicitly, but the Server build must ALSO be clean without it).
- [ ] Both workflow YAMLs valid; `needs: test` on all three release build jobs.
- [ ] Dockerfile: `Directory.Build.props` copied; all three model downloads pinned to a revision with `sha256sum -c` verification.
- [ ] `git status` shows only this plan's files — the user's four working-tree files remain untouched.
