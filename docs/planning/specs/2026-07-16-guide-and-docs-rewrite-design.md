# Unit 7 — Agent Guide & Docs Rewrite: Design

**Goal:** Bring every documentation surface into alignment with the post-P6.6b reality — the
keywords + attributes model and the bare tool names — and, above all, replace the current
`guide.md` with a **genuine, authored guide that teaches use of the tools** rather than reading like
behavioral agent-commentary.

**Type:** Pure documentation effort. No product-logic changes. (Some coupled test-assertion updates.)

## Context / current state (verified)

- The `memory_* → bare` tool rename (formerly "Plan 4") **already landed on `main`** — commits
  `fc60c78`, `d8a2b7d`, `30a9375`, `ef19ed7`. `src/**` has zero `memory_<verb>` tokens; the
  `McpPermissionFilter` map keys are bare; the completeness test exists. So the tools are
  `remember`/`recall`/`search`/… today, in code and in runtime-loaded markdown.
- The old `memory_*` names survive ONLY in the surfaces plan-4 deliberately deferred to this unit:
  the **human docs** (`AGENTS.md` ~15, `README.md` ~12, `docs/*`, `CHANGELOG.md` ~6) and a handful of
  **stale test references**. Updating those is already inside this unit's scope, so no separate
  rename step is needed.
- The `remember` tool surface today is `path`, `content`, `attributes` (`{key:[values]}`),
  `keywords` — the removed carrier params (`description`/`tags`/`codeRefs`/`reviewAfter`/`reviewWhen`)
  are gone. `search` takes `pathPrefix` and `attributeFilter` among others.

### Why the guide rewrite matters (the key requirement)

`prompts/guide.md` is both **stale** and **wrong in voice**:
- **Stale:** its tool table documents removed params (`description/tags/codeRefs/reviewAfter/reviewWhen`);
  `search` params omit `pathPrefix`/`attributeFilter`; review examples use the old `reviewAfter:`
  surface; the "typed keywords → structured fields" section (L50–61) predates the explicit
  `attributes` param and conflates attributes with keywords.
- **Wrong voice:** sections like "Store proactively" and "Every task, ask yourself" are exhortations
  *at* an agent — commentary — not instruction that *teaches the tools*.

**Binding requirement:** the rewritten guide is a genuine authored guide that teaches *use of the
tools* (mental model → tools → worked examples → reference). It must NOT read largely like agent
commentary. Approved shape: **concept-first, manual-style (option B)**.

## Scope

**In scope:** `src/Scrinia.Mcp/prompts/guide.md` (full rewrite); `src/Scrinia.Mcp/ScriniaPrimer.cs`
(light content touch if needed — names already bare); repo-root `AGENTS.md`; human docs under
`docs/**` and `README.md`; the 8 embedded skills (`src/Scrinia.Mcp/skills/*.md`, audit-and-fix-only);
coupled test assertions; stale `memory_*` references in tests.

**Out of scope (explicit):**
- `CHANGELOG.md` — a historical record; rewriting old entries' tool names would falsify history.
- `workspace-scrinia/CLAUDE.md` — lives **outside this repo's git tree**; separate workspace concern.
- Any product-logic / tool-surface change (the surface is already settled).
- Benchmarks (`benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`) — own tool-name
  strings, plus live user WIP.
- A new CI/doc-parity enforcement apparatus — `AGENTS.md` already tracks doc-parity as a deliberate
  known-gap; not adding one here unless separately requested.

## 1. `guide.md` — full rewrite (concept-first)

Structure:

**Concepts (teach the model first)**
1. **What Scrinia is** — 2–3 sentences: persistent, project-scoped memory + reusable skills via MCP
   tools; lives in `.scrinia/`, travels with the code.
2. **Memories & paths** — a memory lives at a path; paths organize hierarchically and auto-tag for
   search; scope a search to a path prefix.
3. **Chunks** — `content` is an array, each element one independently retrievable chunk; design
   chunks around concepts, not size.
