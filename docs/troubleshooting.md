# Troubleshooting

Common issues and solutions for the Scrinia CLI, server, and plugins.

## CLI Issues

### "No .scrinia/ directory found"

The CLI walks up from the current directory looking for the nearest enclosing git repository and uses that repository's root as the workspace root. If no repository encloses the current directory, `cwd` itself is the workspace root and no upward search happens. Either way, `.scrinia/` is created lazily -- only once something is written; reading commands against a workspace with no store create nothing.

**Fix:** Run `scri` from within your project directory, or use `--workspace-root /path/to/project`.

### Memories from another project appear, or my memories seem missing

The workspace root is the nearest enclosing git repository, found by walking up from the current directory looking for a `.git` entry and stopping at the first one. A project that isn't inside any git repository uses its own directory as the root, with no upward search. This means two unrelated projects only ever share a store if they share the same enclosing repository -- a project's memories never bind to a `.scrinia/` sitting in a distant ancestor directory just because that ancestor happens to have one.

If `scri` finds a `.scrinia/` directory outside the root it resolved to, it prints a warning to stderr on writes, `setup`, `serve` startup, and `restore` (the SessionStart hook, once per session), naming the ignored store, the root actually in use, and a ready-to-run migration command pair, for example:

```
scrinia: ignoring store at C:\ancestor\.scrinia
         using C:\projects\your-project

to migrate its memories:
  scri bundle export <topics> --workspace-root "C:\ancestor"
  scri bundle import <file> --workspace-root "C:\projects\your-project"
```

**Fix:**
- If memories from another project are showing up, confirm you're running `scri` (or your MCP client) from inside the intended project, and check stderr for an "ignoring store" warning naming the store actually being used.
- If memories seem to have disappeared, look for an "ignoring store" warning pointing at the old location, then run the printed `scri bundle export` / `scri bundle import` pair to move those memories into the workspace root now in use.
- To make resolution independent of the directory a command or MCP client happens to launch from, pass `--workspace-root /path/to/project` explicitly.
- The warning only fires on `store`/`forget`/`append`/`compact`/`link`, `setup`, `serve` startup, and `restore` — never on read-only commands like `hint`, `recall`, `search`, `list`, or `show`. If you only ever run read commands, run `scri restore` (or any write) once to surface it.

### "Model not found" / Semantic search not working

The built-in Model2Vec embedding model (~22MB) must be downloaded before semantic search works.

**Fix:**
```bash
scri setup
```

This downloads `m2v-MiniLM-L6-v2` to `{exeDir}/models/`. Without it, search falls back to BM25-only (keyword matching still works).

### Slow first search after startup

The first search in a scope computes BM25 corpus statistics and may load vectors. Subsequent searches use cached stats and the 50MB LRU artifact cache.

### "Access denied" when publishing

The `scri.exe` binary is locked while the MCP server is running.

