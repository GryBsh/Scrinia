# Scrinia — Agent Memory Guide

Scrinia is persistent, project-scoped memory and reusable skill prompts, exposed as MCP tools. Memories live in `.scrinia/`, so they travel with the code and are shared with every agent on the repo. This guide covers the model and the tools that operate on it.

## The model

### Memories and paths

A memory is content stored at a **path** like `/findings/SEC-001`. Paths are hierarchical and auto-tagged for search — `/api/auth-flow` is findable by `api`, `auth`, `flow`. Scope a search to a prefix: `search({ query: 'auth', pathPrefix: '/api/' })`.

Memory addresses are logical values, not filesystem paths or URIs. Persistent addresses use `/topic/name` (or a bare name at the tool boundary); ephemeral addresses use the canonical `~name` form. `/temp/name` is accepted as an alias and canonicalized to `~name`. Do not pass `file://`, other URIs, drive-letter paths, or physical filesystem paths.

When writing integrations in .NET, use `MemoryAddress.Parse(raw)` when invalid input should throw, or `MemoryAddress.TryParse(raw, out address, out error)` when it should be reported to the caller. Store and plugin contracts use the typed `MemoryAddress`; artifact locations are opaque `ArtifactKey` values and must not be interpreted as URIs.

### Choosing a path

Store facts about people, their preferences, possessions, relationships, and past events in ordinary memory paths. For example, use `/people/user/travel`, `/preferences/food`, or `/events/weekend-hike`. These are examples, not reserved prefixes; choose a topic that makes the fact easy to find later.

Reserve `/agent/...` for the **AI assistant's own persona, role, and behavioral instructions**. A profile of the human user belongs in ordinary memory, even when it contains durable preferences or working habits. Do not put user facts in `/agent/user` or copy them into an assistant persona.

A project instruction file such as `AGENTS.md` is a separate filesystem document. Recording Scrinia usage there does not mean storing the guide or conversation facts under `/agent/...`.

Use `remember` to add information: it creates a missing memory or appends every supplied chunk, preserving prior content. Send only new information; do not repeat the existing memory. Set `replace: true` only with the complete intended replacement. Prefer a separate topic path for an unrelated fact.

### What to retain

Remember past events as well as ongoing preferences, decisions, fixes, and conventions. A fact can be worth recalling even when it does not describe a lasting habit.

For each fact or event you choose to retain:
1. Keep the actor, action, object, explicit dates, quantities, units, reasons, outcomes, and completed/planned/ongoing status together in a self-contained chunk.
2. Keep the source conversation date and original relative time phrase together. When storing memories, retain relative dates as stated instead of adding inferred calendar dates to content or descriptions. Preserve completed actions separately from expected later steps; an expected date does not prove that an event occurred.
3. Preserve distinctions such as another versus the same event, existing facts versus future goals, and still true versus no longer true. An old item is not necessarily disposed of; examples of actual purchases are not hypothetical purchases. Keep earlier facts and later corrections distinct without inventing event relationships.
4. Quote the short source clause when paraphrasing could change its meaning. Compare selected facts and qualifiers against successful writes before finishing; add omitted details.

### Chunks

A memory's `content` is an **array of chunks**, each stored and retrieved independently. Split a memory along its semantically distinct parts — like the sections of a document — not by size:

```
remember({ path: '/api/auth', description: 'Authentication flow and token refresh behavior', content: [
  'OAuth flow: redirect → callback → token exchange',
  'Token refresh: background job every 55 minutes'
]})
```

Retrieve one chunk with `recall({ path: '/api/auth', chunk: 2 })`.

### Attributes

**Attributes** are typed facets — a map of key to values — that both **filter** searches and **link** related memories:

```
attributes: { person: ['maria'], org: ['acme'], review: ['2026-08-01'] }
```

Keys Scrinia understands specially:

- **Entities** — `person`, `place`, `org`, `thing`, `topic`. The recommended vocabulary for the people, places, orgs, things, and themes memories link on.
- **Dependencies** — `file`, `ref`. Recorded (and, for `file`, hashed) so Scrinia flags drift when a referenced file changes.
- **Review** — `review`. An ISO date (`2026-08-01`), a condition (`when auth changes`), or both; flagged memories surface as `[stale]` / `[review?]` in `list`. Scrinia auto-adds one to memories citing specific counts (e.g. "759 tests").

Any key is allowed — these are just the ones with built-in behavior.

### Keywords

**Keywords** are the distinctive terms and likely search phrasings for a memory. They merge with the terms Scrinia extracts from the content and seed the search index: `keywords: ['oauth flow', 'token refresh']`.

### The entity graph

Attributes build a graph linking memories that share a **key and value** — not just entity keys: every key is treated uniformly, so `project:apollo` links memories just as `person:maria` does. A search naming one linked value surfaces related memories that share no query words — a note tagging both `org:acme` and `person:maria` lets a query about Acme reach Maria. Two habits keep links clean:

