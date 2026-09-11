# scrinia

[![License: BSD-3-Clause](https://img.shields.io/badge/License-BSD--3--Clause-blue.svg)](LICENSE)

Persistent, portable memory for AI agents. Scrinia is a local-first MCP server that stores what agents save and serves it back — **agents bring the intelligence; scrinia provides the substrate**.

Memories are compressed into NMP/2 artifacts on disk and indexed deterministically: BM25 over content + agent-supplied keywords + the `anchor` attribute (query-vocabulary phrases the agent supplies at write time), weighted field scoring over attributes, semantic vectors via Model2Vec (384-dim MiniLM-L6-v2 distillation, ~22MB, zero native deps), re-ranked by cosine similarity over the BM25 + attribute candidate set. **There is no LLM consultation at write or read time** — write goes to disk, read runs the ranker, and that's the loop. The calling agent is the source of any LLM-driven enrichment (supplying the `anchor` attribute, query refinement, summarization); scrinia just stores and surfaces.

Cross-process safe via OS-enforced file locks. Zero infrastructure required.

## Benchmarks

How does a structured memory system compare to simpler approaches? We built a [runnable benchmark suite](tests/Scrinia.Tests/Benchmarks/) that quantitatively compares three strategies:

- **Scrinia** — NMP/2 compressed artifacts, BM25+weighted field search, chunked retrieval
- **Flat-file** — all knowledge in one string (AGENTS.md-style), always fully loaded, substring search
- **Auto memory** — 200-line index always loaded, per-topic files loaded on demand (Claude-style)

### Token efficiency (avg tokens per query)

| Corpus size | Scrinia | Flat-file | Auto memory | Scrinia savings |
|---|---|---|---|---|
| 10 facts | 162 | 557 | 426 | 71% fewer tokens |
| 50 facts | 281 | 2,735 | 989 | 90% fewer tokens |
| 100 facts | 278 | 5,464 | 1,534 | 95% fewer tokens |
| 500 facts | 274 | 27,324 | 5,905 | 99% fewer tokens |

### Scaling (growth rate from 10 to 500 facts)

| System | Growth factor | Pattern |
|---|---|---|
| Scrinia | 1.7x | Near-constant |
| Auto memory | 13.8x | Sublinear |
| Flat-file | 49.1x | Linear |

### Cold start (tokens consumed before first query)

| System | 10 facts | 100 facts | 500 facts |
|---|---|---|---|
| Scrinia | 0 | 0 | 0 |
| Auto memory | 135 | 440 | 1,780 |
| Flat-file | 557 | 5,464 | 27,324 |

### Search recall

All three systems achieve 100% recall on exact-term and natural-language queries. Scrinia's advantage is not accuracy — it's doing it at 1-5% of the token cost.

### Cross-topic isolation

| System | Isolation ratio | Meaning |
|---|---|---|
| Scrinia | 100% | Only loads matching memories |
| Auto memory | 80% | Loads index + routed topic |
| Flat-file | 20% | Always loads all 5 topics |

### First query cost (cold start + query, 100 facts)

| System | Cold start | Query | Total |
|---|---|---|---|
| Scrinia | 0 | 282 | **282** |
| Auto memory | 440 | 1,564 | 2,004 |
| Flat-file | 5,464 | 5,464 | 10,928 |

### Where each system wins

| Dimension | Winner | Why |
|---|---|---|
| Very small corpus (<20 facts) | Flat-file | Negligible overhead, everything fits |
| Token efficiency at scale | Scrinia | Selective retrieval, zero cold start |
| Recall on exact terms | Tie | All systems find substring matches |
| Ranked precision | Scrinia | BM25 + weighted fields produce ranked results |
| Cross-topic isolation | Scrinia | Only loads matching memories |
| Setup simplicity | Flat-file | Just a string, no tools needed |
| Staleness management | Scrinia | Only system with review markers |

Run the benchmarks yourself:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~Benchmarks"
```

### Retrieval quality (academic benchmarks)

Separate from the token-cost comparison above, Scrinia ships a retrieval-quality harness (`scrinia-bench`) that runs against the standard academic memory-retrieval benchmarks the field publishes against: **LongMemEval** (500 questions), **LoCoMo** (1986), **ConvoMem** (250), **MemBench** (8500). Reports R@5 / R@10 / R@50 / NDCG@10 with per-category breakdowns. JSON output schema matches MemPalace's per-question result files so per-question hits can be diffed across systems.

Two modes: pure BM25 (apples-to-apples vs MemPalace's "raw" track) and BM25 + MiniLM embeddings via model2vec. Ablation knobs (`--alpha-recency`, `--tau-days`, `--diversity-lambda`) let us isolate each Scrinia ranker feature's contribution.

```powershell
# Windows / Mac / Linux with pwsh
./scripts/prepare-datasets.ps1
./scripts/run-benchmarks.ps1
```

```bash
# Bash
./scripts/prepare-datasets.sh
./scripts/run-benchmarks.sh
```

Full instructions, ablations, and per-question diff recipes: [`benchmarks/Scrinia.Retrieval.Benchmarks/README.md`](benchmarks/Scrinia.Retrieval.Benchmarks/README.md).

## Install

Install the immutable `scri` global tool from NuGet:

```sh
dotnet tool install --global scrinia
```

This installs the `scri` command onto your global tool path. Verify with `scri --version`.

Build from source (.NET 10 SDK required) instead:

```bash
git clone https://github.com/nickd-scrinia/scrinia
cd scrinia
dotnet build

# Publish trimmed single-file binary
.\publish.ps1 -OutputDir ./dist -Platform win-x64

# Download embedding model for semantic search (~22MB)
scri setup

# Optional: with Vulkan GPU-accelerated embeddings plugin
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithEmbeddings
```

## Where data lives

All writable state goes below `SCRINIA_HOME` (the per-user home resolved deterministically; unset
defaults to `<LocalApplicationData>\Scrinia`). The installed global-tool package is immutable and is
never written at runtime.

```
SCRINIA_HOME/
  config.json                 # per-user registry (shared bases, tracking)
  models/                     # Model2Vec and plugin-downloaded models
  plugins/
    <plugin>/
      <version>/
        <rid>/                # separately installed plugin executable, managed assemblies, native libraries
        state/                # mutable plugin runtime state
```

Models resolve to `SCRINIA_HOME/models`; separately installed plugins live only at the mandatory
`SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — there is no flat plugin fallback. The
optional Vulkan plugin is installed separately (see [Installation](docs/installation.md)).

## MCP setup

Add to your MCP client config (Claude Code, Cursor, Copilot, etc.):

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

To share memories across a team, commit `.scrinia/` like any other source file and, when the same
memories should be shared between repositories, register a shared base with `scri shared base add`
— shared bases always use the `project-git` tracking policy. See
[Sharing Memory Across a Team](docs/multi-user-setup.md).

## CLI quick reference

> **Note**: All mcp tools are availabl via cli, so they can be used in hooks, etc.

```bash
# Infrastructure
scri serve                                  # start MCP server (stdio); auto-downloads embedding model on first run
scri setup                                  # pre-download embedding model (rarely needed — serve auto-runs it)
scri config                                 # list/get/set workspace settings

# Cold-start / lifecycle
scri guide                                  # print the agent guide (run once per session)
scri restore                                # resume agent context (profile, patterns, session log)
scri consolidate                            # mechanical session compaction + stale-entry report (run manually as needed)
scri reindex                                # force rebuild of every vector file (rarely needed; auto-runs on model switch)

# Memory operations
scri list                                   # summary view (topics, keywords, stats)
scri list --summary=false                   # full listing
scri search "auth"                          # hybrid BM25 + semantic search
scri store notes ./notes.md                 # store a file as memory
scri store /api/auth ./auth.md              # store under a topic
scri show /api/auth                         # display memory content
scri forget /api/auth                       # delete a memory
scri append notes ./more.md                 # add a chunk
scri compact notes --keep-recent 3          # merge old chunks; keep 3 newest
scri link notes /api/auth -r "see also"     # bidirectional cross-reference

# Bundle files (grouped under "bundle")
scri bundle export api                      # export topic to .scrinia-bundle
scri bundle import ./bundle.scrinia-bundle  # import a bundle
scri bundle pack docs *.md                  # package raw files into a bundle
```

All commands accept `--workspace-root` to override the workspace directory and `--json` for machine-parseable JSON output. The one-shot `scri migrate` (v1→v2 store layout) is still callable but hidden from `--help`.

## Memory naming

| Pattern | Scope | Example |
|---|---|---|
| `subject` | Local store | `scri store session-notes file.md` |
| `/topic/subject` | Topic group | `scri store /api/auth file.md` |
| `~subject` | Ephemeral (in-memory) | Dies with process |

## MCP tools

12 tools available via `scri serve`.

| Tool | Description |
|---|---|
| `guide` | Returns the embedded agent guide and auto-restores workspace context (skills, recent sessions, patterns). Call once per session. |
| `remember` | Persist a memory at a path. `content` is a string array — each element is one chunk. |
| `recall` | Read a memory by path. Routes `/skill/...` paths to skill loading. |
| `search` | Find memories by keyword or phrase. |
| `list` | List stored memories (`summary` / `full` / `drift` modes). |
| `append` | Append a chunk to an existing memory (or create it). |
| `forget` | Delete a memory by path. |
| `compact` | Merge a multi-chunk memory; archives the original (except an ephemeral, or one with no prior index entry). |
| `link` | Bidirectional link between two memories. |
| `restore` | Resume workspace context mid-session (usually folded into `guide()` at session start). |
| `facts` | Aggregate/relational entity queries (`entities`, `profile`, `intersect`). |

### Built-in skills

Seven skills ship with scrinia and load via `recall({ path: '/skill/{name}' })`:

| Skill | Purpose |
|-------|---------|
| `auditor` | Systematic code, security, and documentation review with sequenced finding IDs |
| `qa` | Test-and-build verification with command-output evidence |
| `debugger` | Scientific-method debugging: observe, hypothesize, isolate, verify |
| `chaos-engineer` | Probe operational resilience: failure domains, blast radius, recovery gaps |
| `onboarder` | Build a codebase mental model for new agents and developers |
| `evolutionary` | Prune stale memories, surface drift, keep skills aligned with practice |
| `self-reflector` | Compare plan vs reality after a unit of work, persist durable lessons |

Projects can override any built-in by writing to `/skill/{name}` — the on-disk version takes precedence and is reusable across sessions.

Plans, retrospectives, agent norms, and findings are all just memories — searchable via `search`, organized via reserved paths (`/findings/`, `/learn/`, `/agent/`, `/patterns/`, `/sessions/`). No separate database, no separate tools.

## Documentation

### User Guides
- **[Getting Started](docs/getting-started.md)** — overview, installation, quick start
- **[CLI Reference](docs/cli-reference.md)** — commands, configuration, embedding providers, MCP client setup
- **[Sharing Memory Across a Team](docs/multi-user-setup.md)** — sharing a workspace through git, shared bases
- **[Troubleshooting](docs/troubleshooting.md)** — common issues, plugin failures, recovery

### Architecture
- **[Overview](docs/architecture/overview.md)** — system design, project structure, dependency graph
- **[CLI Architecture](docs/architecture/cli.md)** — workspace discovery, plugin host, MCP tools
- **[Core Internals](docs/architecture/core.md)** — IMemoryStore, NMP/2 encoding, search algorithms
- **[Embeddings Architecture](docs/architecture/embeddings.md)** — providers, vector store, hybrid scoring

### Specification
- **[NMP/2 Spec](NMP_SPEC.md)** — encoding format specification

## Running tests

```bash
dotnet test Scrinia.sln                                 # everything
dotnet test tests/Scrinia.Tests                         # CLI + MCP + storage + embeddings + reindex
dotnet test tests/Scrinia.Plugin.Embeddings.Tests       # Vulkan embeddings plugin
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests    # retrieval harness and judge
```

## Running benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) is used for measuring hot-path performance
(BM25 corpus stats). Run from the repo root:

```bash
# Run everything (a full pass takes several minutes)
dotnet run -c Release --project tests/Scrinia.Benchmarks

# Run a subset and emit a machine-readable JSON summary
dotnet run -c Release --project tests/Scrinia.Benchmarks -- \
    --filter "*Bm25*" --exporters json
```

JSON exports land in `tests/Scrinia.Benchmarks/BenchmarkDotNet.Artifacts/` and can be diffed
against a committed baseline to gate regressions in CI.

## License

BSD-3-Clause. Copyright (c) 2026 Nick Daniels.
