# Living Documentation Corrections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct current reference documentation for the local CLI/stdio MCP product and standalone embeddings plugin without rewriting dated history or adding unsupported claims.

**Architecture:** Treat `README.md`, top-level `docs/*.md`, and current architecture pages as living references. Compare each claim to current commands, `McpPluginHost`, plugin CLI arguments, discovery/model paths, and publish scripts; edit only confirmed drift. Historical `docs/superpowers/`, `docs/planning/`, reports, and prior changelog entries are read-only.

**Refs:** `docs/superpowers/specs/2026-08-27-0-6-0-versioning-changelog-docs-design.md` (approved documentation scope); `AGENTS.md` (living-versus-historical documentation policy); `src/Scrinia/Commands/ScriniaCommands.cs`, `src/Scrinia/Program.cs`, and `src/Scrinia.Plugin.Embeddings.Cli` (current product surfaces).
ref: `docs/superpowers/specs/2026-08-27-0-6-0-versioning-changelog-docs-design.md`

**Tech Stack:** Markdown, .NET 10 CLI, local stdio MCP, publish scripts, plugin discovery.

## Global Constraints

- “Correct only at current-reference locations.”
- “Do not rewrite historical changelog entries, dated `docs/superpowers/` artifacts, or prior reports.”
- “Do not add HTTP, REST, remote CLI, web UI, `--remote`, `--api-key`, or `--store` guidance.”
- “Plugin examples use the actual executable and discovery layout.”
- “The optional embeddings archive is documented under `plugins/scri-plugin-embeddings/`.”
- “Vulkan native replacement is described as the local publish-script step with explicit CPU fallback where applicable.”

---

## Dependencies / Order

- Depends on IMP-02 and IMP-03 for address and publish facts, and on IMP-04/IMP-01 for entity behavior terminology. Complete Task 1 before Tasks 2–3; complete this plan before the `0.6.0` release gate.
- Only living reference docs named in this plan may change. Preserve dated planning/review artifacts and preserve/exclude pre-existing untracked `docs/server-admin.md` and `docs/web-ui-guide.md`; neither is a release deliverable.
- **Ref:** `AGENTS.md` (historical artifact policy); `docs/superpowers/specs/2026-08-27-0-6-0-versioning-changelog-docs-design.md` (explicit exclusions).

### Task 1: Inventory current living-documentation drift

**Files:**
- Read: `README.md`
- Read: `docs/getting-started.md`
- Read: `docs/cli-reference.md`
- Read: `docs/plugin-authoring.md`
- Read: `docs/troubleshooting.md`
- Read: `docs/architecture/overview.md`
- Read: `docs/architecture/cli.md`
- Read: `docs/architecture/core.md`
- Read: `docs/architecture/embeddings.md`
- Read: `src/Scrinia/Commands/ScriniaCommands.cs`, `src/Scrinia/Program.cs`, `src/Scrinia.Plugin.Embeddings.Cli`, `publish.ps1`, `publish.sh`

**Interfaces:**
- Consumes: current CLI command names/options, stdio MCP setup, plugin executable/discovery/model contract, and publish layout.
- Produces: a concrete edit list naming each stale sentence, target file/heading, replacement fact, and source-of-truth implementation path.

**Ref:** `src/Scrinia/Commands/ScriniaCommands.cs`, `src/Scrinia/Program.cs`, `src/Scrinia.Plugin.Embeddings.Cli`, `publish.ps1`, `publish.sh`; `docs/superpowers/specs/2026-08-27-0-6-0-versioning-changelog-docs-design.md` (inventory method).

- [ ] **Step 1: Search living docs** for `remote`, `HTTP`, `REST`, `web UI`, `--remote`, `--api-key`, `--store`, obsolete plugin paths, incorrect bundle terminology, and unsupported runtime claims.
- [ ] **Step 2: Compare every match** with the current source and scripts; retain any statement that is historical or accurate and list only confirmed living drift for editing.
- [ ] **Step 3: Check links and command examples** against actual files and CLI entrypoints before editing.

