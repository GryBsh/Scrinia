# Scrinia CLI Reference

`scri` is the command-line interface and MCP server for Scrinia. It manages persistent memories and serves as an MCP server exposing two tools (`guide` and `memory`) for AI coding tools.

## Commands

### scri serve

Start the MCP server over stdio transport. This is how MCP clients (Claude Code, Cursor, Copilot) connect to Scrinia.

```bash
scri serve [--workspace-root <path>]
scri serve --remote http://localhost:5000 --api-key <key> [--store default]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--workspace-root` | Auto-detected | Override workspace directory |
| `--remote` | (none) | Connect to a remote Scrinium instead of local storage |
| `--api-key` | (none) | API key for remote server authentication |
| `--store` | `default` | Target store on the remote server |

**Local mode** reads/writes directly to `.scrinia/` on disk. **Remote mode** proxies all MCP tool calls to a Scrinium instance over HTTP.

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

```bash
scri store <name> [file] [--workspace-root <path>]
    [-d description] [-t tag1,tag2] [-k keyword1,keyword2]
    [--review-after 2026-06-01] [--review-when "when auth changes"]
```

**Examples:**

```bash
scri store session-notes ./notes.md
scri store api:auth ./auth.md -k oauth,jwt --review-when "when auth system changes"
cat notes.md | scri store session-notes -
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
newest). The pre-compact version is archived first.

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

Run deterministic consolidation passes over the local store. Designed to be wired
to an editor hook so workspace memory stays tidy without manual intervention. No
LLM call — only mechanical operations (Tier 1).

What it does today:

- Compacts multi-chunk session entries (paths under `/sessions/` or `sessions:`)
  older than `--session-age-days` (default 7) into single-chunk form. Originals
  are archived under `versions/`.
- Reports stats: total memories, total bytes, count of entries past `reviewAfter`.

```bash
scri consolidate [--auto] [--dry-run] [--debounce-minutes 30] [--session-age-days 7] [--json] [--workspace-root <path>]
```

- `--auto` enables debounce: skips the run if `.scrinia/.last-consolidation` shows
  a run within `--debounce-minutes`. Hooks fire on every Stop event, so this
  prevents wasted work.
- `--dry-run` previews actions without writing.
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
review them with `scri list` and decide manually.

### scri link

Create a bidirectional reference between two memories. Both sides get a
`ref:{other}` keyword so searches and graph traversals discover the connection.

```bash
scri link <from> <to> [-r reason] [--workspace-root <path>] [--json]
```

```bash
scri link api:auth-flow patterns:retry -r "auth retries use the documented pattern"
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

### scri export

Export one or more topics to a portable `.scrinia-bundle` file (ZIP format).

```bash
scri export <topics> [--workspace-root <path>] [-o filename]
```

```bash
scri export api,arch -o project-knowledge
```

### scri import

Import topics from a `.scrinia-bundle` file.

```bash
scri import <path> [--workspace-root <path>] [--topics api,arch] [--overwrite]
```

### scri bundle

Bundle raw files from disk into a `.scrinia-bundle` without storing them as memories first. Useful for sharing documentation or code knowledge.

```bash
scri bundle <topic> <files> [--workspace-root <path>] [-o filename] [-d description] [-t tags]
```

```bash
scri bundle docs "src/**/*.md" -d "Source documentation" -t docs,reference
```

### scri setup

Download the Model2Vec embedding model (`m2v-MiniLM-L6-v2`, 384 dimensions) for built-in semantic search.

```bash
scri setup [--workspace-root <path>]
```

Downloads `model.safetensors` (~22MB) and `vocab.txt` from HuggingFace to `{exeDir}/models/m2v-MiniLM-L6-v2/`. Shows progress bars. Skips files that already exist.

No plugin installation required -- Model2Vec is built into Scrinia Core.

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
```

### scri migrate

One-shot utility to migrate a `.scrinia/` store from the v1 (`topic:name`) layout to the v2 (path-based) layout.
Use this only when upgrading a workspace created by an older Scrinia release; new workspaces are already v2.

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
# verify your memories are accessible via scri list / scri show
scri migrate --cleanup            # remove the v1 originals
```

If the migration reports errors, the v1 originals are preserved under `topics/` and the backup directory contains
the pre-migration state.

## JSON Output

All CLI commands support a `--json` flag for machine-readable JSON output. This uses a source-generated `CliJsonContext` for trimming safety.

```bash
scri list --json
scri search "auth" --json
scri show api:auth-flow --json
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
scri export api,arch -o project-knowledge
# Creates .scrinia/exports/project-knowledge.scrinia-bundle
```

**Bundle raw files:**

```bash
scri bundle docs "src/**/*.md" -d "Source documentation"
```

**Import:**

```bash
scri import ./project-knowledge.scrinia-bundle --topics api
```

## Custom Plugin Executable

The CLI discovers plugin executables at `{exeDir}/plugins/scri-plugin-*`. You can override the embeddings plugin name:

```bash
scri config plugins:embeddings my-custom-embeddings
```

The CLI looks for `{exeDir}/plugins/{name}[.exe]`.
