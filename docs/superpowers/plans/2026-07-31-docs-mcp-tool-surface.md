# Docs MCP Tool-Surface Correction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct the stale "two tools (`guide`, `memory`) dispatcher" description across six docs to the real twelve per-verb MCP tools, reword the `McpResponse` truncation hint, add the missing `facts` tool to the agent guide, and fix `CLAUDE.md`'s dispatcher-form lines.

**Architecture:** Mostly Markdown edits with grep-for-absence verification. Two tasks touch C#/tests (the truncation reword and the guide `facts` addition) and follow a real red→green cycle against existing xUnit tests. One task edits a file in the *parent* `workspace-scrinia` repo and is deliberately excluded from this branch.

**Tech Stack:** Markdown docs; C# (.NET 10) `Scrinia.Mcp`; xUnit + FluentAssertions in `tests/Scrinia.Tests`.

## Global Constraints

- **Reserved files — never stage, commit, or edit:** `.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`. (`guide.md` was the fourth reserved file; the user lifted the restriction for this stream only — Task 6 edits it.)
- **Build/test WITHOUT** `-p:NuGetAudit=false`. Use plain `dotnet build` / `dotnet test`.
- **Twelve MCP tools (verbatim, the source of truth):** `guide`, `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`, `facts`.
- **Anti-drift rule:** name tools by group in prose; keep a bare numeral only in the two `overview.md` spots (Task 3).
- **MCP names are `remember`/`recall`.** `store`/`show` are CLI-only command aliases — never present them as MCP tool names.
- **Merge `--no-ff`, KEEP the branch.** Never revert without explicit permission.
- **Branch:** create `docs-mcp-surface` off `main` before Task 1. The working tree already carries the three reserved files plus a pending `guide.md` edit — these float across the checkout untouched.

---

## File Structure

- `src/Scrinia.Mcp/McpResponse.cs` — reword the truncation followUp (Task 1).
- `tests/Scrinia.Tests/ResponseParserTests.cs` — strengthen the truncation test (Task 1).
- `docs/getting-started.md` — user-facing MCP tools overview (Task 2).
- `docs/architecture/overview.md` — diagram box, structure comment, Mcp bullet (Task 3).
- `docs/cli-reference.md` — intro line + reserved-path section (Task 4).
- `docs/server-admin.md`, `docs/architecture/cli.md`, `docs/architecture/server.md` — small prose fixes (Task 5).
- `src/Scrinia.Mcp/prompts/guide.md` — add the `facts` section (Task 6).
- `tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs` — assert guide documents `facts` (Task 6).
- `../CLAUDE.md` (parent `workspace-scrinia` repo) — two dispatcher-form lines (Task 7, off-branch).

---

### Task 1: Reword the `McpResponse` truncation followUp hint

**Files:**
- Modify: `src/Scrinia.Mcp/McpResponse.cs:187-190`
- Test: `tests/Scrinia.Tests/ResponseParserTests.cs:50-66`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: nothing later tasks depend on (behavior-preserving string change).

Context: `ResponseBuilder.Truncate` cuts content at a character cap (~8 KB), not on chunk boundaries, and `Success`/`Warning` truncation fires for any tool's content. The current hint falsely promises `recall({ path, chunk: N+1 })` continues from the cut. The existing test only checks the hint contains "truncated", so it would pass either way — we strengthen it to pin the fix.

- [ ] **Step 1: Strengthen the failing test**

In `tests/Scrinia.Tests/ResponseParserTests.cs`, replace the body of `Truncated_Content_AppendsFollowUpHint` (currently lines 50-66) with:

```csharp
    [Fact]
    public void Truncated_Content_AppendsFollowUpHint()
    {
        // Build content larger than the response cap (~8 KB) and confirm the YAML output
        // surfaces a tool-agnostic recovery hint — NOT a false "next chunk" continuation
        // (truncation is character-based, not chunk-aligned, and fires for any tool).
        string large = new('x', 16 * 1024);
        var yaml = ResponseBuilder.Success(large).ToYaml();

        var parsed = ResponseParser.Parse(yaml);

        parsed.Status.Should().Be("success");
        parsed.Content.Should().NotBeNull();
        parsed.Content!.Should().EndWith("... (truncated)",
            because: "the in-content truncation marker must still be present");

        var hint = parsed.FollowUp.Should().ContainSingle(h => h.Contains("truncated", StringComparison.OrdinalIgnoreCase)).Subject;
        hint.Should().Contain("search",
            because: "the hint must offer a tool-agnostic way to narrow results");
        hint.Should().NotContain("N+1",
            because: "truncation is character-based, so promising chunk N+1 continues the cut is false");
        hint.Should().NotContain("next chunk",
            because: "the hint must not promise a chunk-based continuation that does not exist");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ResponseParserTests.Truncated_Content_AppendsFollowUpHint"`