- **Name a value the same way every time** — `person:maria`, never also `person:maria rodriguez`.
- **Type honestly** — the entity type separates a same-named person and place (`person:paris` vs `place:paris`).

## The tools

### remember — store a memory

`remember({ path, content, description, attributes?, keywords?, replace? })` — `content` is a string array, one chunk per element.

```
remember({ path: '/findings/SEC-001', description: 'Open redirect finding, affected login code, and proposed fix',
  content: ['Login form accepts an unescaped `next` param', 'Fix: allowlist redirect targets'],
  attributes: { topic: ['auth'], file: ['src/Login.tsx'], review: ['when auth changes'] },
  keywords: ['open redirect', 'login next param'] })
```

Use it for information worth recalling later, including past events, preferences, decisions, fixes, and conventions. Keep the explicit details attached to the facts you choose to remember.

### recall — read a memory

`recall({ path, chunk? })` — the whole memory, or one chunk if you pass `chunk`. `recall({ path: '/skill/qa' })` loads a skill; `recall({ path: '/skill/' })` lists skills.

### search — find memories

`search({ query, pathPrefix?, attributeFilter?, scopes?, context?, limit?, excludeTopics? })` — ranked matches, each with a memory description, available chunk excerpts, and attributes to help you choose what to recall. These are partial retrieval hints.

```
search({ query: 'oauth flow', pathPrefix: '/findings/', attributeFilter: { topic: ['auth'] } })
```

Use it before non-trivial work; skip it when the request is self-contained.

### list — browse memories

`list({ mode?, scopes?, offset?, limit?, excludeTopics? })` — `mode` is `summary` (default), `full` (a paginated table), or `drift` (a code-reference drift check). Stale and review-flagged memories are marked here.

### forget — delete a memory

`forget({ path })`.

### compact — merge a memory's chunks

`compact({ path, keepRecent? })` — merges a multi-chunk memory into one and archives the original (except for an ephemeral `~name` memory, or one with no prior index entry); `keepRecent: N` keeps the N most recent chunks.

### link — connect two memories

`link({ path, destination, reason? })` — a bidirectional link between two memories.

### restore — resume your context

`restore()` — reloads the AI assistant's persona, patterns, today's session log, skills, and unresolved conflicts. Rarely called directly; `guide()` folds it in at session start.

### facts — query the entity graph

`facts({ op, entity?, entities?, key?, scopes?, excludeTopics?, limit? })` — aggregate and relational questions `search` can't express. Three ops: `entities` lists the known entity vocabulary, `profile` returns one entity's counts, date span, and co-occurrences, and `intersect` returns memories mentioning *all* of the given entities.

```
facts({ op: 'intersect', entities: ['person:jolene', 'place:france'] })
```

### guide — this guide

`guide()` returns this guide and restores your workspace context in one call. Call it once at session start.

## Reference

### Workspace tracking and shared bases

Two configuration files track workspace state. `<workspace-root>/.scrinia/config.json` remains a flat
`Dictionary<string,string>`; its owned scalars are `Scrinia:Workspace:Tracking` (`local` or
`project-git`) and `Scrinia:Shared:Enabled` (an ordered semicolon-separated list of canonical names).
An absent Enabled key means zero enabled bases; an empty value is invalid. `SCRINIA_HOME/config.json`
is the typed registry with `schemaVersion: 1` and `sharedBases` entries.

`SCRINIA_HOME` uses a non-empty absolute override exactly, fails closed for empty or relative values,
and otherwise resolves exactly to
`Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scrinia")`.
Tracking precedence is `SCRINIA_WORKSPACE_TRACKING`, `SCRINIA__WORKSPACE__TRACKING`, the workspace
scalar, then the command default. Enabled is read directly from workspace config; neither
`SCRINIA_SHARED_ENABLED` nor `SCRINIA__SHARED__ENABLED` is honored.

The supported command forms are:

```text
scri shared base add <name> <absolute-root> [--json]
scri shared base remove <name> [--json]
scri shared base list [--all] [--json]
scri shared enable <name> [--json]
scri shared disable <name> [--json]
scri tracking status [--json]
scri tracking set local|project-git [--json]
scri tracking migrate-legacy --to local|project-git [--json]
```

Shared bases always use fixed `project-git`; canonical names match `^[a-z][a-z0-9-]{0,31}$`.
Validation is filesystem-only and never invokes Git or inspects its index. Mutations use an
interprocess lock and same-directory atomic replacement, preserving unrelated keys and unmanaged
lines. Results report `gitIndexStatus: UNVERIFIED`. Disabled registry names and roots stay private
to the registry and are not disclosed by workspace resolution or diagnostics; only the explicit
user `scri shared base list --all` command enumerates the complete registry, including disabled
entries; the default list shows enabled entries only.

