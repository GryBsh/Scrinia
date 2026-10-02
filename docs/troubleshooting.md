# Troubleshooting

Common issues and solutions for the Scrinia CLI, server, and plugins.

Examples marked `sh` use POSIX shell syntax. Use the equivalent commands in your preferred shell
on other platforms.

## CLI Issues

### Strict migration error codes

Strict safety failures are stable across CLI and MCP. Correct `MALFORMED_ADDRESS` or
`SCOPE_CONTAINMENT_FORBIDDEN` by using a canonical logical address; use the supported lifecycle
when `/skill/...` or `/agent/...` returns `RESERVED_PATH_OPERATION_FORBIDDEN`.

Bundle validation returns `INVALID_BUNDLE` before mutation. A failed commit returns
`BUNDLE_IMPORT_FAILED` only after rollback is proven. `BUNDLE_RECOVERY_REQUIRED` means recovery
evidence is retained: do not retry another import until the recovery command has completed.

### "No .scrinia/ directory found"

The CLI walks up from the current directory looking for the nearest enclosing git repository and uses that repository's root as the resolved workspace root. If no repository encloses the current directory, `cwd` itself is the workspace root and no upward search happens. Either way, `.scrinia/` is created lazily by the first write; read-only commands against a workspace with no store create nothing.

**Fix:** Run `scri` from within your project directory, or use `--workspace-root /path/to/project`.

### Memories from another project appear, or my memories seem missing

The workspace root is the nearest enclosing git repository, found by walking up from the current directory looking for a `.git` entry and stopping at the first one. A project that isn't inside any git repository uses its own directory as the root, with no upward search. This means two unrelated projects only ever share a store if they share the same enclosing repository -- a project's memories never bind to a `.scrinia/` sitting in a distant ancestor directory just because that ancestor happens to have one.

If `scri` finds a `.scrinia/` directory outside the root it resolved to, it prints a warning to stderr on selected memory-write commands (`store`, `forget`, `append`, `compact`, and `link`), `setup`, `serve` startup, and `restore` (the SessionStart hook, once per session). Bundle import is not included. The warning names the ignored store, the root actually in use, and a ready-to-run migration command pair, for example:

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

### Model not found

On its first startup, `scri serve` automatically downloads the built-in Model2Vec embedding model
(approximately 50 MB total, including supporting files), unless you pass `--no-auto-setup`. When an HTTP provider or the Vulkan provider is
configured, that provider may use its own model and model-download process instead.

**Fix:**
```sh
scri setup
```

This downloads `m2v-MiniLM-L6-v2` to `SCRINIA_HOME/models/m2v-MiniLM-L6-v2/`. Without the built-in
model, search falls back to BM25-only (keyword matching still works). If you use an HTTP or
Vulkan provider, follow that provider's model configuration instead.

### Semantic search not working

If semantic search returns nothing or degrades to BM25-only, the built-in Model2Vec model is
missing from `SCRINIA_HOME/models/m2v-MiniLM-L6-v2/`, the configured HTTP or Vulkan provider is
unavailable, or the active provider failed to start. Verify the model is present (run `scri setup`),
confirm the active provider with `scri config Scrinia:Embeddings:Provider`, and read the embedding
provider warning on stderr.

An embeddings executable that returns `EMBEDDINGS_PROTOCOL_UNSUPPORTED` or
`EMBEDDINGS_PROTOCOL_INVALID` is disabled for the session. Upgrade or reinstall the executable;
lexical search remains available. Vector files with an incompatible provider signature are
quarantined, not reused. Run `scri reindex` after the active provider is healthy.

### Search is slower or returns fewer candidates

`Scrinia:Search:CandidateLimit` bounds the shared lexical candidate snapshot. It defaults to `200`
and accepts `1` through `1000` only. Increase it deliberately for broader retrieval; invalid values
fail configuration loading rather than silently scanning the whole corpus.

### Slow first search after startup

The first search in a scope may load index entries, decode artifacts, and load vectors. The 50 MB
LRU caches decoded artifact text; it does not cache BM25 corpus statistics, which are computed for
each search over that search's candidate set.

### Access denied when publishing

The `scri` executable is locked while the MCP server is running.

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

Symptoms: semantic search uses the in-process provider instead of the Vulkan plugin, or search
falls back to BM25 and path scoring after the configured plugin fails.

