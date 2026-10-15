# Scrinia CLI Reference

`scri` manages persistent memories and serves Scrinia's local stdio MCP tools.

## Surface overview

The top-level surface is organized by purpose:

| Group | Commands |
|---|---|
| Infrastructure | `serve`, `setup`, `config`, `reindex` |
| Lifecycle | `guide`, `restore`, `consolidate`, `hint`, `migrate` |
| Memory operations | `list / search / show / store / forget / append / compact / link` |
| Bundle files | `bundle export / import / pack` |

`scri migrate` also runs automatically on first store access; run it to preview or control timing.
`scri migrate_attributes` separately forward-fills older entries onto the attributes model.

## Commands

### Exit status

Commands exit 0 only when the requested operation succeeds. They exit 1 for an error and for a
warning: a completed command that found no matches, had no memories to list, could not compact
because no chunks would be removed, or committed an append with a non-fatal warning. Warning
output remains structured as `status: warning` or `status: success_with_warning`, not an error.

#### Stable strict-safety errors

JSON and MCP responses preserve the same machine-readable codes: `MALFORMED_ADDRESS`,
`SCOPE_CONTAINMENT_FORBIDDEN`, `RESERVED_PATH_OPERATION_FORBIDDEN`, `INVALID_BUNDLE`,
`BUNDLE_IMPORT_FAILED`, `BUNDLE_RECOVERY_REQUIRED`, `EMBEDDINGS_PROTOCOL_UNSUPPORTED`, and
`EMBEDDINGS_PROTOCOL_INVALID`. Correct the logical address or reserved-path operation as indicated;
do not treat a refusal as a successful migration. Recovery codes preserve evidence; follow
[evidence-preserving troubleshooting](troubleshooting.md#bundle-recovery-required). A durable
bundle commit remains success when evidence cleanup is deferred, but retained bundle evidence
blocks later persistent operations until supported verification/cleanup succeeds.

### scri serve

Start the local MCP server over stdio.

```bash
scri serve [--workspace-root <path>] [--no-auto-setup]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--workspace-root` | Auto-detected | Override workspace directory |
| `--no-auto-setup` | `false` | Skip the first-run embedding model download |

Reads and writes `.scrinia/`. On first launch, it downloads the built-in `m2v-MiniLM-L6-v2` model (~22MB) unless `--no-auto-setup` is set. Progress uses stderr; download failure leaves the server running with BM25-only search until `scri setup` succeeds.

The CLI has two local stdio server surfaces: `scri serve` exposes eleven workspace tools and
`scri shared serve` exposes eleven disjoint `shared_*` tools for enabled shared bases. There is no
remote, HTTP, API-key, or `--store` mode. Share memories through source control and resolve conflicts
manually. `serve` never installs an embeddings release bundle.

### scri shared serve

Serve enabled shared-memory MCP tools over stdio. The command fails before opening transport when no shared base is enabled. Shared calls do not carry prompt, correlation, or session fields and never launch Git.

```bash
scri shared serve [--workspace-root <path>]
```

### scri list

List stored memories. By default, it prints the full table; use `--summary` for topics, keywords, and stats.

```bash
scri list [--workspace-root <path>] [--scopes local,api]
    [--summary] [--offset 0] [--limit 0] [--json]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--summary` | `false` | Show summary view (topics, keywords, stats) instead of full table |
| `--offset` | `0` | Skip this many entries (for pagination) |
| `--limit` | `0` | Maximum entries to return (`0` = unlimited) |
| `--json` | `false` | Output as JSON |

### scri search

Search memories with weighted lexical scoring and, when available, semantic vectors.

Retrieval is bounded by `Scrinia:Search:CandidateLimit` (default `200`, inclusive range `1`–`1000`).
Invalid values are configuration errors. Scope, excluded-topic, path/address, and attribute
eligibility precede lexical ranking and K. All scoring contributors, entity facts, and surfaced
facts receive that same bounded snapshot, using the context-augmented query. K is separate from
`--limit`; increasing K deliberately increases retrieval work/latency.

```bash
scri search "query" [--workspace-root <path>] [--scopes local,api] [--limit 20]
```

### scri facts

Query entity facts with `entities`, `profile`, or `intersect`.

```bash
scri facts <entities|profile|intersect> [target] [--key person] [--workspace-root <path>]
    [--scopes local,api] [--exclude-topics plan] [--limit 50] [--json]
```

`profile` accepts one entity as `key:value` or a bare value. `intersect` accepts comma-separated entities. CLI scope and topic filters are comma-separated; use MCP `facts` for names containing commas.

### scri store

Store text from a file or stdin (`-`) as a named memory.

Persistent memory subjects may be at most 182 characters after filename sanitization. The limit
applies to the sanitized persistent subject, not the whole scoped display path.

#### Address input

CLI commands that accept a memory address accept only persistent logical addresses. A bare subject is normalized as a
persistent address; `/topic/subject` is a persistent topic address. Ephemeral `~name` and
`/temp/name` addresses are MCP-only and the CLI rejects them with exit status 1 and a
`FORBIDDEN_ADDRESS_KIND` error.

URI and physical-path inputs are not supported. URI syntax such as `file://` is rejected with
`UNSUPPORTED_INPUT_FORM`; other malformed physical-path forms are rejected as
`MALFORMED_ADDRESS`.
They are never resolved against the local filesystem. In .NET code,
use `MemoryAddress.Parse(raw)` or `MemoryAddress.TryParse(raw, out address, out error)` to obtain
the validated typed address used by store-facing APIs.

`scri store --json` emits `name`, `originalBytes`, and `message`; do not rely on `chunkCount`.
Successful ordinary, `/skill/name`, and `/agent/name` writes report the persisted identity
without a post-commit response-projection failure. Ordinary stored spelling is preserved;
reserved names report their canonical reserved path. Warnings and recovery errors keep their
status, operation/recovery metadata, and stable code.

```bash
scri store <name> [file] [--workspace-root <path>]
    [-d description] [-t tag1,tag2] [-k keyword1,keyword2]
    [--review-after 2026-06-01] [--review-when "when auth changes"]
```

**Examples:**

```bash
scri store session-notes ./notes.md
scri store /api/auth ./auth.md -k oauth,jwt --review-when "when auth system changes"
cat notes.md | scri store session-notes -
```

**Retrieval anchors.** Anchors are supplied as the **`anchor` attribute** via
the MCP `remember` tool's `attributes` parameter (`attributes: { anchor: [...] }`)
— the CLI `store` surface does not expose it as a flag. Anchors are
agent-supplied **retrieval bridges**: short phrases, questions, or category-level
paraphrases that bridge likely user queries to the entry's content. Use the
vocabulary you expect users to query with, not the vocabulary already in the
content (the indexer already covers exact-match). Anchor values are seeded
into the entry's term-frequency dict at write time (so BM25 picks them up
naturally) and are scored like any attribute value (see the attributes model).