4. **Attributes** — `{ key: [values] }` typed facets that *link and filter*. Recommended keys:
   person/place/org/thing/topic (entities), file/ref (dependencies, hashed for drift), review (ISO
   date and/or free-text condition). Any key allowed. Powers exact-match filtering + the entity graph.
5. **Keywords** — distinctive terms *and* the phrasings a searcher would use; merged with
   auto-extracted terms and seeded into search.
6. **Attributes vs. keywords** — the distinction the current guide muddles: attributes = structured
   facets (link/filter); keywords = free-text search signal. Use both.
7. **The entity graph** — attributes link related memories across shared entity *values*; name an
   entity the same way every time, type honestly (`person:paris` vs `place:paris`).

**Tools (teach each tool)**
Each of `remember, recall, search, list, append, forget, compact, link, restore, reconcile` (plus a
short note on `guide` itself), documented as: *what it does · how to call it (bare name, current
params) · a worked example · when to reach for it*. The prior "search-first / store-proactively"
guidance survives ONLY as concise instruction folded into each tool's "when to reach for it".

**Reference**
Reserved-paths table (the full set incl. `/skill/ /agent/ /patterns/ /findings/ /learn/ /sessions/
/checkpoint/ /temp/`) · reading responses (`instruction`/`followUp`/`actionNeeded`/`status`/`action`)
· skills (load/list/override + the 8 built-ins) · sessions · recovery (`restore`) · workspace
(`.scrinia/` in commits) · Scrinia vs. platform auto-memory.

**Removed:** the standalone "Store proactively" and "Every task, ask yourself" commentary blocks.

## 2. `AGENTS.md` — targeted refresh

- L11 / L31: drop "exposes two MCP tools (`guide`, `memory`)" and the `memory()`-dispatcher
  entry-point note → `guide()` + ten bare per-verb tools.
- Tool table (L69–92): rename `memory_*` → bare; update descriptions to reflect the
  attributes+keywords surface.
- Reserved-paths table (L122–133): add `/findings/` and `/learn/` (currently out of sync with the
  guide).
- Project layout: refresh the `Search/` line for the attributes machinery
  (`KnownFields`/`FieldExtractor`/`AttributeReaders`) and add `Migration/`.
- Keep contributor content (Core Abstractions, Pitfalls, Testing, Contributing, known-gaps).

## 3. Human docs — accuracy sweep

`README.md`, `docs/getting-started.md`, `docs/cli-reference.md`, `docs/architecture/core.md`,
`docs/multi-user-setup.md`, and any other `docs/*` with `memory_<verb>` or old-model hits: bare tool
names + new model terms (fields→attributes; drop `description`/`anchors` as separate concepts;
reflect keywords+attributes). Verify each file against current behavior; don't just token-swap.

## 4. Skills + tests

- Audit the 8 embedded skills (`skills/*.md`); fix only genuinely stale model/surface references
  (leave prose that remains correct).
- Clean incidental stale `memory_*` references in tests (`ScriniaMcpToolsTests.cs`,
  `HintCommandTests.cs`, etc.); leave any that legitimately exercise legacy/compat behavior.

## 5. Verification

- Coupled test assertions (guide/skill/primer output) updated to the rewritten content.
- `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)'`
  returns matches ONLY under `CHANGELOG.md` and the excluded bench/plugin/`.superpowers` paths.
- Manual accuracy pass: the guide's tool list and each tool's params match the registered tools /
  their `[Description]` metadata.
- Build clean (`TreatWarningsAsErrors`); embedded `guide.md` re-embeds on rebuild; full test suites
  green.

## Decomposition note (for the plan)

Natural task boundaries: (1) `guide.md` rewrite + its coupled tests; (2) `AGENTS.md`; (3) human docs
sweep; (4) skills audit + stale-test cleanup. Each is independently reviewable and leaves the build
green. The `guide.md` rewrite is the substantive authoring task; the rest are accuracy sweeps.
