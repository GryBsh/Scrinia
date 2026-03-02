# Scrinia CLI Reference

> `scri` — the command-line interface and MCP server for scrinia.

## Commands

### `scri serve`

Start the MCP server over stdio transport. This is how MCP clients (Claude Code, Cursor, Copilot) interact with scrinia.

```bash
scri serve [--workspace-root <path>]
scri serve --remote http://localhost:5000 --api-key <key> [--store default]
```

**Options:**
- `--workspace-root` — Override workspace directory (default: walks up from cwd looking for `.scrinia/`)
- `--remote` — Connect to a remote Scrinia.Server instead of local storage
- `--api-key` — API key for remote server authentication
- `--store` — Target store on the remote server (default: `default`)

### `scri list`

List all stored memories with chunk counts, sizes, token estimates, and review markers.

```bash
scri list [--scopes local,api,ephemeral]
```

### `scri search`

Search memories using BM25 + weighted field + semantic hybrid scoring.

```bash
scri search "query" [--scopes local,api] [--limit 20]
```

### `scri store`

Store a file as a named memory. Reads from a file path or stdin.

```bash
scri store <name> [file] [-d description] [-t tag1,tag2] [-k keyword1,keyword2]
    [--review-after 2026-06-01] [--review-when "when auth changes"]
```

**Examples:**
```bash
scri store session-notes ./notes.md
scri store api:auth ./auth.md -k oauth,jwt --review-when "when auth system changes"
cat notes.md | scri store session-notes -
```

### `scri show`

Display memory content. Optionally write to a file.

```bash
scri show <name> [-o output.md]
```

### `scri forget`

Delete a stored memory.

```bash
scri forget <name>
```

### `scri export`

Export one or more topics to a portable `.scrinia-bundle` file.

```bash
scri export <topics> [-o filename]
```

**Example:**
```bash
scri export api,arch -o project-knowledge
```

### `scri import`

Import topics from a `.scrinia-bundle` file.

```bash
scri import <path> [--topics api,arch] [--overwrite]
```

### `scri bundle`

Bundle raw files into a `.scrinia-bundle` without storing them as memories first.

```bash
scri bundle <topic> <files> [-o filename] [-d description] [-t tags]
```

**Example:**
```bash
scri bundle docs "src/**/*.md" -d "Source documentation" -t docs,reference
```

### `scri setup`

Download the ONNX embedding model for the embeddings plugin.

```bash
scri setup [--workspace-root <path>]
```

Downloads `model.onnx` and `vocab.txt` from HuggingFace (`sentence-transformers/all-MiniLM-L6-v2`) to `{exeDir}/plugins/{pluginName}/models/all-MiniLM-L6-v2/`. Shows progress bars with transfer speed. Skips files that already exist.

Requires the embeddings plugin executable to be present at `{exeDir}/plugins/scri-plugin-embeddings[.exe]` (installed via `publish.ps1 -WithEmbeddings`).

### `scri config`

Get, set, list, or remove workspace configuration settings. Settings are stored in `.scrinia/config.json`.

```bash
scri config                              # list all settings
scri config <key>                        # get a setting
scri config <key> <value>                # set a setting
scri config --unset <key>                # remove a setting
```

**Example:**
```bash
scri config plugins:embeddings my-custom-plugin
scri config Scrinia:Embeddings:Provider ollama
scri config Scrinia:Embeddings:OllamaModel nomic-embed-text
```

## Configuration

Settings are resolved in priority order:

1. **Environment variable** (highest) — key with `:` replaced by `_`, uppercased (e.g. `SCRINIA_EMBEDDINGS_PROVIDER`)
2. **Config file** — `.scrinia/config.json` in the workspace root
3. **Default value** (lowest)

### Configuration keys

| Key | Default | Description |
|---|---|---|
| `plugins:embeddings` | `scri-plugin-embeddings` | Embeddings plugin executable name |
| `Scrinia:Embeddings:Provider` | `onnx` | Embedding provider: `onnx`, `ollama`, `openai`, `none` |
| `Scrinia:Embeddings:Hardware` | `auto` | ONNX hardware: `auto`, `directml`, `cuda`, `cpu` |
| `Scrinia:Embeddings:SemanticWeight` | `50.0` | Semantic score weight in hybrid search |
| `Scrinia:Embeddings:OllamaBaseUrl` | `http://localhost:11434` | Ollama API URL |
| `Scrinia:Embeddings:OllamaModel` | `nomic-embed-text` | Ollama embedding model |
| `Scrinia:Embeddings:OpenAiApiKey` | — | OpenAI API key |
| `Scrinia:Embeddings:OpenAiModel` | `text-embedding-3-small` | OpenAI embedding model |
| `Scrinia:Embeddings:OpenAiBaseUrl` | — | Custom OpenAI-compatible base URL |

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

### Workspace discovery

When `--workspace-root` is not specified, `scri` walks up the directory tree from the current working directory looking for a `.scrinia/` directory (like git finds `.git/`). If none is found, the current directory becomes the workspace root.

## Memory naming

| Pattern | Scope | Storage path |
|---|---|---|
| `subject` | Local | `.scrinia/store/subject.nmp2` |
| `topic:subject` | Topic | `.scrinia/topics/topic/subject.nmp2` |
| `~subject` | Ephemeral | In-memory only (dies with process) |

## MCP client configuration

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

### With custom workspace root

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

### Remote mode (connecting to Scrinia.Server)

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