Expected: FAIL — the current hint contains "N+1" and "next chunk".

- [ ] **Step 3: Reword the hint**

In `src/Scrinia.Mcp/McpResponse.cs`, replace lines 187-190:

```csharp
            if (r.ContentTruncated)
                sb.AppendLine("  - " + YamlEscape(
                    "Content truncated. Call recall({ path, chunk: N+1 }) for the next chunk, " +
                    "or search to scope down to the segment you need."));
```

with:

```csharp
            if (r.ContentTruncated)
                sb.AppendLine("  - " + YamlEscape(
                    "Content was truncated to fit the response limit. Narrow with search " +
                    "(e.g. a pathPrefix or attributeFilter), or recall a specific chunk, " +
                    "to retrieve the omitted portion."));
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ResponseParserTests"`
Expected: PASS (both `Truncated_Content_AppendsFollowUpHint` and `Untruncated_Content_DoesNotAppendFollowUp`).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/McpResponse.cs tests/Scrinia.Tests/ResponseParserTests.cs
git commit -m "fix(mcp): truncation hint is tool-agnostic, drops false chunk N+1 promise"
```

---

### Task 2: `getting-started.md` — real MCP tool surface

**Files:**
- Modify: `docs/getting-started.md:105` and `docs/getting-started.md:162-169`

**Interfaces:**
- Consumes: the twelve-tool list from Global Constraints.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Fix the inline sentence (line 105)**

Replace:

```markdown
Now your AI assistant has access to two MCP tools (`guide`, `memory`) for persistent memory, skills, and session recall. See [CLI Reference](cli-reference.md) for full details.
```

with:

```markdown
Now your AI assistant has access to Scrinia's MCP tools — `guide` plus per-verb memory tools (`remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`) and the `facts` entity-query tool — for persistent memory, skills, and session recall. See [CLI Reference](cli-reference.md) for full details.
```

- [ ] **Step 2: Replace the "MCP Tools Overview" table (lines 162-169)**

Replace:

```markdown
## MCP Tools Overview

When connected via MCP, Scrinia exposes two tools:

| Tool | Actions | Purpose |
|------|---------|---------|
| `guide` | *(none — standalone)* | Session playbook (call once per session) |
| `memory` | `remember`/`store`, `recall`/`show`, `forget`, `search`, `list`, `append`, `compact`, `link`, `restore`, `reconcile` | Unified memory dispatcher; skill paths (`/skill/...`) are routed through it |
```

with:

```markdown
## MCP Tools Overview

When connected via MCP, Scrinia exposes one tool per operation, each with its own schema. Grouped by purpose:

| Group | Tools | Purpose |
|------|---------|---------|
| Lifecycle | `guide`, `restore`, `reconcile` | Session playbook + context restore, and merge-conflict resolution |
| Memory | `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link` | Store, read, find, and maintain memories |
| Facts | `facts` | Aggregate/relational entity queries (`entities`, `profile`, `intersect`) |

`guide` is standalone — call it once per session. Skill paths (`/skill/...`) are routed through `remember` (create/override) and `recall` (load/list), not a separate tool.
```

- [ ] **Step 3: Verify no stale phrasing remains**

Run: `grep -nE "two MCP tools|two tools|noun\('action'\)|unified.*dispatcher" docs/getting-started.md`
Expected: no output.
Run: `grep -n '\x60memory\x60' docs/getting-started.md`
Expected: no output (no remaining `memory` as a tool name).

- [ ] **Step 4: Read the section to confirm it reads cleanly**

Read `docs/getting-started.md` lines 100-170; confirm the table renders and the tool list matches the twelve tools.

- [ ] **Step 5: Commit**

```bash
git add docs/getting-started.md
git commit -m "docs: getting-started reflects real per-verb MCP tool surface"
```

---

### Task 3: `architecture/overview.md` — diagram, structure comment, Mcp bullet

**Files:**
- Modify: `docs/architecture/overview.md:14-15`, `:47`, `:80`

**Interfaces:**
- Consumes: twelve-tool list. This is the ONLY task that keeps a bare numeral (per the anti-drift rule).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Update the ASCII diagram box (lines 14-15)**

The right border `|` must stay column-aligned. Replace exactly:

```
                  |  ScriniaCommands | MCP Tools (2)             |
                  |                  | guide, memory             |