**Checklist:**
1. The configured plugin binary exists at the mandatory
   `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — the default name is
   `scri-plugin-embeddings`, resolved as
   `SCRINIA_HOME/plugins/scri-plugin-embeddings/<version>/<rid>/scri-plugin-embeddings.exe`. There
   is no flat `plugins/<plugin>` fallback.
2. Vulkan drivers are installed and up to date
3. GPU supports Vulkan (check with `vulkaninfo`)
4. The GGUF model exists at `SCRINIA_HOME/models/` (the default is
   `all-MiniLM-L6-v2-Q8_0.gguf`)

**Diagnostic:** Check CLI output during `scri serve` — plugin discovery is logged.

### Plugin crashes / degraded to BM25-only

The CLI auto-reconnects crashed plugins up to 3 times. After 3 failures, it sets `_degraded = true`
and falls back to BM25 and path scoring. Plugin failures never break core memory operations.

**Fix:** Check the plugin's stderr output (forwarded to CLI logs). Common causes:
- Out of GPU memory — try a smaller model
- Vulkan driver incompatibility — update drivers
- Model file corrupted — delete and re-download

### Plugin not found despite being installed

Plugin discovery resolves the executable only through the mandatory
`SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — there is no flat `plugins/<plugin>`
fallback. The plugin name comes from the `plugins:embeddings` config setting (default:
`scri-plugin-embeddings`), and the compatible version and current RID are selected from that
directory. When a plugin executable is found, Scrinia skips built-in Model2Vec; if that plugin
cannot start or becomes unavailable, it does not switch back to Model2Vec and search falls back to
BM25 and path scoring.

**Fix:**
```sh
scri config plugins:embeddings    # Check current setting
ls "$SCRINIA_HOME/plugins"        # List installed plugins under SCRINIA_HOME
```

## Memory Issues

### Version bloat in .scrinia/memories/{scope}/versions/

Every write to an existing memory archives the previous version — `store`, `append` and
`compact` alike. Each memory keeps its 10 most recent versions, so the archive grows with the
number of frequently-edited memories rather than with the number of writes.

**Fix:** Periodically clean old versions:
```sh
# Remove versions older than 30 days
find .scrinia/memories -path "*/versions/*.nmp2" -mtime +30 -delete
```

Archived copies exist for manual recovery only; nothing reads them back, so deleting them is
safe. See [Version retention](#version-retention) below for what is kept and for how long.

### Search returns unexpected results

Scrinia uses hybrid scoring. In the default additive mode, relevance is
`attributeScore + normalizedBm25*5 + supplementalEmbeddingScore + entityGraphScore`; the final
score is `alphaRelevance*relevance + alphaRecency*exp(-ageDays/tauDays)*recencyScale`. RRF mode
combines the same sources by rank instead of raw score. If results seem wrong:

1. Check if embeddings are active: `scri setup` (downloads model if missing)
2. Keywords matter: agent-provided keywords get +5 TF boost, auto-extracted get +2
3. Try `list(mode="full")` to see all entries with their keywords

### Cross-process conflicts

Multiple processes against one workspace — several CLI invocations, or a CLI alongside a running
`scri serve` — use OS-enforced file locks (`.lock` files per scope). If you see stale `.lock` files
after a crash, first stop `scri serve` and every other concurrent Scrinia CLI process. Do not delete
lock files while a Scrinia process is still running: an active process may still be using the lock,
and removing the file does not release its OS lock; another process could then write concurrently.
After all Scrinia processes have stopped:

**Fix:** In a POSIX shell, remove the lock files under the memories root and its topic
subdirectories:

```sh
find .scrinia/memories -type f -name .lock -delete
```

They are recreated automatically.

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
`**/versions/`) so they don't pollute commits. Bundles produced by `scri bundle export` do not
include `versions/`. Bundle export is a CLI command; it is not an MCP operation.

**Manual prune** for skill and agent rollback history (NMP/2 history is already self-managing):

```sh
# Keep the 5 newest skill rollback snapshots per skill name
for f in $(ls .scrinia/skills/versions/ | sed 's/_[0-9]*-[0-9]*\.md//' | sort -u); do
    ls -1t .scrinia/skills/versions/${f}_*.md | tail -n +6 | xargs -r rm
done
```

The equivalent for `agent/versions/` is the same with the directory swapped. Pruning rollback
history is safe — only the currently-active file is read at load time; archived copies exist solely
for human recovery.

## Manual retired merge-driver cleanup and conflict resolution

Scrinia no longer installs merge drivers, and it never launches Git. Conflicts on `.scrinia` files
in a project-Git workspace are resolved by you (or your agent) by hand. The commands below are
guidance only — run them in your shell or agent; Scrinia does not execute them.

If a prior multi-user setup left the legacy driver keys behind, unset them first (replace
`<workspace-root>` with the absolute path to your project root):

```sh
git -C "<workspace-root>" config --local --unset-all merge.scrinia-meta.driver
git -C "<workspace-root>" config --local --unset-all merge.scrinia-nmp2.driver
```

Then resolve the conflicted files manually.