```jsonc
// MCP call shape — what an agent passes to remember
{
  "path": "/findings/auth-refactor",
  "content": ["… memory body …"],
  "attributes": {
    "anchor": [
      "how do I rotate session tokens",
      "what's the OAuth grant flow used by this service",
      "when did the auth refactor land"
    ]
  }
}
```

### scri show

Decode and display a memory's full content. Optionally write to a file.

```bash
scri show <name> [--workspace-root <path>] [-o output.md]
```

### scri forget

Delete a stored memory and remove its index entry.

`scri forget --json` emits `name` and `message`. Consumers must stop reading `success`; use the
process exit and response message instead.

```bash
scri forget <name> [--workspace-root <path>]
```

### scri guide

Print the embedded agent guide — the same document MCP clients receive when they
call the `guide()` tool. Useful when working from a terminal without an MCP session.

```bash
scri guide [--json]
```

Default output is the raw Markdown. `--json` wraps the response in the standard
MCP envelope (`{ action, status, yaml }`) for tooling consumption.

### scri append

Append a chunk to a memory, creating it when absent. Existing memories are archived before append; a new memory has no prior version to archive.

```bash
scri append <name> [<file>] [--workspace-root <path>] [--json]
```

```bash
# From a file
scri append session-notes ./more.md

# From stdin
echo "another paragraph" | scri append session-notes
```

If an ordinary target memory does not exist, `append` creates a single-chunk memory. Missing
`/skill/name` and `/agent/name` targets return `NOT_FOUND`; an existing embedded built-in skill
can be promoted to a disk override.

Reserved append reads the complete body under the entity lock, including text beyond response
display caps. It appends without repeating skill scaffolds, preserves curated/unknown metadata,
and archives the complete prior body and metadata. Serial concurrent appends retain both
suffixes. Skill lookup is disk override, then embedded built-in; legacy NMP/2 skills are not a
fallback.