```

with (note: `(12)` is one char longer than `(2)`, so one trailing space is removed on line 14; `guide + verbs` is the same width as `guide, memory`):

```
                  |  ScriniaCommands | MCP Tools (12)            |
                  |                  | guide + verbs             |
```

- [ ] **Step 2: Update the solution-structure comment (line 47)**

Replace:

```
    Scrinia.Mcp/                     MCP tool definitions (2 tools: guide, memory)
```

with:

```
    Scrinia.Mcp/                     MCP tool definitions (12 per-verb tools)
```

- [ ] **Step 3: Update the Mcp dependency bullet (line 80)**

Replace:

```markdown
- **Mcp** depends only on Core and `ModelContextProtocol`. Contains two MCP tools (`guide`, `memory`) using noun('action') dispatch, plus `ScriniaMcpJsonContext` for trimming safety
```

with:

```markdown
- **Mcp** depends only on Core and `ModelContextProtocol`. Defines the MCP tools — `guide` plus per-verb memory tools (`remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`) and `facts` — each with its own JSON schema, plus `ScriniaMcpJsonContext` for trimming safety
```

- [ ] **Step 4: Verify**

Run: `grep -nE "MCP Tools \(2\)|2 tools|guide, memory|noun\('action'\)|two MCP tools" docs/architecture/overview.md`
Expected: no output.
Run: `grep -nE "MCP Tools \(12\)|12 per-verb tools" docs/architecture/overview.md`
Expected: two matches (lines 14 and 47).

- [ ] **Step 5: Confirm the ASCII box is still aligned**

Read `docs/architecture/overview.md` lines 7-18; confirm the right-edge `|` on the two edited lines aligns with the surrounding box borders.

- [ ] **Step 6: Commit**

```bash
git add docs/architecture/overview.md
git commit -m "docs: overview diagram + structure reflect 12 per-verb MCP tools"
```

---

### Task 4: `cli-reference.md` — intro line + reserved-path routing

**Files:**
- Modify: `docs/cli-reference.md:3`, `:718`, `:722`

**Interfaces:**
- Consumes: twelve-tool list.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Fix the intro sentence (line 3)**

Replace:

```markdown
`scri` is the command-line interface and MCP server for Scrinia. It manages persistent memories and serves as an MCP server exposing two tools (`guide` and `memory`) for AI coding tools.
```

with:

```markdown
`scri` is the command-line interface and MCP server for Scrinia. It manages persistent memories and serves as an MCP server exposing one tool per operation — `guide`, the per-verb memory tools (`remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`), and `facts` — for AI coding tools.
```

- [ ] **Step 2: Fix the reserved-path intro (line 718)**

Replace:

```markdown
Memory paths are free-form, but a small set of namespaces have first-class behavior in the `memory` tool:
```

with:

```markdown
Memory paths are free-form, but a small set of namespaces have first-class behavior:
```

- [ ] **Step 3: Fix the `/skill/...` routing row (line 722)**

Replace:

```markdown
| `/skill/...` | Reusable specialist prompts (built-in or override). Routed through `memory()` to load/store on disk under `.scrinia/skills/`. |
```

with:

```markdown
| `/skill/...` | Reusable specialist prompts (built-in or override). Routed through `recall` (load/list) and `remember` (create/override) to disk under `.scrinia/skills/`. |
```

- [ ] **Step 4: Verify**

Run: `grep -nE "two tools|exposing two|the \x60memory\x60 tool|\x60memory\(\)\x60" docs/cli-reference.md`
Expected: no output.

- [ ] **Step 5: Commit**

```bash
git add docs/cli-reference.md
git commit -m "docs: cli-reference intro + skill routing use real per-verb tools"
```

---

### Task 5: `server-admin.md`, `cli.md`, `server.md` — remaining prose fixes

**Files:**
- Modify: `docs/server-admin.md:420`
- Modify: `docs/architecture/cli.md:84`, `:86`, `:252`
- Modify: `docs/architecture/server.md:331`

**Interfaces:**
- Consumes: twelve-tool list.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: `server-admin.md` line 420**

Replace:

```markdown
Both MCP tools (`guide` and `memory`) are available through this endpoint, authenticated with the same API key scheme.
```

with:

```markdown
All of Scrinia's MCP tools (`guide` plus the per-verb memory tools and `facts`) are available through this endpoint, authenticated with the same API key scheme.
```

- [ ] **Step 2: `architecture/cli.md` line 84**

Replace:

```markdown
The two MCP tools (`guide`, `memory`) are exposed by `ScriniaMcpTools`, a sealed partial class registered via `WithTools<T>()`. They access the store via `MemoryStoreContext.Current`, which is set during workspace initialization.
```

with:

```markdown
The MCP tools are exposed by `ScriniaMcpTools`, a sealed partial class registered via `WithTools<T>()`. They access the store via `MemoryStoreContext.Current`, which is set during workspace initialization.
```

- [ ] **Step 3: `architecture/cli.md` line 86**

Replace:

```markdown
`memory()` is the unified dispatcher for `remember`/`store`, `recall`/`show`, `forget`, `search`, `list`, `append`, `compact`, `link`, `restore`, and `reconcile`. Skill-path routing (`/skill/...`) handles skill creation/load through the same entry point.
```

with:

```markdown
Each operation is its own tool with its own schema: `guide`, `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`, and `facts`. Skill-path routing (`/skill/...`) handles skill create/load inside `remember`/`recall`.
```

- [ ] **Step 4: `architecture/cli.md` line 252 (Test Coverage bullet)**

Replace:

```markdown
- The two MCP tools (`guide`, `memory`)
```

with:

```markdown
- The MCP tools (`guide`, the per-verb memory tools, and `facts`)
```

- [ ] **Step 5: `architecture/server.md` line 331**

Replace:

```markdown
Both MCP tools (`guide` and `memory`) are available, defined in `MemoryTools.cs`. The MCP path uses `IMemoryEventSink` for event hooks (not `IMemoryOperationHook`) to avoid double-firing.
```

with:

```markdown
All of Scrinia's MCP tools are available, defined in `MemoryTools.cs` (and `MemoryTools.Facts.cs`). The MCP path uses `IMemoryEventSink` for event hooks (not `IMemoryOperationHook`) to avoid double-firing.
```

- [ ] **Step 6: Verify all three files**

Run: `grep -nE "two MCP tools|Both MCP tools|\x60memory\x60\) are|unified dispatcher|\x60memory\(\)\x60" docs/server-admin.md docs/architecture/cli.md docs/architecture/server.md`
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add docs/server-admin.md docs/architecture/cli.md docs/architecture/server.md
git commit -m "docs: server-admin + architecture prose reflect per-verb MCP tools"
```

