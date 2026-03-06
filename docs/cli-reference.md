# Scrinia CLI Reference

`scri` is the command-line interface and MCP server for Scrinia. It provides 11 commands for managing persistent memories and serves as an MCP server for AI coding tools.

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

List all stored memories with chunk counts, sizes, token estimates, and review markers.

```bash
scri list [--workspace-root <path>] [--scopes local,api,ephemeral]
```

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
