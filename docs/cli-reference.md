# Scrinia CLI Reference

`scri` is the command-line interface and MCP server for Scrinia. It manages persistent memories and serves as an MCP server exposing two tools (`guide` and `memory`) for AI coding tools.

## Surface overview

The top-level surface is organized by purpose:

| Group | Commands |
|---|---|
| Infrastructure | `serve`, `setup`, `config`, `reindex` |
| Lifecycle | `guide`, `restore`, `reconcile`, `consolidate`, `hint` |
| Memory operations | `memory list / search / show / store / forget / append / compact / link` |
| Bundle files | `bundle export / import / pack` |

`scri migrate` is still callable for one-shot v1→v2 store migration but is hidden from `--help` (see [scri migrate](#scri-migrate)).

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

### scri memory list

List stored memories. Defaults to summary mode (topics, keywords, stats). Use `--summary false` for the full table with chunk counts, sizes, token estimates, and review markers.

```bash
scri memory list [--workspace-root <path>] [--scopes local,api,ephemeral]
    [--summary] [--offset 0] [--limit 50] [--json]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--summary` | `true` | Show summary view (topics, keywords, stats) instead of full table |
| `--offset` | `0` | Skip this many entries (for pagination) |
| `--limit` | `50` | Maximum entries to return |
| `--json` | `false` | Output as JSON |

### scri memory search

Search memories using BM25 + weighted-field hybrid scoring. With the embeddings plugin active, semantic vector scores are blended in.

```bash
scri memory search "query" [--workspace-root <path>] [--scopes local,api] [--limit 20]
```

### scri memory store

Compress and persist text as a named memory. Reads from a file path or stdin (`-`).

```bash
scri memory store <name> [file] [--workspace-root <path>]
    [-d description] [-t tag1,tag2] [-k keyword1,keyword2]
    [--review-after 2026-06-01] [--review-when "when auth changes"]
```

**Examples:**

```bash
scri memory store session-notes ./notes.md
scri memory store api:auth ./auth.md -k oauth,jwt --review-when "when auth system changes"
cat notes.md | scri memory store session-notes -
```

### scri memory show

Decode and display a memory's full content. Optionally write to a file.

```bash
scri memory show <name> [--workspace-root <path>] [-o output.md]
```

### scri memory forget

Delete a stored memory and remove its index entry.

```bash
scri memory forget <name> [--workspace-root <path>]
```

### scri guide

Print the embedded agent guide — the same document MCP clients receive when they
call the `guide()` tool. Useful when working from a terminal without an MCP session.

```bash
scri guide [--json]
```

Default output is the raw Markdown. `--json` wraps the response in the standard
MCP envelope (`{ action, status, yaml }`) for tooling consumption.

### scri memory append

Append a new chunk to an existing memory. The previous version is archived to
`{scope}/versions/{name}_{timestamp}.nmp2` so the append is undoable.

```bash
scri memory append <name> [<file>] [--workspace-root <path>] [--json]
```

```bash
# From a file
scri memory append session-notes ./more.md

# From stdin
echo "another paragraph" | scri memory append session-notes
```

If the target memory doesn't exist, `append` falls back to creating it as a
single-chunk memory (equivalent to `memory store`).

### scri memory compact

Merge the chunks of a multi-chunk memory back into a single chunk (or keep the N
newest). The pre-compact version is archived first.

```bash
scri memory compact <name> [--keep-recent N] [--workspace-root <path>] [--json]
```

- Default (`--keep-recent 0`): all chunks are concatenated into one.
- `--keep-recent N` (N ≥ 1): keep only the N most recent chunks; older ones are dropped.

```bash
scri memory compact session-notes               # merge all chunks
scri memory compact session-notes --keep-recent 5  # keep last 5
```

### scri consolidate

Run consolidation passes over the local store. Designed to be wired to an editor
hook so workspace memory stays tidy without manual intervention. Operates in two
tiers: Tier 1 (default) is deterministic and mechanical; Tier 2 (`--with-llm`)
adds an LLM pass for description backfill, session summaries, and fact extraction.

**Tier 1** (always runs):

- Compacts multi-chunk session entries (paths under `/sessions/` or `sessions:`)
  older than `--session-age-days` (default 7) into single-chunk form. Originals
  are archived under `versions/`.
- Reports stats: total memories, total bytes, count of entries past `reviewAfter`.

**Tier 2** (`--with-llm`): after Tier 1 completes, runs an LLM pass over the local
store. Requires an `IBackgroundLlm` to be wired (Ollama / OpenAI-compatible
endpoint, or the bundled `scri-plugin-llm`). Three sub-passes:

1. **Description backfill** — every memory whose description still starts with
   the auto-fallback (first 200 chars of content) gets a regenerated description.
2. **Session summarization** — session entries that were compacted in Tier 1
   this run get a one-paragraph summary written into their description field.
3. **Fact extraction** — every memory gets 3–7 atomic facts extracted into
   `ArtifactEntry.Facts: string[]`. Each fact enters `TermFrequencies` at
   weight +2 so BM25 surfaces them automatically.

Progress is checkpointed to `.scrinia/.tier2-progress.json` keyed by qualified
name + content hash. Re-running `--with-llm` only re-prompts memories whose
content changed since the last pass — kill-and-resume is safe.

If no LLM backend is configured, `--with-llm` exits 2 with a setup hint. Run
`scri setup` to wire one up, or set `Scrinia:Llm:BaseUrl` to a manual endpoint.

```bash
scri consolidate [--auto] [--dry-run] [--with-llm] [--debounce-minutes 30] \
    [--session-age-days 7] [--json] [--workspace-root <path>]
```

- `--auto` enables debounce: skips the run if `.scrinia/.last-consolidation` shows
  a run within `--debounce-minutes`. Hooks fire on every Stop event, so this
  prevents wasted work.
- `--dry-run` previews actions without writing.
- `--with-llm` adds Tier 2; no-op when no backend is available.
- After a non-dry run, `.scrinia/.last-consolidation` is updated with the current
  UTC timestamp.

Wire it to a Claude Code Stop hook (opt-in) by adding to your
`.claude/settings.json`:

```json
{
  "hooks": {
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          { "type": "command", "command": "scri consolidate --auto" }
        ]
      }
    ]
  }
}
```

Stale entries (past their `reviewAfter` date) are reported but NOT deleted —
review them with `scri memory list` and decide manually.

### scri memory link

Create a bidirectional reference between two memories. Both sides get a
`ref:{other}` keyword so searches and graph traversals discover the connection.

```bash
scri memory link <from> <to> [-r reason] [--workspace-root <path>] [--json]
```

```bash
scri memory link api:auth-flow patterns:retry -r "auth retries use the documented pattern"
```

### scri restore

Resume agent context after a fresh session — emits the agent profile, recurring
patterns, today's session log, and the list of available skills. The same call
the MCP `memory('restore')` action makes.

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
scri reconcile --conflict-id local/skills/qa.nmp2 --choice theirs

# Resolve by providing merged content from a file
cat resolved.md | scri reconcile --conflict-id local/notes/api.nmp2 --choice merged --merged-content -
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

Interactive workspace setup. Three things in order:

1. **Ollama auto-detection** (`http://localhost:11434` by default). Probes
   `GET /api/tags`; if reachable, prompts whether to use Ollama for embeddings
   and completions. On `yes`:
   - Picks an embedding model from already-pulled models, or pulls
     `nomic-embed-text` (default), or accepts a typed name.
   - Picks a completion model similarly, defaulting to `lfm2:1.2b` with
     `llama3.2:1b` as fallback if the LFM2 pull fails.
   - Streams `POST /api/pull` with per-layer progress for any missing models.
   - Writes the resulting `Scrinia:Embeddings:*` and `Scrinia:Llm:*` config
     to the workspace.
2. **Built-in embedding model** (`m2v-MiniLM-L6-v2`, 384-dim, ~22MB) downloaded
   from HuggingFace to `{exeDir}/models/m2v-MiniLM-L6-v2/`. Only used when
   Ollama isn't configured and the Vulkan embeddings plugin isn't installed.
3. **LLM model** (`LFM2.5-1.2B-Instruct-Q5_K_M.gguf`, ~900MB) downloaded for the
   bundled `scri-plugin-llm` plugin. Only runs when the plugin exe is installed
   AND no Ollama/HTTP backend is configured. Use `--llm-download` to
   force-download even when not needed; `--no-llm-download` to skip the prompt
   entirely.

```bash
scri setup [--workspace-root <path>] [--no-ollama] [--llm-download] [--no-llm-download] \
    [--multi-user] [--resolver none|claude|copilot] \
    [--hooks] [--uninstall-hooks] [--project]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--no-ollama` | `false` | Skip the Ollama probe + prompt entirely. |
| `--llm-download` | (prompt) | Force-download the bundled LLM GGUF without prompting. |
| `--no-llm-download` | (prompt) | Skip the bundled LLM GGUF download without prompting. |
| `--multi-user` | `false` | Configure git merge drivers for multi-user collaboration. |
| `--resolver` | `none` | Conflict resolver under `--multi-user`: `none`, `claude`, or `copilot`. |
| `--hooks` | `false` | Install SessionStart/Stop/UserPromptSubmit hooks into detected agent CLIs (Claude Code, Codex, GitHub Copilot). Skips the model-download flow. |
| `--uninstall-hooks` | `false` | Remove scrinia-managed hooks from agent CLIs. User-authored hooks are preserved. |
| `--project` | `false` | With `--hooks` / `--uninstall-hooks`, target workspace-local config files (`.claude/`, `.codex/`, `.github/hooks/`) instead of user-global (`~/.claude/` etc.). |

Most users don't need to run `setup` directly — `scri serve` auto-downloads the
embedding model on first launch (use `--no-auto-setup` to opt out). Run `setup`
explicitly when wiring Ollama, configuring multi-user collaboration
(`--multi-user`), installing agent hooks (`--hooks`), or when you want to
control when the network call happens.

#### Hook installer (`--hooks`)

Wires scrinia into the user's existing agent CLI sessions:

- **SessionStart** → `scri restore` — primes context (profile, patterns, today's session log) when the agent starts a session.
- **Stop** → `scri consolidate --auto` — runs Tier 1 housekeeping when the agent stops.
- **UserPromptSubmit** → `scri hint` — emits a one-line "relevant memories exist" marker before the agent processes each user prompt.

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

Plain output format: `[scrinia] N memories match: name1, name2, name3. Run memory('search', 'name1') to retrieve.`

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
scri config Scrinia:Llm:Provider openai
scri config Scrinia:Llm:BaseUrl http://localhost:11434/v1
scri config Scrinia:Llm:Model lfm2:1.2b
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

**Background LLM (Tier 2)**

| Key | Default | Description |
|---|---|---|
| `Scrinia:Llm:Provider` | `auto` | `auto` (HTTP → agent-CLIs → plugin), `openai` (OpenAI-compat HTTP), `anthropic` (native Messages API), `gemini` (native generateContent), `claude-cli`/`codex-cli`/`copilot-cli` (shell out to the agent CLI), `plugin` (force bundled), or `none`. |
| `Scrinia:Llm:BaseUrl` | `http://localhost:11434/v1` | OpenAI-compatible chat-completions endpoint. |
| `Scrinia:Llm:Model` | `lfm2:1.2b` | Model name sent in the chat-completions request body. Matches the Ollama tag for the LFM2.5-Instruct family. |
| `Scrinia:Llm:ApiKey` | (none) | Sent as `Authorization: Bearer …` when set. Optional for Ollama / local servers. |
| `Scrinia:Llm:Temperature` | `0.3` | Sampling temperature. Tier 2 tasks favour low-temperature, reproducible output. |
| `Scrinia:Llm:RequestTimeoutSeconds` | `120` | Outer HTTP ceiling. Per-task budgets are tighter and set via `CancellationToken`. |
| `Scrinia:Llm:LocalModelFile` | (built-in default) | Override the GGUF filename loaded by the bundled `scri-plugin-llm`. |
| `Scrinia:Llm:LocalModelUrl` | (built-in default) | Override the HuggingFace URL the plugin downloads from on first run. |
| `Scrinia:Llm:LocalContextSize` | `8192` | n_ctx passed to LLamaSharp for the bundled plugin's GGUF load. |
| `Scrinia:Llm:AnthropicApiKey` | (none) | API key sent as `x-api-key` to Anthropic. Required when `Provider=anthropic`. |
| `Scrinia:Llm:AnthropicBaseUrl` | `https://api.anthropic.com` | Anthropic Messages API base URL. |
| `Scrinia:Llm:GeminiApiKey` | (none) | API key sent as `x-goog-api-key` to Gemini. Required when `Provider=gemini`. |
| `Scrinia:Llm:GeminiBaseUrl` | `https://generativelanguage.googleapis.com` | Gemini generateContent base URL. |

**Pre-send hint (`scri hint`)**

| Key | Default | Description |
|---|---|---|
| `Scrinia:Hint:Enabled` | `true` | When `false`, `scri hint` is a silent no-op (lets users disable globally without touching every CLI's hook config). |
| `Scrinia:Hint:MinPromptChars` | `8` | Prompts shorter than this skip the lookup entirely (avoids firing on "hi" / "thanks"). |
| `Scrinia:Hint:MinScore` | `10.0` | BM25 score floor — matches below this are suppressed. |

Plugin-specific keys (`plugins:embeddings`, `plugins:llm`) override the executable
name used to launch the corresponding plugin process — only useful for custom
builds.

### scri migrate

One-shot utility to migrate a `.scrinia/` store from the v1 (`topic:name`) layout to the v2 (path-based) layout.
Use this only when upgrading a workspace created by an older Scrinia release; new workspaces are already v2. Hidden from `scri --help` because it's one-time-use, but the command is still callable.

```bash
scri migrate [--workspace <path>] [--dry-run] [--backup=true|false] [--cleanup]
```

| Flag | Default | Description |
|---|---|---|
| `--workspace` | cwd | Workspace root containing the `.scrinia/` directory. |
| `--dry-run` | false | Print the migration plan without copying any files. |
| `--backup` | true | Copy `.scrinia/` to `.scrinia-backup-{timestamp}/` before migrating. |
| `--cleanup` | false | Remove the v1 originals after verifying the v2 copies. Run as a separate step after the initial migration. |

Recommended workflow:

```bash
scri migrate --dry-run            # preview what would be moved
scri migrate                      # run the migration (backup created automatically)
# verify your memories are accessible via scri memory list / scri memory show
scri migrate --cleanup            # remove the v1 originals
```

If the migration reports errors, the v1 originals are preserved under `topics/` and the backup directory contains
the pre-migration state.

## JSON Output

All CLI commands support a `--json` flag for machine-readable JSON output. This uses a source-generated `CliJsonContext` for trimming safety.

```bash
scri memory list --json
scri memory search "auth" --json
scri memory show api:auth-flow --json
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

When `--workspace-root` is not specified, `scri` walks up the directory tree from the current working directory looking for a `.scrinia/` directory (like git finds `.git/`). If none is found, the current directory becomes the workspace root and `.scrinia/` is created on first write.

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
      "args": ["serve"],
      "transport": "stdio"
    }
  }
}
```

### With Custom Workspace Root

```json
{
  "mcpServers": {
    "scrinia": {
      "command": "scri",
      "args": ["serve", "--workspace-root", "/path/to/workspace"],
      "transport": "stdio"
    }
  }
}
```

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
| `~subject` | Ephemeral | In-memory only (dies with process) |

Names are sanitized for filesystem safety: `..` is stripped, `/` and `\` are replaced with `_`, and invalid filename characters are removed.

### Reserved Path Conventions

Memory paths are free-form, but a small set of namespaces have first-class behavior in the `memory` tool:

| Path | Purpose |
|------|---------|
| `/skill/...` | Reusable specialist prompts (built-in or override). Routed through `memory()` to load/store on disk under `.scrinia/skills/`. |
| `/agent/...` | Agent profile and behavioral norms (`.scrinia/agent/{name}.md` with sidecar metadata). |
| `/patterns/...` | Recurring patterns and conventions. |
| `/sessions/...` | Session logs by date. |
| `/checkpoint/...` | State snapshots. |
| `/temp/...` | Ephemeral (dies on process exit). |

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