---

### Task 6: `guide.md` — document the `facts` tool

**Files:**
- Modify: `src/Scrinia.Mcp/prompts/guide.md` (insert a `### facts` section after the `reconcile` section, before `### guide`)
- Test: `tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs:16-25`

**Interfaces:**
- Consumes: the `facts` op vocabulary from `src/Scrinia.Mcp/MemoryTools.Facts.cs` (`entities`, `profile`, `intersect`).
- Produces: nothing later tasks depend on.

**IMPORTANT:** `guide.md` already carries an unrelated pending working-tree edit by the user (deletion of the "### Scrinia vs. platform auto-memory" subsection). This is intended. Do NOT revert or restore it — `git add` the whole file so the user's deletion and the `facts` addition commit together.

- [ ] **Step 1: Add the failing assertion**

In `tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs`, in `GuidePrompt_ReturnsTheAgentGuide` (lines 16-25), add one assertion after the existing `prompt.Should().Contain("Reserved paths");` line:

```csharp
        prompt.Should().Contain("facts",
            because: "the facts tool must be documented in the agent guide");
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ScriniaMcpPromptsResourcesTests.GuidePrompt_ReturnsTheAgentGuide"`
Expected: FAIL — the guide has no `facts` section yet.

- [ ] **Step 3: Insert the `facts` section into `guide.md`**

Find this block (currently lines 108-112):

```markdown
### reconcile — resolve merge conflicts

`reconcile({ conflictId?, choice?, mergedContent? })` — no arguments scans `.scrinia/` for git merge conflicts; a `conflictId` plus a `choice` (`ours` / `theirs` / `merged`) resolves one.

### guide — this guide
```

