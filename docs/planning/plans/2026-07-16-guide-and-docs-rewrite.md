# Unit 7 — Agent Guide & Docs Rewrite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring every documentation surface into alignment with the post-P6.6b keywords+attributes model and the bare tool names, and replace `guide.md` with a genuine authored guide that *teaches use of the tools* rather than reading like behavioral agent-commentary.

**Architecture:** Pure documentation effort — no product-logic changes. The only code touched is coupled test assertions that pin guide/prompt content. The guide is rewritten concept-first (teach the model → teach each tool → reference). Every doc uses the bare tool names that already ship in code.

**Tech Stack:** Markdown (embedded resources compiled into `Scrinia.Mcp`), C# / .NET 10, xUnit + FluentAssertions.

## Global Constraints

- **Bare tool names only** — `guide`, `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`. The `memory_*` prefix is gone from code; it must not appear in any rewritten doc. (`git grep memory_<verb>` may still legitimately match `CHANGELOG.md`, `docs/reviews/**`, `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `.superpowers/**` — all out of scope.)
- **The guide is a teaching guide, not commentary.** Concept-first, manual-style: teach the model, then each tool as *what it does · how to call it · a worked example · when to reach for it*, then reference. DROP the standalone "Store proactively" and "Every task, ask yourself" exhortation blocks. Behavioral guidance survives only as concise instruction folded into each tool's "when to reach for it".
- **Current tool surface (verify against the `[Description]` attributes in `src/Scrinia.Mcp/MemoryTools.cs`):**
  - `remember(path, content[], attributes?, keywords?)`
  - `recall(path, chunk?, withBuiltin?)`
  - `search(query, context?, scopes?, limit=20, excludeTopics?, pathPrefix?, attributeFilter?)`
  - `list(scopes?, mode="summary"|"full"|"drift", offset=0, limit=50, excludeTopics?)`
  - `append(path, content, attributes?, keywords?)`
  - `forget(path)`
  - `compact(path, keepRecent=0)`
  - `link(path, destination, reason?)`
  - `restore()`
  - `reconcile(conflictId?, choice="ours"|"theirs"|"merged", mergedContent?)`
- **Model vocabulary:** `attributes` is `{ key: [values] }`. Recommended keys: `person`/`place`/`org`/`thing`/`topic` (entities → entity graph); `file`/`ref` (dependencies, hashed for drift); `review` (ISO 8601 date and/or free-text condition). Any key allowed. `keywords` are distinctive terms + a searcher's likely phrasings. REMOVED as separate params/members: `description`, `tags`, `codeRefs`, `reviewAfter`, `reviewWhen`, `RetrievalAnchors`. The persisted drift baseline is `FileHashes` (JSON key `"codeRefs"`); the searchable attribute is `file`.
- **Reserved paths (full set):** `/skill/`, `/agent/`, `/patterns/`, `/findings/`, `/learn/`, `/sessions/`, `/checkpoint/`, `/temp/`.
- **No product-logic changes.** Only markdown/doc files and coupled test assertions.
- **Out of scope (do NOT touch):** `CHANGELOG.md` and `docs/reviews/**` (historical records — rewriting would falsify history); `workspace-scrinia/CLAUDE.md` (outside this repo's git tree); `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**` (own tool strings + live WIP); `.superpowers/**`; `src/Scrinia.Mcp/plugins/**`. No new CI/doc-parity apparatus.
- **Build/verify:** `TreatWarningsAsErrors` is on. Build per-project with `-p:NuGetAudit=false` (`Scrinia.sln` restore fails on a pre-existing AppHost NuGet-audit). Embedded markdown re-embeds on rebuild of `Scrinia.Mcp`.
- **Commit trailer, exactly (both lines):**
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```
- **Staging:** never `git add -A`; stage explicit paths and verify `git status`. Branch off `main` before Task 1. Never push.

---

### Task 1: Rewrite `guide.md` (concept-first teaching guide) + coupled tests

The substantive authoring task. Replace the stale, commentary-heavy guide with a concept-first guide that teaches the tools. The current guide's tool table documents removed params, its `search` params omit `pathPrefix`/`attributeFilter`, its review examples use the removed `reviewAfter:` surface, and its "typed keywords → structured fields" section predates the explicit `attributes` param.

**Files:**
- Rewrite: `src/Scrinia.Mcp/prompts/guide.md`
- Modify (coupled test): `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (`Guide_ContainsCoreSections`, ~L1906–1918)
- Verify (should stay green): `tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs` (`GuidePrompt_ReturnsTheAgentGuide`, ~L17)

**Interfaces:**
- Consumes: the tool surface + model vocabulary in Global Constraints (authoritative source: the `[Description]` attributes in `MemoryTools.cs`).
- Produces: the rewritten `guide.md` that every other task's cross-references stay consistent with.

**Required guide structure (concept-first):**
1. **What Scrinia is** — 2–3 sentences: persistent, project-scoped memory + reusable skills via MCP tools; lives in `.scrinia/`, travels with the code.
2. **Concepts (teach the model):** Memories & paths (hierarchical, auto-tagged, `pathPrefix`-scopable) · Chunks (`content` array, one chunk per element, designed around concepts) · Attributes (`{key:[values]}` typed facets that link + filter; the recommended-key vocabulary above) · Keywords (distinctive terms + searcher phrasings) · **Attributes vs. keywords** (facets that link/filter vs. free-text search signal — use both) · The entity graph (attributes link related memories across shared entity *values*; name consistently, type honestly, e.g. `person:paris` vs `place:paris`).
3. **Tools:** each of `remember, recall, search, list, append, forget, compact, link, restore, reconcile` (+ a short note on `guide`) as *what it does · how to call it (bare name, current params) · one worked example · when to reach for it*. Use the signatures in Global Constraints. Example for `remember`:
   ```
   remember({ path: '/findings/SEC-001',
     content: ['OAuth flow: redirect → callback → token exchange', 'Token refresh: every 55 min'],
     attributes: { org: ['acme'], topic: ['auth'], review: ['2026-08-01'] },
     keywords: ['oauth flow', 'token refresh'] })
   ```
   Example for `search`: `search({ query: 'oauth flow', pathPrefix: '/findings/', attributeFilter: { topic: ['auth'] } })`.
4. **Reference:** reserved-paths table (all 8) · reading responses (`instruction`/`followUp`/`actionNeeded`/`status`/`action`) · skills (load `recall({ path: '/skill/qa' })`, list `recall({ path: '/skill/' })`, override `remember({ path: '/skill/{name}', ... })`, the 8 built-ins, `withBuiltin` merge flag) · sessions (`append({ path: '/sessions/YYYY-MM-DD', ... })`) · recovery (`restore`) · workspace (`.scrinia/` in commits) · Scrinia vs. platform auto-memory.

**Removed from the current guide:** the standalone "## Store proactively" and "## Every task, ask yourself" commentary blocks, and the "Review markers" section's `reviewAfter:`/`reviewWhen:` examples (replaced by `attributes: { review: [...] }` shown in the attributes concept + `remember` example).

- [ ] **Step 1: Update the coupled test to the new content (write it first)**

In `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`, `Guide_ContainsCoreSections` currently asserts the stale surface. Replace its body's assertions with ones that pin the NEW stable sections (adjust exact heading strings to match the headings you author in Step 2):

```csharp
guide.Should().Contain("## Chunks");
guide.Should().Contain("## Attributes");
guide.Should().Contain("append");
guide.Should().Contain("review");                 // review is now an attribute key, not a param
guide.Should().Contain("/checkpoint/");
guide.Should().Contain("## Recovery");
guide.Should().Contain("remember");
guide.Should().NotContain("memory_remember");
guide.Should().NotContain("reviewAfter");         // the removed param must NOT reappear
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~Guide_ContainsCoreSections -p:NuGetAudit=false`
Expected: FAIL — the current guide still contains `reviewAfter` and lacks `## Attributes`.

- [ ] **Step 3: Rewrite `guide.md`**

Author the guide per the Required structure above, to the teaching-guide quality bar (concept-first, worked examples, no commentary blocks). Use ONLY the bare tool names and the current params. Ensure the exact heading strings match what the test pins (`## Chunks`, `## Attributes`, `## Recovery`) and that the guide contains the substrings `remember`, `append`, `review`, `/checkpoint/`, and never `memory_remember` or `reviewAfter`.

- [ ] **Step 4: Rebuild and run the coupled tests**

Run: `dotnet build src/Scrinia.Mcp -p:NuGetAudit=false` (re-embeds the guide), then `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~Guide_ContainsCoreSections|FullyQualifiedName~GuidePrompt_ReturnsTheAgentGuide" -p:NuGetAudit=false`
Expected: PASS both. (`GuidePrompt_ReturnsTheAgentGuide` asserts `remember`, `Reserved paths`, and NOT `memory_remember` — keep a "Reserved paths" heading so it stays green.)

- [ ] **Step 5: Full suite + grep + commit**

Run: `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false` → green. Confirm `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- src/Scrinia.Mcp/prompts/guide.md` returns nothing.
```bash
git add src/Scrinia.Mcp/prompts/guide.md tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git status
git commit -m "docs(mcp): rewrite guide.md as a concept-first teaching guide for the attributes model"
```

---

### Task 2: Refresh repo-root `AGENTS.md`

`AGENTS.md` is the contributor doc for agents working on the Scrinia codebase. It's stale on the tool surface and reserved paths.

**Files:**
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: the bare tool names + reserved-path set from Global Constraints; the guide's reserved-paths table from Task 1 (keep them consistent).

- [ ] **Step 1: Fix the tool-surface claims**

- L11: change "exposes two MCP tools (`guide`, `memory`)" → "exposes eleven MCP tools — `guide()` plus one per memory operation". Drop the parenthetical `memory` dispatcher.
- L31 (Project Layout): change `MemoryTools.cs <- guide() + memory() entry points (sealed partial)` → `MemoryTools.cs <- guide() + the per-verb memory tools (sealed partial)`.

- [ ] **Step 2: Rename the MCP Tools table to bare names + accurate descriptions**

In the table at ~L69–92, rename every `memory_<verb>` row to its bare name and update the `remember` row to mention the current surface. Example replacements:
```
| `remember` | Persist a memory at a path. `content` is a string array (one chunk per element); `attributes` are typed facets that link/filter; `keywords` are search terms. |
| `recall`   | Read a memory by path. Routes `/skill/...` paths to skill loading. |
| `search`   | Find memories by keyword or phrase; supports `pathPrefix` and `attributeFilter`. |
| `list`     | List stored memories (`summary` / `full` / `drift` modes). |
| `append`   | Append a chunk to an existing memory (or create it). |
| `forget`   | Delete a memory by path. |
| `compact`  | Merge a multi-chunk memory; archives the original. |
| `link`     | Bidirectional link between two memories. |
| `restore`  | Resume workspace context mid-session (usually folded into `guide()`). |
| `reconcile`| Scan `.scrinia/` for git merge conflicts, or resolve one. |
```
Also update the skill-routing bullet lines (~L87–92) `memory_recall`/`memory_remember` → `recall`/`remember`, and `memory_restore` → `restore` in the prose at ~L92.

- [ ] **Step 3: Sync reserved paths + refresh the Search/ layout line**

- In the Reserved Paths table (~L122–133), add rows for `/findings/...` (audit, review, and QA findings) and `/learn/...` (retrospectives and durable lessons) so it matches the guide's set.
- In Project Layout, refresh the `Search/` line (~L21) to name the attributes machinery, e.g. `Search/ <- BM25 + WeightedFieldScorer + KnownFields/FieldExtractor/AttributeReaders + TextAnalysis`, and add a `Migration/ <- AttributeMigration (forward-fill + key migration)` line under `Scrinia.Core/`.

- [ ] **Step 4: Verify + commit**

Run: `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- AGENTS.md` → nothing. (`AGENTS.md` is not compiled; no build needed.)
```bash
git add AGENTS.md
git status
git commit -m "docs: refresh AGENTS.md tool surface, reserved paths, and layout for the current model"
```

---

### Task 3: Rewrite `docs/architecture/core.md` for the attributes model

This is the deepest staleness: `core.md` documents the pre-P6.6b `ArtifactEntry` (with `ReviewAfter`/`ReviewWhen`/`CodeRefs`/`RetrievalAnchors`/`Fields`), a whole "Unified Fields" section, and dual-read prose. Update it to the current model.

**Files:**
- Modify: `docs/architecture/core.md`

**Current `ArtifactEntry` shape (source of truth — `src/Scrinia.Core/Models/ArtifactEntry.cs`):**
```
Name, Uri, OriginalBytes, ChunkCount, CreatedAt,
ContentPreview?, Keywords?, TermFrequencies?, UpdatedAt?, ChunkEntries?,
FileHashes?  (JSON "codeRefs" — dependency path→hash drift baseline; keys also surface as the `file` attribute),
Attributes?  (JSON "attributes" — Dictionary<string,string[]>, the unified typed facets)
```
The removed members (`Description`, `Tags`, `ReviewAfter`, `ReviewWhen`, `RetrievalAnchors`) are gone; their data lives in `Attributes` (`review`/`tag`/`anchor` keys) and `ContentPreview`. A back-compat `LegacyFields`/`NormalizeAttributesKey()` reads old `"fields"`-key sidecars into `Attributes`.

- [ ] **Step 1: Update the `ArtifactEntry` definition block**

Replace the entry-definition code block (~L100–117, the one showing `ReviewAfter`/`ReviewWhen`/`CodeRefs`/`RetrievalAnchors`/`Fields`) with the current shape above. Replace the `Fields is multi-valued …` paragraph with an `Attributes is multi-valued (key → string[])` paragraph pointing at the "Attributes" section.

- [ ] **Step 2: Rename "Unified Fields" → "Attributes" and update its body**

The section at ~L254+ ("Unified Fields", `ArtifactEntry.Fields`): rename to "Attributes" and rewrite so it describes `ArtifactEntry.Attributes` (`Dictionary<string,string[]>?`, JSON `"attributes"`). Keep the entity-key / reference-key distinction. Update the dual-read prose (scorer re-parses `key:value` `Keywords`; entity graph unions `ParseTypedEntities` with the attribute entity keys; orphan tracking checks both `Keywords` `ref:` and `Attributes["ref"]`). Replace `CodeRefs` references with `FileHashes` (JSON `"codeRefs"`), noting the MCP write path unions the explicit `file`/`codeRefs` input with `Attributes["file"]` before hashing into `FileHashes`.

- [ ] **Step 3: Sweep remaining `Fields`/`CodeRefs`/`ReviewAfter` references in the file**

Update the scoring table + `RouteKeywords`/`SeedTermFrequencies` prose (~L192–298) to say `Attributes` where it says `Fields`, and `FileHashes` where it says `CodeRefs`. Keep the *behavior* description accurate (the scorer, entity graph, and drift logic are unchanged — only the member names moved from `Fields`→`Attributes`, `CodeRefs`→`FileHashes`).

- [ ] **Step 4: Verify + commit**

Run: `git grep -niE '\bReviewAfter\b|\bReviewWhen\b|\bRetrievalAnchors\b|\bCodeRefs\b|Unified Fields|ArtifactEntry\.Fields' -- docs/architecture/core.md` → returns only intentional back-compat mentions (e.g. the `"codeRefs"` JSON key, the legacy `"fields"` key), no stale member documentation.
```bash
git add docs/architecture/core.md
git status
git commit -m "docs(arch): update core.md ArtifactEntry + Attributes model (post carrier-member removal)"
```

---

### Task 4: Sweep the remaining human docs (names + model terms)

Accuracy sweep across the human docs that still carry `memory_*` names or removed-model terms. Verify each change against current behavior — do not blind token-swap.

**Files (each has `memory_*` and/or model-term hits):**
- `README.md` — L7 rewrites the retrieval description that cites "optional `RetrievalAnchors`" as a separate input; reframe: agent-supplied `keywords` + the `anchor` attribute feed retrieval (anchors are no longer a distinct top-level member). Swap any `memory_<verb>` → bare.
- `docs/cli-reference.md` — L83 "Retrieval anchors. The `RetrievalAnchors` field …" → describe the `anchor` attribute; L183/L185 `reviewAfter` staleness prose → the `review` attribute's date. Swap `memory_<verb>` → bare.
- `docs/getting-started.md` — swap `memory_<verb>` → bare (2 hits); verify the skills table / examples still match current behavior.
- `docs/multi-user-setup.md` — swap `memory_<verb>` → bare (6 hits).
- `docs/web-ui-guide.md`, `docs/architecture/embeddings.md` — swap the single `memory_<verb>` hit each → bare.
- `docs/security.md` — the `{LockPath}`/`{ElapsedMs}` "fields" at L82 are log fields, NOT the model — **leave unchanged**; verify no other stale hit.

**Interfaces:** consumes the bare names + model vocabulary from Global Constraints.

- [ ] **Step 1: Rename tool names to bare across the listed files**

Go file by file. Replace each `memory_<verb>` token (the 10 verbs) with its bare form in prose and examples. Do not touch `MemoryTools`/`MemoryNaming` identifiers or reserved paths.

- [ ] **Step 2: Fix the model-term references**

Apply the per-file model updates listed above (README L7 retrieval framing; cli-reference L83/L183/L185; leave security.md L82). Where a doc describes `RetrievalAnchors`/`reviewAfter`/`Fields` as a member, reframe to the `anchor`/`review`/attributes model, keeping the described behavior accurate.

- [ ] **Step 3: Verify + commit**

Run: `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- README.md 'docs/**' ':!docs/reviews' ':!docs/superpowers'` → nothing. Spot-check that no reframed sentence describes behavior that no longer exists.
```bash
git add README.md docs/cli-reference.md docs/getting-started.md docs/multi-user-setup.md docs/web-ui-guide.md docs/architecture/embeddings.md
git status
git commit -m "docs: bare tool names + attributes-model terms across human docs"
```

---

### Task 5: Audit the two stale skills + clean stale test refs

Two embedded skills reference the old surface/model; fix only what's genuinely stale. Also clean incidental `memory_*` refs in tests.

**Files:**
- Modify: `src/Scrinia.Mcp/skills/evolutionary.md`, `src/Scrinia.Mcp/skills/qa.md` (only stale model/surface refs)
- Modify (stale refs, if the assertion is not testing legacy/compat behavior): `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`, `tests/Scrinia.Tests/HintCommandTests.cs`, and any other `tests/Scrinia.Tests/**` file whose `memory_*` reference is incidental.

- [ ] **Step 1: Audit the two skills**

Read `evolutionary.md` and `qa.md`. For each `reviewAfter`/`reviewWhen`/`retrievalanchors`/`codeRefs`/`fields`/`memory_<verb>` hit, decide: is it describing current behavior wrongly (fix → attributes/bare) or is the prose still correct (leave)? Apply only the genuine fixes. Do not reword correct prose.

- [ ] **Step 2: Clean incidental stale test refs**

Run: `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- tests/Scrinia.Tests`. For each hit, if it's an incidental stale mention (comment, example string not asserting legacy behavior), rename to bare. If a test deliberately asserts legacy/compat handling of an old name, LEAVE it and note why in the commit body.

- [ ] **Step 3: Rebuild + full suite**

Run: `dotnet build src/Scrinia.Mcp -p:NuGetAudit=false` (re-embeds skills), then `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false` → green, 0 warnings.

- [ ] **Step 4: Verify + commit**

Run: `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- src/Scrinia.Mcp/skills tests/Scrinia.Tests` → returns only deliberate legacy/compat assertions (documented in the commit body), nothing else.
```bash
git add src/Scrinia.Mcp/skills tests/Scrinia.Tests
git status
git commit -m "docs(skills): fix stale model/surface refs in evolutionary+qa skills; clean stale test refs"
```

---

## Verification (whole unit)

1. `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)'` returns matches ONLY under `CHANGELOG.md`, `docs/reviews/**`, `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `.superpowers/**`, `src/Scrinia.Mcp/plugins/**`, and any deliberately-documented legacy/compat test assertion.
2. `dotnet build src/Scrinia.Mcp -p:NuGetAudit=false` and `dotnet build src/Scrinia.Server -p:NuGetAudit=false` clean (0 warnings).
3. `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false` green.
4. Manual accuracy pass: the rewritten guide's tool list and each tool's params match the `[Description]` metadata in `MemoryTools.cs`; `AGENTS.md` and `docs/architecture/core.md` describe the current `ArtifactEntry`/attributes model.
5. Read the rewritten `guide.md` end-to-end: it teaches the tools (concept → tool → example → when), with no "Store proactively"/"Every task, ask yourself" commentary blocks.

## Self-Review (done during authoring)

- **Spec coverage:** §1 guide → Task 1; §2 AGENTS.md → Task 2; §3 human docs → Tasks 3 (core.md, called out as substantial) + 4 (the rest); §4 skills+tests → Task 5; §5 verification → per-task + whole-unit verification. CHANGELOG/CLAUDE.md/reviews exclusions carried into Global Constraints.
- **Placeholder scan:** each task names exact files, exact current line regions, exact test edits, and exact grep/commit commands; no "TBD"/"handle edge cases".
- **Consistency:** the tool signatures, attribute keys, and reserved-path set are stated once in Global Constraints and referenced by every task; the guide test's pinned headings (`## Chunks`/`## Attributes`/`## Recovery`) match the guide structure in Task 1.