Run: `dotnet build Scrinia.sln -c Release`

Expected: PASS; the inventory references the current build successfully and contains no speculative replacement.

- [ ] **Step 4: Keep the inventory in the implementation task record and create no standalone inventory file; the next task edits only the named living-reference lines.**

### Task 2: Update product and CLI references

**Files:**
- Modify: `README.md`
- Modify: `docs/getting-started.md`
- Modify: `docs/cli-reference.md`
- Modify: `docs/troubleshooting.md`
- Modify only confirmed stale sections: `docs/architecture/overview.md`, `docs/architecture/cli.md`, `docs/architecture/core.md`

**Interfaces:**
- Consumes: Task 1 edit list and current local CLI/stdio MCP behavior.
- Produces: accurate install, setup, command, bundle, troubleshooting, and architecture guidance.

- [ ] **Step 1: Replace removed-surface guidance** with `scri serve` over local stdio and supported `--workspace-root` usage; do not introduce a remote substitute.
- [ ] **Step 2: Correct bundle/troubleshooting terminology** to the current bundle commands, recovery behavior, and per-topic limitations.
- [ ] **Step 3: Update architecture ownership descriptions** only where they contradict current local implementation; preserve concise existing headings and style.
- [ ] **Step 4: Validate links and commands.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -c Release --filter "FullyQualifiedName~Guide|FullyQualifiedName~Config|FullyQualifiedName~Bundle"`

Expected: PASS; each changed command maps to a current CLI entrypoint and each changed link maps to an existing path.

- [ ] **Step 5: Commit product-reference corrections.**

```bash
git add README.md docs/getting-started.md docs/cli-reference.md docs/troubleshooting.md docs/architecture/overview.md docs/architecture/cli.md docs/architecture/core.md
git commit -m "docs: correct living CLI references"
```

### Task 3: Update plugin and embeddings references

**Files:**
- Modify: `docs/plugin-authoring.md`
- Modify: `docs/architecture/embeddings.md`
- Modify only plugin sections in `README.md` and `docs/getting-started.md` if Task 1 identifies drift

**Interfaces:**
- Consumes: `McpPluginHost`, plugin CLI argument handling, discovery/model paths, `publish.projects.tsv`, `publish.ps1`, and `publish.sh`.
- Produces: executable-first plugin authoring and deployment documentation matching `plugins/scri-plugin-embeddings/`, its model directory, local stdio MCP, and Vulkan fallback.

**Ref:** `src/Scrinia.Plugin.Embeddings.Cli`, `src/Scrinia.Plugin.Embeddings`, `publish.projects.tsv`, `publish.ps1`, `publish.sh`; `docs/superpowers/specs/2026-08-27-0-6-0-versioning-changelog-docs-design.md` (plugin documentation decisions).

- [ ] **Step 1: Document standalone executable operation** and local MCP stdio transport using the actual plugin command/configuration shape.
- [ ] **Step 2: Document nested discovery layout** exactly as `plugins/scri-plugin-embeddings/`, including executable name and model directory.
- [ ] **Step 3: Document Vulkan replacement** as the publish-script behavior for Windows/Linux and explicit CPU fallback for macOS or missing native packages.
- [ ] **Step 4: Validate plugin examples** against source and publish scripts rather than prose assumptions.

Run: `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj -c Release --filter "FullyQualifiedName~EmbeddingsPlugin"`

Expected: PASS for available plugin tests; unavailable model/native checks are recorded as unavailable, not passed.

- [ ] **Step 5: Commit plugin-reference corrections.**

```bash
git add docs/plugin-authoring.md docs/architecture/embeddings.md README.md docs/getting-started.md
git commit -m "docs: align plugin deployment references"
```

## Self-review

- Spec coverage: all named living docs, local product model, plugin executable/layout/model/Vulkan behavior, troubleshooting terminology, links/commands, and historical immutability are covered.
- Placeholder scan: no speculative feature, generic exclusion, or unresolved editorial placeholder appears.
- Type/signature consistency: documentation references existing commands and plugin surfaces only; no code API is introduced.