Replace it with (inserts `### facts` between `reconcile` and `guide`):

````markdown
### reconcile — resolve merge conflicts

`reconcile({ conflictId?, choice?, mergedContent? })` — no arguments scans `.scrinia/` for git merge conflicts; a `conflictId` plus a `choice` (`ours` / `theirs` / `merged`) resolves one.

### facts — query the entity graph

`facts({ op, entity?, entities?, key?, scopes?, excludeTopics?, limit? })` — aggregate and relational questions `search` can't express. Three ops: `entities` lists the known entity vocabulary, `profile` returns one entity's counts, date span, and co-occurrences, and `intersect` returns memories mentioning *all* of the given entities.

```
facts({ op: 'intersect', entities: ['person:jolene', 'place:france'] })
```

### guide — this guide
````

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ScriniaMcpPromptsResourcesTests"`
Expected: PASS (the guide is an embedded resource; `dotnet test` rebuilds and picks up the edit).

- [ ] **Step 5: Commit (whole `guide.md`, including the pending WIP deletion)**

```bash
git add src/Scrinia.Mcp/prompts/guide.md tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs
git commit -m "docs(guide): document the facts tool in the agent guide"
```

- [ ] **Step 6: Full build + test gate**

Run: `dotnet build Scrinia.sln` (WITHOUT `-p:NuGetAudit=false`)
Expected: build succeeds.
Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: all pass (covers Task 1 + Task 6 changes).

---

### Task 7: `CLAUDE.md` — dispatcher-form fix (parent repo, OFF-BRANCH)

**Files:**
- Modify: `../CLAUDE.md` — i.e. `C:\SynologyDrive\Source\repos\workspace-scrinia\CLAUDE.md` (tracked in the **parent `workspace-scrinia` repo**, not this one).

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

**IMPORTANT:** This file is outside the Scrinia repo. Apply the edit directly and leave it as a working-tree change in the parent repo. Do NOT `git add`/`commit` it from the Scrinia repo — it is not part of this branch or the merge. The user commits it in the parent repo at their discretion.

- [ ] **Step 1: Fix the two dispatcher-form quick-reminder lines**

Replace:

```markdown
- `memory('search', ...)` before non-trivial work; skip for self-contained requests.
- `memory('remember', { path, content: [...] })` immediately when you learn something non-obvious.
```

with:

```markdown
- `search({ query: '...' })` before non-trivial work; skip for self-contained requests.
- `remember({ path, content: [...] })` immediately when you learn something non-obvious.
```

- [ ] **Step 2: Verify**

Run: `grep -nE "memory\('search'|memory\('remember'" ../CLAUDE.md`
Expected: no output.

- [ ] **Step 3: Report (do not commit)**

Confirm to the user the parent-repo `CLAUDE.md` is edited and left uncommitted for them to commit in the parent repo.

---

## Self-Review

**Spec coverage:**
- Finding 1 (six-file drift) → Tasks 2, 3, 4, 5. ✓
- Finding 2 (truncation hint) → Task 1. ✓
- Finding 3 (`guide.md` missing `facts` + WIP handling) → Task 6. ✓
- Finding 4 (`CLAUDE.md`) → Task 7. ✓
- Anti-drift rule (numeral only in overview.md) → Task 3 keeps `(12)`; Tasks 2/4/5 use grouped names. ✓
- MCP-name accuracy (no `store`/`show` as tool names) → Task 2 table uses `remember`/`recall` only. ✓

**Placeholder scan:** No TBD/TODO; every edit has complete before/after text. ✓

**Type/string consistency:** The twelve-tool list is identical everywhere it appears. The truncation test asserts against the exact new wording in Task 1 Step 3. The `facts` assertion string ("facts") appears in the Task 6 Step 3 section. ✓

## Post-Implementation (orchestrator, after all tasks)

- `git status --short` must show ONLY the three reserved files still modified (`.gitignore`, `plugins/opencode/package.json`, `plugins/opencode/scrinia.ts`) — nothing else uncommitted in the Scrinia repo. `guide.md` is now committed on the branch.
- Merge `--no-ff` into `main`, KEEP the branch `docs-mcp-surface`.
- The parent-repo `CLAUDE.md` edit (Task 7) is reported as a separate uncommitted change, not part of the merge.
