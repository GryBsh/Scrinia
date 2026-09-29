# 0.6.0 Versioning, Changelog, and Living Documentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Set the release version exactly to `0.6.0`, add the current release narrative, and correct only living documentation to match the local CLI/stdio MCP and plugin implementation.

**Architecture:** `Directory.Build.props` remains the single version source. Add a new current `0.6.0` changelog entry without rewriting prior entries. Update only named living docs and architecture pages after checking commands, plugin discovery, model paths, and publish scripts.

**ref:** `docs/superpowers/specs/2026-08-27-0-6-0-versioning-changelog-docs-design.md` (approved version, changelog, and documentation scope); `Directory.Build.props` (canonical version property); `CHANGELOG.md` (release-history structure).

**Tech Stack:** MSBuild, .NET 10 CLI, Markdown, Keep a Changelog, existing source-generated version metadata.

## Global Constraints

- “The release version is exactly `0.6.0`.”
- “Directory.Build.props remains the sole version authority.”
- “Do not rewrite historical changelog entries, dated `docs/superpowers/` artifacts, or prior reports.”
- “Do not add HTTP, REST, remote CLI, web UI, `--remote`, `--api-key`, or `--store` guidance.”
- “Plugin documentation is checked against `McpPluginHost`, plugin CLI argument handling, discovery, model paths, and publish-script output.”

---

## Dependencies / Order

- Depends on IMP-04, IMP-01, IMP-02, and IMP-03 completing their implementation and focused tests; Task 1 precedes Task 2, and living-documentation work follows the source facts.
- Keep source assertions and executable probes as separate checks: pre-change source assertions run before the version edit, while all executable version/help/MCP probes are owned by the final integration plan.
- **ref:** `docs/superpowers/specs/2026-08-27-release-improvement-milestone-design.md` (approved dependency and release-gate order).

### Task 1: Set and test the canonical version

**Files:**
- Modify: `Directory.Build.props:8-10`
- Create: `tests/Scrinia.Tests/VersionMetadataTests.cs`
- Read: project files, `src/Scrinia/Program.cs`, MCP server info code

**Interfaces:**
- Consumes: MSBuild `$(Version)` source and project-file version declarations; executable CLI/MCP metadata is verified only by final integration.
- Produces: exact `0.6.0` source metadata with no contradictory project-level override.

**ref:** `Directory.Build.props`; `docs/superpowers/specs/2026-08-27-0-6-0-versioning-changelog-docs-design.md` (version authority and source-only verification boundary).

- [ ] **Step 1: Add source-only assertions** that read `Directory.Build.props`, assert the pre-change canonical property is exactly `<Version>0.5.0</Version>`, scan project files for conflicting `<Version>` elements, and assert `CHANGELOG.md` contains `## [Unreleased]`, without launching an executable or requiring a build output.
- [ ] **Step 2: Run the pre-change source assertions before changing the property.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~VersionMetadataTests"`

Expected: PASS because the canonical property is still `0.5.0` and `CHANGELOG.md` still contains `## [Unreleased]`; no executable probe is part of this check.

- [ ] **Step 3: Change only the canonical property to `0.6.0`; do not add project-level overrides.
- [ ] **Step 4: Re-run the source-only assertions** after the edit, updating their expected canonical value to exactly `0.6.0`; do not add executable probes to this plan. The final integration plan owns the fresh publish, `--version`, `--help`, and stdio MCP probes.

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~VersionMetadataTests"`

Expected: PASS for the source assertions: exactly one canonical `0.6.0` property, no conflicting project-level version, and the `Unreleased` heading remains historical/current changelog structure. Executable results are recorded only by final integration.

- [ ] **Step 5: Commit version metadata and test.**

```bash
git add Directory.Build.props tests/Scrinia.Tests/VersionMetadataTests.cs
git commit -m "chore: set solution version to 0.6.0"
```

### Task 2: Add the 0.6.0 changelog entry

**Files:**
- Modify: `CHANGELOG.md` immediately below `## [Unreleased]` or replace only the current release heading as established by the release convention

**Interfaces:**
- Consumes: completed IMP-04, IMP-01, IMP-02, IMP-03 results and known platform limitations.
- Produces: a factual `0.6.0` entry naming preserved APIs/storage behavior, entity materialization/adjacency, address conformance, publish parity/archive verification, and documentation corrections.

**ref:** `CHANGELOG.md`; `docs/superpowers/specs/2026-08-27-0-6-0-versioning-changelog-docs-design.md` (release narrative and limitation language).

- [ ] **Step 1: Draft the entry** with Keep a Changelog headings and explicit “no public API/storage/layout change” language; state host-restricted NativeAOT and unrun-platform limitations without claiming execution.
- [ ] **Step 2: Check the diff** to ensure no historical entry text changed.

Run: `git diff -- CHANGELOG.md`

Expected: only the new top release entry is added or the current release heading is updated; prior dated content is byte-for-byte unchanged.

- [ ] **Step 3: Commit the release narrative.**

```bash
git add CHANGELOG.md
git commit -m "docs: add 0.6.0 changelog entry"
```

## Self-review

- Spec coverage: exact version, probes, changelog scope, preserved historical entries, and release limitation language are covered. Living documentation is handled by the separate documentation plan.
- Placeholder scan: no speculative feature or unresolved editorial decision appears.
- Type/signature consistency: verification names existing CLI/MCP metadata surfaces only; no implementation API is introduced.