Tracking states are `fresh`, `policy-required`, `legacy-clean`, `legacy-mixed`, `legacy-manual`,
`legacy-unsafe`, `local`, `project-git`, and `policy-drift`. Errors use the declared Core codes,
including `LEGACY_MIGRATION_REQUIRED`, `MANAGED_FILE_UNSAFE`, `TRACKING_POLICY_REQUIRED`,
`TRACKING_POLICY_INVALID`, `MIGRATION_MANUAL_CLEANUP_REQUIRED`, `CONFIG_LOCK_TIMEOUT`,
`CONFIG_WRITE_FAILED`, `CONFIG_RECOVERY_REQUIRED`, and `IO_FAILURE`, with argument/configuration,
policy/state, and filesystem
failures separated into exit classes 2, 3, and 4. `migrate-legacy` removes only recognized legacy
artifacts and preserves unrelated content; no active merge surface is part of this
contract.

### Reserved paths

Any path works; a few prefixes carry first-class behavior:

| Path | Purpose |
|------|---------|
| `/skill/...` | Reusable specialist prompts (load via `recall`, override by storing) |
| `/agent/...` | AI assistant persona and behavioral instructions; not human-user facts |
| `/patterns/...` | Recurring patterns and conventions |
| `/findings/...` | Audit, review, and QA findings |
| `/learn/...` | Retrospectives and durable lessons |
| `/sessions/...` | Session logs by date (`YYYY-MM-DD`) |
| `/checkpoint/...` | State snapshots |
| `/temp/...` | Ephemeral — dies on process exit. An alias for `~name`; both spell the same memory. |

Any other path is plain memory, grouped by its first segment.

The CLI is persistent-only. Its memory commands reject both `~name` and `/temp/name` with a forbidden-address error because each CLI invocation is a short-lived process. Use an MCP client connected to the long-lived `scri serve` process for ephemeral memories. URI-shaped or physical paths are rejected as unsupported input rather than resolved.

### Reading responses

Every response carries structured fields:

- `instruction` — what to do next; follow it.
- `followUp` — paths worth loading (the response returned a short summary to save tokens).
- `actionNeeded` — blocking warnings (merge conflicts, drift); address these first.
- `status` / `action` — what happened.

### Skills

Skills are specialist prompts you load when you hit a domain you were not optimized for:

```
recall({ path: '/skill/qa' })                          — load a skill
recall({ path: '/skill/' })                            — list skills
remember({ path: '/skill/my-helper', description: 'Specialist instructions and when to use them.', content: [...], replace: true }) — create or override
```

Built-ins: `auditor`, `qa`, `debugger`, `chaos-engineer`, `onboarder`, `evolutionary`, `self-reflector`. Built-ins are embedded in Scrinia. Project overrides live at `.scrinia/skills/{name}.md` and take precedence. To compare an override with the current built-in before merging, use `recall({ path: '/skill/{name}', withBuiltin: true })`.

### Sessions

Keep a running log: `remember({ path: '/sessions/2026-07-16', description: 'Session decisions, completed work, and open follow-ups.', content: ['...'] })`.

### Recovery

After a context reset, `restore()` brings back the AI assistant's persona, patterns, session log, skills, and unresolved conflicts. Read its `actionNeeded` warnings first, then follow `followUp`.

### Workspace

Scrinia writes to `.scrinia/`. Commit those changes — that is how memory travels with the code. To
share memories across a team, commit `.scrinia/` like any other source file; to share the same
memories between several repositories, register a shared base with `scri shared base add <name>
<absolute-root>` — shared bases are always fixed to the `project-git` tracking policy.


## Descriptions and evidence retrieval

Every MCP `remember` call requires a nonblank `description`, including appends and reserved paths. Describe the subjects and kinds of evidence in the whole resulting memory. It replaces the previous description even on append; recall earlier content when needed to preserve its scope. Keep detailed evidence in `content`. Existing stores and CLI writes retain their excerpt fallback; no migration is required.

Do not repeat inferred calendar dates from memory summaries. Use the original relative phrase and source conversation date. Calculate a date only if the question needs it and it can be verified from that anchor; otherwise keep the range or uncertainty. An expected date does not prove occurrence. Count the requested action: an order placed to buy an item counts as buying unless contradicted or cancelled. Delivery and possession are separate; require evidence of either only when asked about that state. Plans are not placed orders. Do not add unstated completion conditions.

1. Use search descriptions and excerpts to choose what to recall. Read the full relevant content before answering; retrieve individual chunks if recall is truncated, including dates, qualifications, and corrections.
2. For counts, totals, and lists, gather all relevant candidates. Track source, date, status, and inclusion/exclusion reasons before calculating. Repeated mentions alone do not prove the same or different events.
3. For counts, totals, and lists, short or untruncated search results do not prove complete coverage. Vary category/action terms and browse `list({ mode: 'full', limit: 50 })` for missing paths. Check further inventory pages within the request budget and recall relevant evidence. If hits are truncated or off-topic, refine by subject, path, or attributes. Disclose unchecked areas; a missing preview does not prove a fact is absent.
4. Stop when relevant candidates and contradictions are checked or the request budget is reached. State any remaining uncertainty.