Append responses include `operationId`. Committed work with non-fatal cleanup or event-delivery
trouble returns `success_with_warning` and exits 1. Recovery-required results retain journal
evidence and diagnostics; follow [recovery guidance](troubleshooting.md#bundle-recovery-required)
instead of assuming a dedicated repair command exists.

Persistent memory subjects may be at most 182 characters after filename sanitization. The limit
applies to the sanitized persistent subject, not the whole scoped display path.

### scri compact

Merge the chunks of a multi-chunk persistent memory back into a single chunk (or keep the N
newest). The pre-compact version is archived first when it has a prior index entry.

```bash
scri compact <name> [--keep-recent N] [--workspace-root <path>] [--json]
```

- Default (`--keep-recent 0`): all chunks are concatenated into one.
- `--keep-recent N` (N ≥ 1): keep only the N most recent chunks; older ones are dropped.

```bash
scri compact session-notes               # merge all chunks
scri compact session-notes --keep-recent 5  # keep last 5
```

### scri consolidate

Compact eligible session entries and report stale memories. It does not run an LLM.

**What it does:**

- Compacts multi-chunk session entries (paths under `/sessions/` or `sessions:`)
  older than `--session-age-days` (default 7) into single-chunk form. Originals
  are archived under `versions/`.
- Reports stats: total memories, total bytes, count of entries past their `review` date.

Stale entries (past their `review` date) are reported but NOT deleted —
review them with `scri list` and decide manually.

```bash
scri consolidate [--auto] [--dry-run] [--debounce-minutes 30] \
    [--session-age-days 7] [--json] [--workspace-root <path>]
```

- `--auto` enables debounce: skips the run if `.scrinia/.last-consolidation` shows
  a run within `--debounce-minutes`.
- `--dry-run` previews actions without writing.
- After a non-dry run, `.scrinia/.last-consolidation` is updated with the current
  UTC timestamp.

`scri consolidate` is manual. Run it directly or from a scheduled task; `scri setup --hooks` does not install a SessionEnd hook.

### scri link

Create a bidirectional reference between two memories. Both sides get a `ref`
attribute naming the other, so searches and graph traversals discover the
connection. Both memories must already exist — if either is missing, neither is
written.

Ephemeral `~name` memories cannot be linked from the CLI. An ephemeral memory
lives only in the memory of the process that created it, and every `scri`
invocation is a fresh process, so a `~name` is never present when the command
starts — `scri link ~scratch /patterns/x` is refused outright with an
ephemeral-address error (exit 1), before either side's existence is even
checked. The MCP `link` tool does support ephemerals, because `scri serve` is a
long-lived process that still holds the ones it created.

```bash
scri link <from> <to> [-r reason] [--workspace-root <path>] [--json]
```

```bash
scri link /api/auth-flow /patterns/retry -r "auth retries use the documented pattern"
```

### scri maintenance

Clean eligible archive pairs and verified terminal recovery evidence in one bounded batch.

```bash
scri maintenance [--workspace-root <path>] [--scopes local,api]
    [--batch-size 1..50] [--continuation-token <token>] [--json]
```

Omit `--batch-size` to use the configured value. Reuse the returned opaque continuation token to continue work. Invalid policy, lock contention, partial failure, and invalid continuation exit 1.

### scri restore

Restore saved agent context: the agent profile, patterns, today's session log, and available skills.

```bash
scri restore [--workspace-root <path>] [--hook [--client codex|claude|opencode|copilot]] [--json]
```

Use `--hook` for the SessionStart hook payload. With `--hook`, `--client` tailors that payload for the named supported client.
This command restores agent context, not transaction evidence or filesystem backups.

Maintenance handles bounded archive and terminal-evidence cleanup; it is not a universal bundle
journal repair. File-entity cleanup verifies committed existence as well as bytes. Unexpected
entries or unprovable inspection retain evidence; preserve it and use the
[recovery procedure](troubleshooting.md#bundle-recovery-required).

### scri bundle export

Export one or more topics to a portable `.scrinia-bundle` file (ZIP format).

`scri bundle export --json` emits `message`; do not rely on `path`.

Export captures one admitted persistent snapshot, prepares and validates the complete ZIP, and
only then replaces its destination. Empty exports and failed publication preserve an existing
output, including an automatically generated filename collision. Cleanup removes only files
owned by this attempt. A detected destination change or unproven compensation retains owned
evidence and reports its paths; preserve it for investigation. Denied cleanup after proven
publication can leave owned sibling files while export remains successful. External writers can
still race between the final ownership check and filesystem mutation.

```bash
scri bundle export <topics> [--workspace-root <path>] [-o filename]
```

```bash
scri bundle export api,arch -o project-knowledge
```

### scri bundle import

Import topics from a `.scrinia-bundle` file.

```bash
scri bundle import <path> [--workspace-root <path>] [--topics api,arch] [--overwrite]
```

Default `--overwrite=false` reserves each logical body/metadata pair: if either destination
member exists, the entire entity is skipped. `--overwrite` replaces the exact pair and removes
a stale destination sidecar when the bundle has none. Ordinary memory names resolve
case-insensitively while preserving existing stored spelling; file entities retain their
existing exact-spelling behavior.

Success counts reflect committed memory entries and file entities, not requested entries.
A file-only import succeeds. An all-skipped import reports no changes with warning status and
exit 1. Skill/agent file entities are included only when the selected topics cover the complete
archive topic set; all declarations are still validated when a filter excludes publication.

Import uses a bounded workspace transaction over ordinary memories and selected file entities.
Stop all old writers before upgrading. Legacy/unknown retained evidence is refused, not converted
automatically. See [exact archive/journal limits](security.md#bundle-and-journal-limits) and
[backup/recovery guidance](troubleshooting.md#bundle-recovery-required).
`INVALID_BUNDLE` is an invalid archive, `BUNDLE_IMPORT_FAILED` is a refusal before mutation or
proven rollback, and `BUNDLE_RECOVERY_REQUIRED` retains unproven evidence. A proven durable
commit remains success despite cleanup failure; later persistent operations require verified
cleanup. Derived vectors, facts, and configuration are outside the bundle rollback manifest.

### scri bundle pack

Pack raw files from disk into a `.scrinia-bundle` without storing them as memories first. Useful for sharing documentation or code knowledge.

```bash
scri bundle pack <topic> <files> [--workspace-root <path>] [-o filename] [-d description] [-t tags]
```

```bash
scri bundle pack docs "src/**/*.md" -d "Source documentation" -t docs,reference
```

### scri setup

Set up embeddings and optional agent hooks:

1. **Ollama auto-detection for embeddings** (`http://localhost:11434` by default).
   Probes `GET /api/tags`; if reachable, prompts whether to use Ollama for
   embeddings. On `yes`:
   - Picks an embedding model from already-pulled models, or pulls
     `nomic-embed-text` (default), or accepts a typed name.
   - Streams `POST /api/pull` with per-layer progress for any missing model.
   - Writes the resulting `Scrinia:Embeddings:*` config to the workspace.
2. **Built-in embedding model** (`m2v-MiniLM-L6-v2`, 384-dim, ~22MB) downloaded
   from HuggingFace to `SCRINIA_HOME/models/m2v-MiniLM-L6-v2/`. Only used when
   Ollama isn't configured and the Vulkan embeddings plugin isn't installed.

Setup does **not** prompt for any LLM configuration. Scrinia ships zero
LLM-at-rest enrichment — there is no background LLM consulted at write or read
time, so there is nothing for setup to wire up. Agents that
want to enrich memories do so client-side and pass `attributes` (including
`anchor`) and `keywords` through the MCP `remember` tool.

```bash
scri setup [--workspace-root <path>] [--no-ollama] \
    [--hooks] [--uninstall-hooks] [--project] [--embeddings-source <directory>]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--no-ollama` | `false` | Skip the Ollama probe + prompt entirely. |
| `--embeddings-source` | Adjacent manifest, if present | Directory containing `embeddings-bundle.json` and its runtime payload. Cannot be combined with hook management. |
| `--hooks` | `false` | Install SessionStart + UserPromptSubmit hooks into detected agent CLIs (Claude Code, Codex, GitHub Copilot). Skips the model-download flow. |
| `--uninstall-hooks` | `false` | Remove scrinia-managed hooks from agent CLIs. User-authored hooks are preserved. |
| `--project` | `false` | With `--hooks` / `--uninstall-hooks`, target workspace-local config files (`.claude/`, `.codex/`, `.github/hooks/`) instead of user-global (`~/.claude/` etc.). |

When the Ollama probe falls through (declined, unreachable, or `--no-ollama`),
setup clears any prior Ollama-derived embedding config keys
(`Scrinia:Embeddings:Provider=ollama`, the Ollama URL + model). Custom OpenAI /
Voyage / Azure / Google embedding configurations are preserved.

`scri serve` downloads the built-in model on first launch unless `--no-auto-setup` is set. Run `setup` for Ollama, hooks, or explicit download timing.

Extract the embeddings release archive. From the extracted directory, run `./scri setup` on
Linux/macOS or `.\scri.exe setup` in PowerShell on Windows.
With a NuGet-installed host, use `scri setup --embeddings-source <extracted-release-directory>`.
Setup verifies and copies the bundle into
`SCRINIA_HOME/plugins/scri-plugin-embeddings/0.6.0/<rid>/`, preserving source files, and selects
the plugin name/version in workspace configuration. Windows/Linux use Vulkan; macOS uses CPU/Metal.
Checksums establish content consistency, not publisher identity.

Success confirms installation, configuration, and path selection, not plugin initialization or
inference. `InstalledConfigurationFailed` and `InstalledModelPreparationFailed` retain the installed
payload. Correct the reported settings, I/O, or download issue and retry the printed command; other
failed outcomes may leave an uncertain destination. Conflicting plugin selections or provider
environment overrides must be resolved first. Migration of legacy assets stored beside the
executable is a separate step.
See [complete installation and retry guidance](installation.md#embeddings-plugin-installation).

Local preparation downloads missing Model2Vec files to `SCRINIA_HOME/models/m2v-MiniLM-L6-v2/`
and, with the plugin selected, `SCRINIA_HOME/models/all-MiniLM-L6-v2-Q8_0.gguf`. These downloads
require network access. Optional HTTP providers use their configured endpoint.

#### Hook installer (`--hooks`)

Wires scrinia into the user's existing agent CLI sessions. Two hooks are
installed:

- **SessionStart** → `scri restore` — primes context (profile, patterns, today's session log) when the agent starts a session.
- **UserPromptSubmit** → `scri hint` — emits a one-line "relevant memories exist" marker before the agent processes each user prompt.

`SessionEnd` is no longer installed. The previous SessionEnd hook ran
`scri consolidate --auto` to drive an LLM enrichment sweep at session close;
that pipeline is gone, so the hook went with it. If you want session-compaction
housekeeping, run `scri consolidate` manually.

Per-CLI write locations:

| CLI | User scope | Project scope |
|---|---|---|
| Claude Code | `~/.claude/settings.json` | `<workspace>/.claude/settings.json` |
| Codex (0.124+) | `~/.codex/hooks.json` | `<workspace>/.codex/hooks.json` |
| GitHub Copilot (Feb 2026 GA) | `~/.copilot/hooks/scrinia.json` | `<workspace>/.github/hooks/scrinia.json` |

User-authored hooks in the same config files are preserved — scrinia marks its blocks with a `_scriniaManaged` sentinel and only touches those. Re-running `--hooks` is idempotent. `--uninstall-hooks` removes only marked blocks.

### scri hint

Show a pre-send hint for memories relevant to a prompt.

```bash
scri hint [prompt] [--workspace-root <path>] [--json]
echo "user prompt" | scri hint
```

Reads prompt from positional arg if given, otherwise from stdin. Auto-detects JSON envelopes (`{"prompt": "...", ...}`) used by some CLIs' hook protocols and extracts the `prompt` key.

Empty stdout when:
- The prompt is shorter than `Scrinia:Hint:MinPromptChars` (default 8 — skips "hi", "thanks").
- No matches clear `Scrinia:Hint:MinScore` (default 10.0).
- `Scrinia:Hint:Enabled` is set to `false`.

Plain output format: `[scrinia] N memories match: name1, name2, name3. Run search({ query: 'name1' }) to retrieve.`

`--json` returns `{"count": N, "matches": [{"scope", "name", "score"}, ...]}`, indented, through the same source-generated serializer as every other `--json` surface.

`scri setup --hooks` invokes this from UserPromptSubmit; run it directly to tune thresholds.

### scri reindex

Force a full rebuild of every vector file in the workspace. Use when:

- The embedding signature on disk has drifted from the active config and the
  automatic startup quarantine didn't pick it up.
- You suspect a vector file is corrupted (cosine scores look wrong despite
  obvious matches in BM25).
- You changed `Scrinia:Embeddings:ChunkSize` / `ChunkOverlap` and want to verify
  the rebuild ran end-to-end instead of relying on the lazy fallback.

```bash
scri reindex [--workspace-root <path>] [--json]
```

`scri reindex --json` emits `embedded`, `total`, `skipped`, `failed`, and `message`. Consumers
must stop reading `rescuedScopes`; use the observed counters and response message instead.

Enumerates the canonical memories from the store, probes their vector scopes explicitly,
and rebuilds the vectors from sidecars through the active embedding pipeline. Each memory's
replacement is written to a temporary SVF3 file and atomically replaces its existing
`vectors.bin`; no backup tree is created or retained. A failed replacement
leaves the prior durable vector snapshot untouched.

For background context on the auto-quarantine flow that handles 95% of model
switches without needing this command, see
[docs/architecture/embeddings.md](architecture/embeddings.md#chunked-embeddings).

### scri config

Get, set, list, or remove workspace configuration settings.

```bash
scri config                              # List all settings
scri config <key>                        # Get a setting
scri config <key> <value>                # Set a setting
scri config --unset <key>                # Remove a setting
```

```bash
scri config plugins:embeddings my-custom-plugin
scri config Scrinia:Embeddings:Provider ollama
scri config Scrinia:Embeddings:OllamaModel nomic-embed-text
scri config Scrinia:Embeddings:ChunkSize 1800
```

Writes to `Scrinia:Embeddings:*` trigger an automatic vector reindex if the
composed embedding signature changed (provider, model, chunk size, or overlap).

#### Config key reference

**Embeddings**

| Key | Default | Description |
|---|---|---|
| `Scrinia:Embeddings:Provider` | `model2vec` | Provider: `model2vec`, `ollama`, `openai`, `voyageai`, `azure`, `google`, or `none`. HTTP providers skip the built-in Model2Vec + Vulkan plugin entirely. |
| `Scrinia:Embeddings:SemanticWeight` | `50.0` | Multiplier applied to cosine similarity in hybrid scoring. |
| `Scrinia:Embeddings:ChunkSize` | `1200` | Sliding-window size in characters for chunked embeddings. Roughly 300 tokens for English. |
| `Scrinia:Embeddings:ChunkOverlap` | `200` | Overlap in characters between adjacent windows. Must be strictly less than `ChunkSize`; a value at or above it reverts both keys to their defaults with a warning. |
| `Scrinia:Embeddings:MaxChunksPerMemory` | `500` | Safety cap on chunks per memory; excess tail is dropped from embed (BM25 still indexes full text). **Maximum 999** — larger values clamp to 999 with a warning. See the note below. |
| `Scrinia:Embeddings:OllamaBaseUrl` | `http://localhost:11434` | Ollama API endpoint. |
| `Scrinia:Embeddings:OllamaModel` | `all-minilm` | Ollama embedding model name. |
| `Scrinia:Embeddings:OpenAi*` | — | `ApiKey`, `Model`, `BaseUrl` for OpenAI / compatible. |
| `Scrinia:Embeddings:VoyageAi*` | — | `ApiKey`, `Model`, `BaseUrl` for Voyage AI. |
| `Scrinia:Embeddings:Azure*` | — | `Endpoint`, `ApiKey`, `Deployment`, `Model`, `ApiVersion`, `UseV1` for Azure AI Foundry. |
| `Scrinia:Embeddings:Google*` | — | `ApiKey`, `Model`, `BaseUrl`, `Dimensions` for Google Gemini. |

`MaxChunksPerMemory` counts windows across a memory's **whole content array**, and every
non-blank content element contributes at least one window. It is therefore also a floor on how
many content elements can be vector-reachable: a memory of 600 short elements has its 501st
onward dropped from embedding. Because `append` adds elements at the end, the tail this drops is
the *newest* content, which is why the default is generous. The 999 ceiling is structural — the
stored chunk index encodes an element index and a window ordinal against a stride of 1000, so a
cap of 1000 or more would let one element's windows overflow into the next element's index space.

**Search Ranking**

| Key | Default | Description |
|---|---|---|
| `Scrinia:Search:Alpha:Relevance` | `1.0` | Weight applied to the relevance term (attribute match + BM25 + supplemental embedding score) in the final ranking sum. |
| `Scrinia:Search:Alpha:Recency` | `1.0` | Weight applied to the recency term. Set to `0` to rank on relevance alone. |
| `Scrinia:Search:TauDays` | `14.0` | Decay constant for the recency term: `exp(-Δt_days / τ_days)`. |
| `Scrinia:Search:Scale:Recency` | `50.0` | Multiplier applied to the (already 0..1) recency term before summing. |
| `Scrinia:Search:Fusion` | `sum` | How the three relevance signals (attribute match, BM25, supplemental embedding) combine: `sum` (historical additive weighting, lexical-dominant) or `rrf` (Reciprocal Rank Fusion, scale-invariant across sources). |

**Pre-send hint (`scri hint`)**

| Key | Default | Description |
|---|---|---|
| `Scrinia:Hint:Enabled` | `true` | When `false`, `scri hint` is a silent no-op (lets users disable globally without touching every CLI's hook config). |
| `Scrinia:Hint:MinPromptChars` | `8` | Prompts shorter than this skip the lookup entirely (avoids firing on "hi" / "thanks"). |
| `Scrinia:Hint:MinScore` | `10.0` | BM25 score floor — matches below this are suppressed. |

The `plugins:embeddings` key overrides the executable name used to launch the
embeddings plugin process — only useful for custom builds. Scrinia ships no LLM
plugin; the historical `plugins:llm` key has been removed.

### Workspace tracking and shared bases

Two configuration files are used. The workspace file, `<workspace-root>/.scrinia/config.json`, stays
the flat `Dictionary<string,string>` configuration. Its owned scalars are
`Scrinia:Workspace:Tracking` (`local` or `project-git`) and `Scrinia:Shared:Enabled` (an ordered,
semicolon-separated list of canonical names). An absent Enabled key is the valid zero-base state;
an empty value or an empty, duplicate, or noncanonical segment is invalid.

The per-user registry is `SCRINIA_HOME/config.json`, exactly shaped as `schemaVersion: 1` plus a
`sharedBases` object whose canonical keys contain `{ "root": "..." }` entries. A non-empty absolute
`SCRINIA_HOME` is used exactly; empty or relative values fail closed. If it is unset, the resolver
uses exactly `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scrinia")`.
Workspace tracking precedence is `SCRINIA_WORKSPACE_TRACKING`, then
`SCRINIA__WORKSPACE__TRACKING`, then the workspace scalar, then the command default. Enabled is read
directly from the workspace file, so `SCRINIA_SHARED_ENABLED` and
`SCRINIA__SHARED__ENABLED` are deliberately ignored.

The complete command surface is:

```bash
scri shared base add <name> <absolute-root> [--json]
scri shared base remove <name> [--json]
scri shared base list [--all] [--json]
scri shared enable <name> [--json]
scri shared disable <name> [--json]
scri tracking status [--json]
scri tracking set local|project-git [--json]
scri tracking migrate-legacy --to local|project-git [--json]
```

Shared bases always use `project-git`; that policy is fixed and is not stored per base. Canonical
names match `^[a-z][a-z0-9-]{0,31}$`. Roots must be existing directories with filesystem Git
evidence: their own `.git` directory or a valid `gitdir:` marker whose target is an existing
directory. This does not prove a functional repository or worktree. Validation is filesystem-only:
no command invokes Git or inspects its index, and results label the index `gitIndexStatus: UNVERIFIED`.

Mutations take an interprocess lock and use same-directory atomic replacement. A failed replacement
preserves the previous complete bytes or leaves a recovery journal that restores them before the
next operation. Unrelated flat keys and lines outside managed ignore blocks survive transitions.
Disabled registry names and roots are private to the registry and are absent from workspace
resolution, diagnostics, and warnings; only `scri shared base list --all` enumerates the complete
registry, including disabled entries. Without `--all`, the list is limited to enabled entries.

Tracking states are `fresh`, `policy-required`, `legacy-clean`, `legacy-mixed`, `legacy-manual`,
`legacy-unsafe`, `local`, `project-git`, and `policy-drift`. Declared errors include
`CLI_INVALID_ARGUMENT`, `WORKSPACE_CONFIG_MALFORMED`, `USER_CONFIG_MALFORMED`,
`USER_CONFIG_SCHEMA_UNSUPPORTED`, `SHARED_BASE_ALREADY_EXISTS`, `SHARED_BASE_NOT_FOUND`,
`SHARED_BASE_ROOT_COLLISION`, `SHARED_BASE_UNSAFE_PATH`, `SHARED_BASE_NOT_GIT_ROOT`,
`SHARED_TRACKING_POLICY_DRIFT`, `LEGACY_MIGRATION_REQUIRED`, `MANAGED_FILE_UNSAFE`,
`TRACKING_POLICY_REQUIRED`, `TRACKING_POLICY_INVALID`, `MIGRATION_MANUAL_CLEANUP_REQUIRED`,
`CONFIG_LOCK_TIMEOUT`, `CONFIG_WRITE_FAILED`, `CONFIG_RECOVERY_REQUIRED`, and `IO_FAILURE`. Exit
classes are 2 for argument or
configuration errors, 3 for policy/state refusals, and 4 for lock, filesystem, or atomic-write
failures. `tracking migrate-legacy` is the only form that removes recognized legacy artifacts; it
preserves unrelated content and never starts an active merge surface.

### scri migrate

Migrates a workspace store to the v3 layout, where every memory lives under
`.scrinia/memories/`. Migration also runs **automatically** on first store access, so this
command is only needed to preview the plan, migrate deliberately before a large operation,
or re-run after restoring a backup.

```bash
scri migrate [--workspace-root <path>] [--dry-run] [--skip-backup] [--json]
```

| Flag | Default | Description |
|---|---|---|
| `--workspace-root` | cwd | Workspace root containing the `.scrinia/` directory. |
| `--dry-run` | false | Print how many files would move without moving any. |
| `--skip-backup` | false | Skip the timestamped backup; the backup runs by default. |
| `--json` | false | Output as JSON instead of formatted text. |

Files are **moved**, not copied — exactly one copy of each memory exists at all times. Where
two legacy locations map to the same target (for example `topics/api/x` and
`topics/memory/api/x`), the non-namespaced entry wins and the other is moved to
`.scrinia-conflicts-{timestamp}/` rather than being discarded.

To roll back, stop any running Scrinia process and replace `.scrinia/` with the backup
directory.

Unrelated command with a similar name: `scri migrate_attributes` forward-fills older entries
onto the attributes model (tags/codeRefs/review/anchors → `attributes`/`keywords`) — it does
not touch the on-disk layout.

## JSON Output

All CLI commands support a `--json` flag for machine-readable JSON output. This uses a source-generated `CliJsonContext` for trimming safety.

```bash
scri list --json
scri search "auth" --json
scri show /api/auth-flow --json
```

## Workspace

Scrinia stores all data in a `.scrinia/` directory at the workspace root:

```
.scrinia/
  memories/           Persistent memory artifacts and metadata sidecars
    session-notes.nmp2
    session-notes.meta.json
    api/              Topic "api" memory artifacts and sidecars
  embeddings/         Vector data (created when semantic embeddings are enabled)
  exports/            Exported .scrinia-bundle files
  config.json         Workspace configuration
```

### Workspace Discovery

When `--workspace-root` is not specified, `scri` walks up from the current working directory looking for the nearest enclosing git repository, identified by a `.git` entry (a directory in a normal checkout, or a file pointing at `gitdir:` in a worktree or submodule). That repository's root becomes the workspace root. If no git repository encloses the working directory, the working directory itself is the workspace root and no upward search happens.

`.scrinia/` is created lazily -- only when something is written. Reading commands (`hint`, `search`, `show`, `list`) against a workspace with no store create nothing.

If a `.scrinia/` directory exists somewhere resolution deliberately did not use (for example, in an ancestor directory outside the resolved root), writes, `setup`, and `serve` startup print a warning to stderr naming the ignored store, the root in use, and a `scri bundle export` / `scri bundle import` pair to migrate its memories into the resolved workspace.

## Configuration

Settings are resolved in priority order:

1. **Environment variable** (highest) -- either the single-underscore form, key with `:` replaced by `_` and uppercased (e.g., `SCRINIA_EMBEDDINGS_PROVIDER`), or the .NET-standard double-underscore form (e.g., `SCRINIA__EMBEDDINGS__PROVIDER`). When both are set the single-underscore form wins.
2. **Config file** -- `.scrinia/config.json` in the workspace root
3. **Default value** (lowest)

The config file is read once per process and cached, so a change written by another process is not
picked up by an already-running one. That costs nothing for CLI commands, where every invocation is
a fresh process; restart a long-running `scri serve` after `scri config set`.

### General Settings

| Key | Default | Description |
|-----|---------|-------------|
| `plugins:embeddings` | `scri-plugin-embeddings` | Embeddings plugin executable name |

### Embedding Provider Settings

| Key | Default | Description |
|-----|---------|-------------|
| `Scrinia:Embeddings:Provider` | `model2vec` | Provider: `model2vec`, `ollama`, `openai`, `voyageai`, `azure`, `google`, `none` |
| `Scrinia:Embeddings:SemanticWeight` | `50.0` | Semantic score weight in hybrid search |

### Model2Vec Provider (Default)

The default provider. Runs the `m2v-MiniLM-L6-v2` model (384 dimensions, distilled from all-MiniLM-L6-v2) locally with zero native dependencies. Pure C# implementation using SafeTensors format (F16).

Setup:

```bash
scri setup
```

Downloads `model.safetensors` (~22MB) and `vocab.txt` to `SCRINIA_HOME/models/m2v-MiniLM-L6-v2/`.

### Vulkan Provider (Optional Plugin)

GPU-accelerated embeddings via LLamaSharp with Vulkan backend. Requires the plugin to be installed:

```bash
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithEmbeddings
```

When the Vulkan plugin is installed, it automatically overrides the built-in Model2Vec provider.

### Ollama Provider

Uses a local or remote Ollama instance for embeddings.

| Key | Default | Description |
|-----|---------|-------------|
| `Scrinia:Embeddings:OllamaBaseUrl` | `http://localhost:11434` | Ollama API URL |
| `Scrinia:Embeddings:OllamaModel` | `all-minilm` | Ollama embedding model |

```bash
scri config Scrinia:Embeddings:Provider ollama
scri config Scrinia:Embeddings:OllamaModel nomic-embed-text
```

### OpenAI Provider

Uses the OpenAI embeddings API.

| Key | Default | Description |
|-----|---------|-------------|
| `Scrinia:Embeddings:OpenAiApiKey` | (none) | OpenAI API key (required) |
| `Scrinia:Embeddings:OpenAiModel` | `text-embedding-3-small` | OpenAI embedding model |
| `Scrinia:Embeddings:OpenAiBaseUrl` | `https://api.openai.com/v1` | Base URL (for custom endpoints) |

```bash
scri config Scrinia:Embeddings:Provider openai
scri config Scrinia:Embeddings:OpenAiApiKey sk-...
```

### Voyage AI Provider

Uses the Voyage AI embeddings API. Recommended by Anthropic for use with Claude.

| Key | Default | Description |
|-----|---------|-------------|
| `Scrinia:Embeddings:VoyageAiApiKey` | (none) | Voyage AI API key (required) |
| `Scrinia:Embeddings:VoyageAiModel` | `voyage-3.5` | Voyage AI embedding model |
| `Scrinia:Embeddings:VoyageAiBaseUrl` | `https://api.voyageai.com/v1` | Voyage AI base URL |

```bash
scri config Scrinia:Embeddings:Provider voyageai
scri config Scrinia:Embeddings:VoyageAiApiKey pa-...
```

### Azure AI Foundry Provider

Uses Azure OpenAI embeddings. Supports both classic deployment-scoped and modern v1 URL patterns.

| Key | Default | Description |
|-----|---------|-------------|
| `Scrinia:Embeddings:AzureEndpoint` | (none) | Azure endpoint URL (required) |
| `Scrinia:Embeddings:AzureApiKey` | (none) | Azure API key (required) |
| `Scrinia:Embeddings:AzureDeployment` | `text-embedding-3-small` | Deployment name (classic URL) |
| `Scrinia:Embeddings:AzureModel` | `text-embedding-3-small` | Model name (v1 URL body) |
| `Scrinia:Embeddings:AzureApiVersion` | `2024-10-21` | API version |
| `Scrinia:Embeddings:AzureUseV1` | `false` | Use v1 URL pattern |

**Classic (deployment-scoped):**

```bash
scri config Scrinia:Embeddings:Provider azure
scri config Scrinia:Embeddings:AzureEndpoint https://myresource.openai.azure.com
scri config Scrinia:Embeddings:AzureApiKey ...
scri config Scrinia:Embeddings:AzureDeployment text-embedding-3-small
```

URL: `{endpoint}/openai/deployments/{deployment}/embeddings?api-version={apiVersion}`

**V1 (model in body):**

```bash
scri config Scrinia:Embeddings:Provider azure
scri config Scrinia:Embeddings:AzureEndpoint https://myresource.openai.azure.com
scri config Scrinia:Embeddings:AzureApiKey ...
scri config Scrinia:Embeddings:AzureUseV1 true
scri config Scrinia:Embeddings:AzureModel text-embedding-3-small
```

URL: `{endpoint}/openai/v1/embeddings`

### Google Gemini Provider

Uses the Google Gemini embedContent API.

| Key | Default | Description |
|-----|---------|-------------|
| `Scrinia:Embeddings:GoogleApiKey` | (none) | Google API key (required) |
| `Scrinia:Embeddings:GoogleModel` | `gemini-embedding-001` | Gemini embedding model |
| `Scrinia:Embeddings:GoogleBaseUrl` | `https://generativelanguage.googleapis.com` | Gemini API base URL |
| `Scrinia:Embeddings:GoogleDimensions` | `0` | Output dimensions (0 = model default, 3072) |

```bash
scri config Scrinia:Embeddings:Provider google
scri config Scrinia:Embeddings:GoogleApiKey AIza...
```

### None Provider

Disables semantic search entirely. Only BM25 + weighted-field scoring is used.

```bash
scri config Scrinia:Embeddings:Provider none
```

## MCP Client Configuration

### Claude Code

Add to `.mcp.json` in your project root or `~/.claude/`:

```json
{
  "mcpServers": {
    "scrinia": {
      "command": "scri",
      "args": ["serve", "--workspace-root", "/path/to/your/project"],
      "transport": "stdio"
    }
  }
}
```

`scri serve` resolves its workspace from the nearest enclosing git repository relative to
its working directory; passing `--workspace-root` explicitly makes the server independent
of whatever directory the client launches it from.

### Cursor / Other MCP Clients

The configuration pattern is the same -- point your MCP client at `scri serve` over stdio. Refer to your client's documentation for the exact configuration file location and format.

## Memory Naming Conventions

| Pattern | Scope | Storage Path |
|---------|-------|--------------|
| `subject` | Local | `.scrinia/memories/subject.nmp2` |
| `topic:subject` | Topic | `.scrinia/memories/topic/subject.nmp2` |
| `~subject` | Ephemeral | In-memory only (dies with process). **Not available from the CLI.** |

Names are sanitized for filesystem safety: `..` is stripped, `/` and `\` are replaced with `_`, and invalid filename characters are removed.

Artifact locations are implementation details. `ArtifactEntry.Address` is the logical address and
`ArtifactEntry.ArtifactKey` is an opaque, store-relative key suitable for portable persistence.
Clients must not treat the key as a URI or construct a filesystem location from it; the store owns
artifact resolution.

### Reserved Path Conventions

Memory paths are free-form, but a small set of namespaces have first-class behavior:

| Path | Purpose |
|------|---------|
| `/skill/...` | Reusable specialist prompts (built-in or override). Routed through `recall` (load/list) and `remember` (create/override) to disk under `.scrinia/skills/`. |
| `/agent/...` | AI assistant persona and behavioral instructions (`.scrinia/agent/{name}.md` with sidecar metadata). Store user facts in ordinary memory paths such as `/people/user/travel`. |
| `/patterns/...` | Recurring patterns and conventions. |
| `/sessions/...` | Session logs by date. |
| `/checkpoint/...` | State snapshots. |
| `/temp/...` | Ephemeral — an alias for `~subject`; both spell the same memory. **Not available from the CLI.** |

The `list` and `search` actions support an `excludeTopics` parameter to filter specific topics from the result set.

## Portable Bundles

Bundles (`.scrinia-bundle` files) are ZIP archives containing memories and their index. They're the mechanism for sharing knowledge between workspaces or team members.

**Export topics:**

```bash
scri bundle export api,arch -o project-knowledge
# Creates .scrinia/exports/project-knowledge.scrinia-bundle
```

**Pack raw files:**

```bash
scri bundle pack docs "src/**/*.md" -d "Source documentation"
```

**Import:**

```bash
scri bundle import ./project-knowledge.scrinia-bundle --topics api
```

## Custom Plugin Executable

The CLI discovers plugin executables only through the mandatory
`SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — there is no flat plugin fallback and
nothing is scanned from the executable directory. You can override the embeddings plugin name:

```bash
scri config plugins:embeddings my-custom-embeddings
```

The resolved executable must live at
`SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/<plugin>[.exe]`; discovery never scans the executable
directory or a flat `plugins/<plugin>` path.
