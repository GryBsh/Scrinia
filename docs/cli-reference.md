# Scrinia CLI Reference

`scri` is the command-line interface and MCP server for Scrinia. It manages persistent memories and serves as an MCP server exposing one tool per operation — `guide`, the per-verb memory tools (`remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`), and `facts` — for AI coding tools.

## Surface overview

The top-level surface is organized by purpose:

| Group | Commands |
|---|---|
| Infrastructure | `serve`, `setup`, `config`, `reindex` |
| Lifecycle | `guide`, `restore`, `reconcile`, `consolidate`, `hint`, `migrate` |
| Memory operations | `list / search / show / store / forget / append / compact / link` |
| Bundle files | `bundle export / import / pack` |

`scri migrate` (store-layout migration, see [scri migrate](#scri-migrate)) also runs
automatically on first store access — the command exists for previewing and deliberate
timing, not as a required step. `scri migrate_attributes` is a separate, unrelated command
that forward-fills older entries onto the attributes model.

## Commands

### scri serve

Start the MCP server over stdio transport. This is how MCP clients (Claude Code, Cursor, Copilot) connect to Scrinia.

```bash
scri serve [--workspace-root <path>] [--no-auto-setup]
scri serve --remote http://localhost:5000 --api-key <key> [--store default]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--workspace-root` | Auto-detected | Override workspace directory |
| `--remote` | (none) | Connect to a remote Scrinium instead of local storage |
| `--api-key` | (none) | API key for remote server authentication |
| `--store` | `default` | Target store on the remote server |
| `--no-auto-setup` | `false` | Skip the first-run embedding model download |

**Local mode** reads/writes directly to `.scrinia/` on disk. On first launch, `serve` checks for the built-in embedding model (`m2v-MiniLM-L6-v2`) and downloads it if missing (~50MB, one-time). Download progress goes to stderr to keep the JSON-RPC stdout clean. If the download fails, the server still starts — semantic search degrades to BM25-only until you run `scri setup` manually.

**Remote mode** proxies all MCP tool calls to a Scrinium instance over HTTP — no local embedding model needed.

### scri list

List stored memories. Defaults to summary mode (topics, keywords, stats). Use `--summary false` for the full table with chunk counts, sizes, token estimates, and review markers.

```bash
scri list [--workspace-root <path>] [--scopes local,api,ephemeral]
    [--summary] [--offset 0] [--limit 50] [--json]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--summary` | `true` | Show summary view (topics, keywords, stats) instead of full table |
| `--offset` | `0` | Skip this many entries (for pagination) |
| `--limit` | `50` | Maximum entries to return |
| `--json` | `false` | Output as JSON |

### scri search

Search memories using BM25 + weighted-field hybrid scoring. With the embeddings plugin active, semantic vector scores are blended in.

```bash
scri search "query" [--workspace-root <path>] [--scopes local,api] [--limit 20]
```

### scri store

Compress and persist text as a named memory. Reads from a file path or stdin (`-`).

> **Ephemeral memories are not available from the CLI.** `~name` and its `/temp/...` alias live
> only in the process that creates them, and the CLI exits immediately — so a CLI ephemeral write
> would be discarded. All memory commands refuse an ephemeral address with exit 1. Use an MCP
> client for ephemerals, or a persistent path.

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

Append a new chunk to an existing memory. The previous version is archived to
`{scope}/versions/{name}_{timestamp}.nmp2` so the append is undoable.

```bash
scri append <name> [<file>] [--workspace-root <path>] [--json]
```

```bash
# From a file
scri append session-notes ./more.md

# From stdin
echo "another paragraph" | scri append session-notes
```

If the target memory doesn't exist, `append` falls back to creating it as a
single-chunk memory (equivalent to `store`).

### scri compact

Merge the chunks of a multi-chunk memory back into a single chunk (or keep the N
newest). The pre-compact version is archived first, except for an ephemeral (`~name`)
memory or one with no prior index entry — neither has a previous version to archive.

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

Mechanical housekeeping over the local store. Compacts multi-chunk session
entries and reports stale entries. There is no LLM pass — scrinia ships zero
LLM-at-rest enrichment; anything you want an LLM to do to your memories happens
in the agent that writes them.

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

`scri consolidate` is now a **manual command** — `scri setup --hooks` no longer
installs a SessionEnd hook for it. Run it by hand when you want session
compaction, or wire it into a scheduled task. The previous SessionEnd-hook
auto-install was paired with an LLM-enrichment sweep at session close; that
pipeline is gone, so there's no reason to fire this command per-session anymore.

### scri link

Create a bidirectional reference between two memories. Both sides get a `ref`
attribute naming the other, so searches and graph traversals discover the
connection. Both memories must already exist — if either is missing, neither is
written.

Ephemeral `~name` memories cannot be linked from the CLI. An ephemeral memory
lives only in the memory of the process that created it, and every `scri`
invocation is a fresh process, so a `~name` is never present when the command
starts — `scri link ~scratch /patterns/x` can only answer "not found". The MCP
`link` tool does support ephemerals, because the server (`scri serve`) is a
long-lived process that still holds the one that created them.

```bash
scri link <from> <to> [-r reason] [--workspace-root <path>] [--json]
```

```bash
scri link /api/auth-flow /patterns/retry -r "auth retries use the documented pattern"
```

### scri restore

Resume agent context after a fresh session — emits the agent profile, recurring
patterns, today's session log, and the list of available skills. The same call
the MCP `restore` call makes.

```bash
scri restore [--workspace-root <path>] [--json]
```

### scri reconcile

Scan `.scrinia/` for unresolved git merge conflicts, or resolve a specific
conflict by its workspace-relative path. Run with no arguments to get the
conflict list, then resolve each path independently — the resolve step re-reads
the file fresh, so it doesn't depend on any state from the scan.

```bash
scri reconcile [--conflict-id <relative-path>] [--choice ours|theirs|merged] \
               [--merged-content <content-or-->] [--workspace-root <path>] [--json]
```

```bash
# Scan
scri reconcile

# Resolve a conflict by taking the incoming side
scri reconcile --conflict-id memories/local/auth-flow.nmp2 --choice theirs

# Resolve by providing merged content from a file
cat resolved.md | scri reconcile --conflict-id memories/local/api-design.nmp2 --choice merged --merged-content -
```

### scri bundle export

Export one or more topics to a portable `.scrinia-bundle` file (ZIP format).

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

### scri bundle pack

Pack raw files from disk into a `.scrinia-bundle` without storing them as memories first. Useful for sharing documentation or code knowledge.

```bash
scri bundle pack <topic> <files> [--workspace-root <path>] [-o filename] [-d description] [-t tags]
```

```bash
scri bundle pack docs "src/**/*.md" -d "Source documentation" -t docs,reference
```

### scri setup

Interactive workspace setup. Two things in order:

1. **Ollama auto-detection for embeddings** (`http://localhost:11434` by default).
   Probes `GET /api/tags`; if reachable, prompts whether to use Ollama for
   embeddings. On `yes`:
   - Picks an embedding model from already-pulled models, or pulls
     `nomic-embed-text` (default), or accepts a typed name.
   - Streams `POST /api/pull` with per-layer progress for any missing model.
   - Writes the resulting `Scrinia:Embeddings:*` config to the workspace.
2. **Built-in embedding model** (`m2v-MiniLM-L6-v2`, 384-dim, ~22MB) downloaded
   from HuggingFace to `{exeDir}/models/m2v-MiniLM-L6-v2/`. Only used when
   Ollama isn't configured and the Vulkan embeddings plugin isn't installed.

Setup does **not** prompt for any LLM configuration. Scrinia ships zero
LLM-at-rest enrichment — there is no background LLM the server consults at
write or read time, so there is nothing for setup to wire up. Agents that
want to enrich memories do so client-side and pass `attributes` (including
`anchor`) and `keywords` through the MCP `remember` tool.

```bash
scri setup [--workspace-root <path>] [--no-ollama] \
    [--multi-user] [--resolver none|claude|copilot] \
    [--hooks] [--uninstall-hooks] [--project]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--no-ollama` | `false` | Skip the Ollama probe + prompt entirely. |
| `--multi-user` | `false` | Configure git merge drivers for multi-user collaboration. |
| `--resolver` | `none` | Conflict resolver under `--multi-user`: `none`, `claude`, or `copilot`. Strictly merge-driver semantics. |
| `--hooks` | `false` | Install SessionStart + UserPromptSubmit hooks into detected agent CLIs (Claude Code, Codex, GitHub Copilot). Skips the model-download flow. |
| `--uninstall-hooks` | `false` | Remove scrinia-managed hooks from agent CLIs. User-authored hooks are preserved. |
| `--project` | `false` | With `--hooks` / `--uninstall-hooks`, target workspace-local config files (`.claude/`, `.codex/`, `.github/hooks/`) instead of user-global (`~/.claude/` etc.). |

When the Ollama probe falls through (declined, unreachable, or `--no-ollama`),
setup clears any prior Ollama-derived embedding config keys
(`Scrinia:Embeddings:Provider=ollama`, the Ollama URL + model). Custom OpenAI /
Voyage / Azure / Google embedding configurations are preserved.

Most users don't need to run `setup` directly — `scri serve` auto-downloads the
embedding model on first launch (use `--no-auto-setup` to opt out). Run `setup`
explicitly when wiring Ollama embeddings, configuring multi-user collaboration
(`--multi-user`), installing agent hooks (`--hooks`), or when you want to
control when the network call happens.

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

Pre-send relevance hint. Emits a single-line marker telling the agent which stored memories look relevant to a prompt. No retrieval, no LLM call — just a sub-millisecond BM25 lookup.

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

`--json` returns `{"count": N, "matches": [{"scope", "name", "score"}, ...]}`.

Normally invoked by the UserPromptSubmit hook installed via `scri setup --hooks`; can also be run by hand to debug threshold tuning.

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

Moves every `vectors.bin` to a timestamped `vectors.bin.pre-reindex-{stamp}`
backup, then runs `WorkspaceSetup.LoadPluginsAsync` which sees the missing files
and rebuilds from sidecars. The backup files are kept on disk — delete them
manually once you've confirmed the new vectors work.

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
| `Scrinia:Embeddings:ChunkOverlap` | `200` | Overlap in characters between adjacent windows. Must be strictly less than `ChunkSize`. |
| `Scrinia:Embeddings:MaxChunksPerMemory` | `100` | Safety cap on chunks per memory; excess tail is dropped from embed (BM25 still indexes full text). |
| `Scrinia:Embeddings:OllamaBaseUrl` | `http://localhost:11434` | Ollama API endpoint. |
| `Scrinia:Embeddings:OllamaModel` | `all-minilm` | Ollama embedding model name. |
| `Scrinia:Embeddings:OpenAi*` | — | `ApiKey`, `Model`, `BaseUrl` for OpenAI / compatible. |
| `Scrinia:Embeddings:VoyageAi*` | — | `ApiKey`, `Model`, `BaseUrl` for Voyage AI. |
| `Scrinia:Embeddings:Azure*` | — | `Endpoint`, `ApiKey`, `Deployment`, `Model`, `ApiVersion`, `UseV1` for Azure AI Foundry. |
| `Scrinia:Embeddings:Google*` | — | `ApiKey`, `Model`, `BaseUrl`, `Dimensions` for Google Gemini. |

**Pre-send hint (`scri hint`)**

| Key | Default | Description |
|---|---|---|
| `Scrinia:Hint:Enabled` | `true` | When `false`, `scri hint` is a silent no-op (lets users disable globally without touching every CLI's hook config). |
| `Scrinia:Hint:MinPromptChars` | `8` | Prompts shorter than this skip the lookup entirely (avoids firing on "hi" / "thanks"). |
| `Scrinia:Hint:MinScore` | `10.0` | BM25 score floor — matches below this are suppressed. |

The `plugins:embeddings` key overrides the executable name used to launch the
embeddings plugin process — only useful for custom builds. Scrinia ships no
server-side LLM plugin; the historical `plugins:llm` key has been removed.

### scri migrate

Migrates a workspace store to the v3 layout, where every memory lives under
`.scrinia/memories/`. Migration also runs **automatically** on first store access, so this
command is only needed to preview the plan, migrate deliberately before a large operation,
or re-run after restoring a backup.

```bash
scri migrate [--workspace <path>] [--dry-run] [--backup=true|false]
```

| Flag | Default | Description |
|---|---|---|
| `--workspace` | cwd | Workspace root containing the `.scrinia/` directory. |
| `--dry-run` | false | Print how many files would move without moving any. |
| `--backup` | true | Copy `.scrinia/` to `.scrinia-backup-{timestamp}/` before migrating. |

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
  store/              Local memories (.nmp2 files + index.json)
  topics/
    api/              Topic "api" memories
    arch/             Topic "arch" memories
  embeddings/         Vector data (per-workspace, created by embeddings plugin)
  exports/            Exported .scrinia-bundle files
  config.json         Workspace configuration
```

### Workspace Discovery

When `--workspace-root` is not specified, `scri` walks up from the current working directory looking for the nearest enclosing git repository, identified by a `.git` entry (a directory in a normal checkout, or a file pointing at `gitdir:` in a worktree or submodule). That repository's root becomes the workspace root. If no git repository encloses the working directory, the working directory itself is the workspace root and no upward search happens.

`.scrinia/` is created lazily -- only when something is written. Reading commands (`hint`, `recall`, `search`, `show`, `list`) against a workspace with no store create nothing.

If a `.scrinia/` directory exists somewhere resolution deliberately did not use (for example, in an ancestor directory outside the resolved root), writes, `setup`, and `serve` startup print a warning to stderr naming the ignored store, the root in use, and a `scri bundle export` / `scri bundle import` pair to migrate its memories into the resolved workspace.

## Configuration

Settings are resolved in priority order:

1. **Environment variable** (highest) -- key with `:` replaced by `_`, uppercased (e.g., `SCRINIA_EMBEDDINGS_PROVIDER`)
2. **Config file** -- `.scrinia/config.json` in the workspace root
3. **Default value** (lowest)

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

Downloads `model.safetensors` (~22MB) and `vocab.txt` to `{exeDir}/models/m2v-MiniLM-L6-v2/`.

### Vulkan Provider (Optional Plugin)

GPU-accelerated embeddings via LLamaSharp with Vulkan backend. Requires the plugin to be installed:

```bash
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithVulkan
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

### Remote Mode (Connecting to Scrinium)

```json
{
  "mcpServers": {
    "scrinia": {
      "command": "scri",
      "args": ["serve", "--remote", "http://localhost:5000", "--api-key", "YOUR_KEY"],
      "transport": "stdio"
    }
  }
}
```

### Cursor / Other MCP Clients

The configuration pattern is the same -- point your MCP client at `scri serve` over stdio. Refer to your client's documentation for the exact configuration file location and format.

## Memory Naming Conventions

| Pattern | Scope | Storage Path |
|---------|-------|--------------|
| `subject` | Local | `.scrinia/store/subject.nmp2` |
| `topic:subject` | Topic | `.scrinia/topics/topic/subject.nmp2` |
| `~subject` | Ephemeral | In-memory only (dies with process). **Not available from the CLI.** |

Names are sanitized for filesystem safety: `..` is stripped, `/` and `\` are replaced with `_`, and invalid filename characters are removed.

### Reserved Path Conventions

Memory paths are free-form, but a small set of namespaces have first-class behavior:

| Path | Purpose |
|------|---------|
| `/skill/...` | Reusable specialist prompts (built-in or override). Routed through `recall` (load/list) and `remember` (create/override) to disk under `.scrinia/skills/`. |
| `/agent/...` | Agent profile and behavioral norms (`.scrinia/agent/{name}.md` with sidecar metadata). |
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

The CLI discovers plugin executables at `{exeDir}/plugins/scri-plugin-*`. You can override the embeddings plugin name:

```bash
scri config plugins:embeddings my-custom-embeddings
```

The CLI looks for `{exeDir}/plugins/{name}[.exe]`.
