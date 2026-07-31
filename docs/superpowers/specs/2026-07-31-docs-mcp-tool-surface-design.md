# Stream 7 — Docs/minor: MCP tool-surface drift + truncation hint

**Date:** 2026-07-31
**Stream:** 7 of the 2026-07-29 whole-codebase review remediation (docs/minor)
**Verdict source:** independent-agent REQUEST_CHANGES review, 2026-07-29

## Problem

Two confirmed findings, both verified against live code.

### 1. MCP tool-surface documentation drift

The docs describe the MCP surface as **"two tools (`guide`, `memory`) using
noun('action') dispatch"** — a design that no longer exists. The actual surface
is **twelve per-verb tools**, each registered as its own `[McpServerTool]` with
its own JSON schema:

```
guide  remember  recall  search  list  append
forget  compact  link  restore  reconcile  facts
```

Evidence:

- `src/Scrinia.Mcp/MemoryTools.cs` — `guide`, `remember`, `recall`, `search`,
  `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`.
- `src/Scrinia.Mcp/MemoryTools.Facts.cs` — `facts`.
- `MemoryTools.cs:65-72` documents the deliberate refactor *away* from a single
  dispatcher: "Each tool below has its own JSONSchema — the validator sees
  exactly the fields the operation accepts."
- Both hosts register the whole type: `WithTools<ScriniaMcpTools>(...)` at
  `src/Scrinia/Commands/ScriniaCommands.Serve.cs:96` (CLI) and
  `src/Scrinia.Server/Program.cs:303` (server). No `memory` tool is registered.

There is **no `memory` dispatcher tool**. `remember`/`recall` are the MCP names;
`store`/`show` are CLI-only command aliases (`scri store`, `scri show`) — the
current getting-started table conflates the two.

The stale claim appears in **six active doc files** (the finding named only the
first two):

| File | Lines |
|---|---|
| `docs/getting-started.md` | 105, 164, 169 |
| `docs/architecture/overview.md` | 14, 47, 80 |
| `docs/cli-reference.md` | 3, 718, 722 |
| `docs/server-admin.md` | 420 |
| `docs/architecture/cli.md` | 84, 86, 252 |
| `docs/architecture/server.md` | 331 |

### 2. `McpResponse` truncation followUp hint

`src/Scrinia.Mcp/McpResponse.cs:187-190` emits, when content is truncated:

> "Content truncated. Call recall({ path, chunk: N+1 }) for the next chunk, or
> search to scope down to the segment you need."

This is misleading:

- Truncation is **character-based** (`ResponseBuilder.Truncate`, an 8 KB cap
  cutting mid-line at `MaxResponseChars - YamlOverhead`), **not chunk-aligned**.
  `recall({ chunk: N+1 })` does not resume where the cut happened.
- `ResponseBuilder.Success`/`Warning` truncate content for **any** tool —
  `search`, `list`, `guide`, `facts` — none of which take a `path`+`chunk`
  resumption. The hint points those callers at a nonsensical next step.

## Scope

**In scope:**

- Correct the MCP tool-surface description across all **six** active doc files.
- Reword the `McpResponse` truncation hint to be accurate and tool-agnostic.

**Out of scope (explicitly):**

- `src/Scrinia.Mcp/prompts/guide.md` — a reserved user working-tree file; never
  touched by this stream even if it carries the same drift.
- `workspace-scrinia/CLAUDE.md` — outside this repo's docs tree; its
  `memory('search', …)` form is an intentional conceptual simplification.
- Historical `docs/planning/**` and `docs/superpowers/**` specs/plans —
  point-in-time records, left as written.
- Making the truncation hint fully context-aware (threading the source
  tool/path into the response). Larger change; not warranted for this stream.

## Design

### Doc corrections

Replace "two tools / unified `memory` dispatcher / noun('action') dispatch"
language with the real surface: the twelve per-verb tools, **named and grouped
by purpose** rather than counted, to resist future re-drift:

- **Lifecycle:** `guide`, `restore`, `reconcile`
- **Memory operations:** `remember`, `recall`, `search`, `list`, `append`,
  `forget`, `compact`, `link`
- **Facts:** `facts`

**Anti-drift rule:** name the tools by group everywhere; keep a bare numeral
only in the two places where the format demands one:

- `docs/architecture/overview.md:14` — the ASCII box `MCP Tools (2)` → `(12)`.
- `docs/architecture/overview.md:47` — the solution-structure comment
  `(2 tools: guide, memory)` → `(12 per-verb tools)`.

Everywhere else, no magic number to go stale.

Per-file intent:

- **`getting-started.md:105`** — reword the "two MCP tools (`guide`, `memory`)"
  sentence to reference the per-verb surface.
- **`getting-started.md:162-169`** — the "MCP Tools Overview" section: replace
  the two-row (`guide`, `memory`) table with the grouped tool list; fix the
  `remember`/`store` and `recall`/`show` conflation (MCP names are
  `remember`/`recall`).
- **`overview.md:14,47,80`** — ASCII diagram box, solution-structure comment,
  and the Mcp dependency bullet (drop "noun('action') dispatch"; describe
  per-verb tools with per-tool schemas).
- **`cli-reference.md:3`** — the intro "exposing two tools (`guide` and
  `memory`)" sentence.
- **`cli-reference.md:716-722`** — "Reserved Path Conventions": the `/skill/...`
  routing is real, but retarget "the `memory` tool" / "`memory()`" phrasing to
  the actual `remember`/`recall` routing.
- **`server-admin.md:420`** — "Both MCP tools (`guide` and `memory`)".
- **`cli.md:84,86,252`** — the "two MCP tools" registration prose and the
  `memory()` dispatcher paragraph.
- **`server.md:331`** — "Both MCP tools (`guide` and `memory`)".

Skill-path routing (`/skill/...` → `SkillLoad`/`SkillCreate` via
`recall`/`remember`) is genuine and should be described accurately, not deleted.

### `McpResponse` truncation hint

Reword `McpResponse.cs:187-190`. New text (final wording refined during
implementation):

> "Content was truncated to fit the response limit. Narrow with search (e.g. a
> pathPrefix or attributeFilter), or recall a specific chunk, to retrieve the
> omitted portion."

Drops the false "chunk: N+1 continues from the cut" promise; gives advice valid
regardless of which tool produced the content.

## Testing

- Docs: no automated tests. Verification is `dotnet build Scrinia.sln` staying
  green (docs don't affect it) plus a manual read for internal consistency and
  no remaining `two tools` / `noun('action')` occurrences in the six files.
- `McpResponse`: search the test tree for any assertion on the truncation
  string (`chunk: N+1`, "Content truncated") and update it to the new wording.
  Run the relevant test project. Build WITHOUT `-p:NuGetAudit=false`.

## Constraints (standing, carried across all streams)

- `web/`-style discipline: change only what the finding requires.
- Never stage/commit/touch the four reserved working-tree files:
  `.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`,
  `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`,
  `src/Scrinia.Mcp/prompts/guide.md`.
- Build/test without `-p:NuGetAudit=false`.
- Merge `--no-ff`, keeping the branch. Never revert without explicit permission.