**Fix:** Stop the MCP server (e.g., toggle it off in your editor's MCP settings), then re-run `publish.ps1`.

### Config changes not taking effect

Config resolution order (highest priority first):
1. Environment variable (key with `:` → `_`, uppercased)
2. `.scrinia/config.json`
3. Hardcoded default

**Fix:** Check `scri config` to see current values. Environment variables override config file values.

Config is read once per process and cached, so a change written by another process is not seen by
an already-running one. That costs nothing for CLI commands, where every invocation is a fresh
process, but a long-running `scri serve` keeps the values it started with — restart it after
`scri config set`.

## Plugin Issues

### Vulkan plugin not loading

Symptoms: Semantic search works but uses CPU (Model2Vec) instead of GPU.

**Checklist:**
1. Plugin binary exists at `{exeDir}/plugins/scri-plugin-embeddings/scri-plugin-embeddings[.exe]` (checked first), or `{exeDir}/plugins/scri-plugin-embeddings[.exe]` (checked second)
2. Vulkan drivers are installed and up to date
3. GPU supports Vulkan (check with `vulkaninfo`)
4. GGUF model exists at `{exeDir}/plugins/scri-plugin-embeddings/models/`

**Diagnostic:** Check CLI output during `scri serve` — plugin discovery is logged.

### Plugin crashes / "degraded to BM25-only"

The CLI auto-reconnects crashed plugins up to 3 times. After 3 failures, it sets `_degraded = true` and falls back to BM25-only search. Plugin failures never break core memory operations.

**Fix:** Check the plugin's stderr output (forwarded to CLI logs). Common causes:
- Out of GPU memory — try a smaller model
- Vulkan driver incompatibility — update drivers
- Model file corrupted — delete and re-download

### Plugin not found despite being installed

Plugin discovery looks for executables at `{exeDir}/plugins/{name}/{name}[.exe]` first, then `{exeDir}/plugins/{name}[.exe]`. The plugin name comes from the `plugins:embeddings` config setting (default: `scri-plugin-embeddings`).

**Fix:**
```bash
scri config plugins:embeddings    # Check current setting
ls $(dirname $(which scri))/plugins/  # List discovered plugins
```

## Memory Issues

### Version bloat in .scrinia/memories/{scope}/versions/

Every write to an existing memory archives the previous version — `store`, `append` and
`compact` alike. Each memory keeps its 10 most recent versions, so the archive grows with the
number of frequently-edited memories rather than with the number of writes.

**Fix:** Periodically clean old versions:
```bash
# Remove versions older than 30 days
find .scrinia/memories -path "*/versions/*.nmp2" -mtime +30 -delete
```

Archived copies exist for manual recovery only; nothing reads them back, so deleting them is
safe. See [Version retention](#version-retention) below for what is kept and for how long.

### Search returns unexpected results

Scrinia uses hybrid scoring: `fieldScore + bm25*5 + semanticScore`. If results seem wrong:

1. Check if embeddings are active: `scri setup` (downloads model if missing)
2. Keywords matter: agent-provided keywords get +5 TF boost, auto-extracted get +2
3. Try `list(mode="full")` to see all entries with their keywords

### Cross-process conflicts

Multiple processes against one workspace — several CLI invocations, or a CLI alongside a running
`scri serve` — use OS-enforced file locks (`.lock` files per scope). If you see stale `.lock` files
after a crash:

**Fix:** Delete `.lock` files in `.scrinia/store/` and `.scrinia/topics/*/`. They are recreated automatically.

### `RECOVERY_BLOCKED` during a memory or bundle operation

An interrupted bundle restore may leave `.restore-journal.json` in one persistent topic scope. Scrinia
automatically rolls that scope forward before reads and writes. If the retained journal is malformed,
fails integrity verification, requires a newer Scrinia version, or cannot be replayed because of an
I/O failure, only that scope is blocked; other topics and ephemeral memories remain available.

**Fix:** Use the scope and journal path in the error. For a newer journal, upgrade Scrinia and retry.
For a malformed or integrity-failed journal, preserve the journal, inspect or restore it with a
compatible Scrinia build, then retry. For replay failures, check permissions and free disk space before
retrying. Do not delete or edit the journal to force recovery: Scrinia deliberately retains failed
journals as evidence and never rolls back or rereads the source bundle.

This recovery is per topic scope only. A multi-topic bundle and skill/agent file entities are not a
single transaction, so an interruption can leave one topic awaiting its next access while another has
already completed.

### Version retention

There is **no limit on the number of memories** you can store, and the currently-active body of
every memory is preserved indefinitely. The retention below is the size of the **rollback history**
kept beside each memory — used for recovery after an accidental overwrite, not for steady-state
reads.

Every overwrite / append to a memory `foo` copies the *prior* body to a sibling `versions/`
directory before the new body is written. The active file (`foo.nmp2`, `foo.md`) is never touched by
the retention logic.

| Memory class | Active file (kept forever) | Rollback history location | History size |
|---|---|---|---|
| NMP/2 memory body | `{scope}/{name}.nmp2` | `{scope}/versions/{name}_{ts}.nmp2` plus its `.meta.json` | Capped at 10 newest per memory (oldest archives auto-deleted on each overwrite). |
| Skill markdown (`/skill/*`) | `.scrinia/skills/{name}.md` | `.scrinia/skills/versions/{name}_{ts}.md` | Unbounded — every prior version is retained until you prune manually. |
| Agent config markdown (`/agent/*`) | `.scrinia/agent/{name}.md` | `.scrinia/agent/versions/{name}_{ts}.md` | Unbounded — every prior version is retained until you prune manually. |

`{ts}` is `yyyyMMdd-HHmmss-fff` for an NMP/2 archive, and a `-2`, `-3` … suffix is appended if that
millisecond is already taken. An archive is never overwritten: two writes to the same memory inside
one second are the ordinary shape of an agent correcting itself mid-turn, and a coarser stamp used
to let the second one silently replace the first.

The NMP/2 archive carries the memory's `.meta.json` sidecar alongside the body, so a restored
version arrives with the keywords, attributes and drift baseline it had at the time.

The `versions/` directories are gitignored by default (`.scrinia/.gitignore` includes
`**/versions/`) so they don't pollute commits. Bundles produced by `scri bundle export` /
`bundle('export')` do not include `versions/`.

**Manual prune** for skill and agent rollback history (NMP/2 history is already self-managing):

```bash
# Keep the 5 newest skill rollback snapshots per skill name
for f in $(ls .scrinia/skills/versions/ | sed 's/_[0-9]*-[0-9]*\.md//' | sort -u); do
    ls -1t .scrinia/skills/versions/${f}_*.md | tail -n +6 | xargs -r rm
done
```

The equivalent for `agent/versions/` is the same with the directory swapped. Pruning rollback
history is safe — only the currently-active file is read at load time; archived copies exist solely
for human recovery.
