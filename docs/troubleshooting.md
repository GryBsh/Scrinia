# Troubleshooting

Common issues and solutions for the Scrinia CLI, server, and plugins.

Examples marked `sh` use POSIX shell syntax. Use the equivalent commands in your preferred shell
on other platforms.

## CLI Issues

### Strict migration error codes

Strict safety failures are stable across CLI and MCP. Correct `MALFORMED_ADDRESS` or
`SCOPE_CONTAINMENT_FORBIDDEN` by using a canonical logical address; use the supported lifecycle
when `/skill/...` or `/agent/...` returns `RESERVED_PATH_OPERATION_FORBIDDEN`.

Bundle validation returns `INVALID_BUNDLE` before mutation. `BUNDLE_IMPORT_FAILED` means either
refusal before mutation (for example, prior-state journal capacity) or a failed publication with
proven rollback. `BUNDLE_RECOVERY_REQUIRED` retains evidence and refuses further persistent
operations until supported recovery succeeds. See [bundle recovery](#bundle-recovery-required);
there is no dedicated recovery CLI.

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
and accepts `1` through `1000` only. Scope, excluded-topic, path/address, and attribute eligibility
are applied before K. Selective filters do not require a larger K merely to get past ineligible
entries. Increase K deliberately for broader eligible retrieval at higher work/latency; it is
separate from the result limit. Invalid values fail configuration loading rather than scanning
the whole corpus.

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

### Setup installed the plugin but returned an error

`InstalledConfigurationFailed` and `InstalledModelPreparationFailed` confirm that setup retained
the verified payload under `SCRINIA_HOME/plugins/scri-plugin-embeddings/0.6.0/<rid>/`.
For configuration failure, correct the reported workspace/provider settings or I/O problem.
For a model preparation failure, restore network access or resolve the reported download error.
Then rerun the printed
`scri setup --embeddings-source <embeddings-source> --workspace-root <workspace-root>` command.
Replace each placeholder with the corresponding directory path and quote each path
separately. The extracted source is unchanged.
Other installation failures may name an uncertain destination; that path is not proof of installation.

Resolve conflicting `plugins:embeddings`, plugin-version environment overrides, or
`SCRINIA_EMBEDDINGS_PROVIDER`/`SCRINIA__EMBEDDINGS__PROVIDER` selections before retrying. Setup
success establishes installation, configuration, and path selection; it does not establish plugin
initialization or inference. `serve` does not install a bundle. Migration of legacy assets stored
beside the executable is separate. See [installation guidance](installation.md#embeddings-plugin-installation).

### Vulkan plugin not loading

Symptoms: semantic search uses the in-process provider instead of the Vulkan plugin, or search
falls back to BM25 and path scoring after the configured plugin fails.

**Checklist:**
1. The configured plugin binary exists at the mandatory
   `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — the default name is
   `scri-plugin-embeddings`, resolved as
   `SCRINIA_HOME/plugins/scri-plugin-embeddings/<version>/<rid>/scri-plugin-embeddings.exe`. There
   is no flat `plugins/<plugin>` fallback.
2. On Windows/Linux, Vulkan drivers are installed and up to date
3. On Windows/Linux, the GPU supports Vulkan (check with `vulkaninfo`); macOS uses CPU/Metal
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

Cooperating CLI and `scri serve` processes use workspace admission at
`.scrinia/.bundle-restore.lock` and scope/entity locks. The existence of a lock file does not mean
a process owns the OS lock, and deleting a filename does not release an active lock. Check active
processes and permissions instead of deleting lock files to force progress.

Before upgrading to the repaired protocol, stop every old `scri serve` and concurrent CLI writer.
Reopen with compatible builds only. Direct filesystem edits and older writers are not coordinated
by the new admission boundary. A lock-file cleanup is not transaction recovery.

### `RECOVERY_BLOCKED` in a persistent scope

The standalone per-scope `.restore-journal.json` records roll-forward final bytes.
`prepared` replays and verifies them; `completed` verifies terminal bytes and cleans evidence.
Malformed, unsupported, integrity-failed, or unreplayable scope evidence is retained and blocks
that scope. A verified terminal scope/coordinator snapshot can remain readable when cleanup is
deferred; new bundle publication still refuses retained scope/coordinator evidence.

Record the reported scope, operation ID, journal path, and reason. Stop writers and preserve a
filesystem backup before intervention. Resolve permissions, free-space, or other reported I/O
problems, then retry with a compatible build when the evidence is supported. Do not edit or delete
journals to bypass validation. These scope journals are distinct from workspace bundle evidence.

### Bundle recovery required

`BUNDLE_RECOVERY_REQUIRED` identifies workspace bundle evidence at `.scrinia/.bundle-restore-journal.json`; the similarly named
`.bundle-restore.lock` is only the admission lock. The journal owns ordinary-memory and
skill/agent pairs across the import. Recovery validates the whole manifest and preflights every
current member before changing any member. `prepared` evidence restores only owned prior
states; `committed` and `rolled-back` evidence verify terminal states and clean up. Recovery
never rereads the source ZIP.

A proven durable commit is success even when cleanup fails: imported counts still describe
committed changes. Retained bundle evidence blocks later persistent operations until verified
cleanup succeeds. An uncertain decision, unknown/legacy journal, terminal mismatch, or third
state refuses recovery without speculative compensation. For a prepared operation, recovery
restores the previous file contents only when each affected file matches a state recorded in the
journal. Explicit ephemeral-only operations and
the embedded guide remain available.

1. Stop every writer, including older builds.
2. Preserve a filesystem copy of the entire `.scrinia` tree, including journals, sidecars,
   archives, and other retained evidence. Export from a blocked workspace is not a backup.
3. Record the exact error code, operation ID if reported, evidence path, and underlying reason.
4. Correct reported permissions, disk capacity, or other I/O failures. Retry the original
   operation using a compatible repaired build only when it supports that evidence.
5. If evidence is legacy or cannot be proven, retain it for investigation. Restoring a known-good
   complete filesystem backup requires an explicit operator procedure with all writers stopped;
   upgrading alone does not convert unknown evidence.

Direct evidence edits are not an automatic repair procedure. There is no dedicated bundle
recovery command. `scri restore` restores agent context, `scri reindex` rebuilds derived vectors,
and `scri maintenance` handles its documented bounded cases. Maintenance does not universally
repair bundle journals. Committed file-entity cleanup checks expected absence separately from
an empty file; unexpected entries and unprovable inspection retain evidence. New scope mutations
refuse retained terminal scope/coordinator evidence, while bounded verified reads and explicit
recovery cleanup remain available. A legacy read requiring a sidecar rewrite also waits for cleanup.

See [exact limits](security.md#bundle-and-journal-limits) and
[protocol details](architecture/core.md#journaled-bundle-restore-recovery).

### Tracking recovery evidence

`CONFIG_RECOVERY_REQUIRED` or a recovery-blocked response may identify the separate tracking
journal that protects `config.json`, `.gitignore`, `.gitattributes`, and `merge.config`. Stop all
writers, back up the entire workspace and journal, and record the reported evidence path/reason.
Prepared recovery proceeds only when each affected file matches a state recorded in the journal.
A legacy bare `complete` marker cannot prove
which terminal bytes belong to the operation; unreadable evidence also requires investigation.
Never blindly delete the journal to make access succeed. A compatible retry can recover supported,
proven evidence; there is no dedicated recovery CLI.

Stop older configuration writers before upgrading: Windows configuration-lock identity now
normalizes absolute path case, changing lock filenames. Mixed old/new writers cannot be assumed
to coordinate, even if they refer to the same workspace.

### Version retention

For interruption recovery, first follow [evidence-preserving backup guidance](#bundle-recovery-required).
Per-memory archives below are manual history, distinct from whole-workspace transaction evidence.

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

## Shared tracking policy drift

`SHARED_TRACKING_POLICY_DRIFT` means the shared base's `.scrinia/.gitignore` lacks the exact current
`project-git` managed block. The block now includes scope archives, locks and recovery journals.
Scrinia refuses an older block and does not update it automatically.

Stop writers and back up the base. In the registered base's `.scrinia/.gitignore`, replace the existing
managed block, including both marker lines, with the block below. Preserve user rules outside it.
If neither marker is present, append the block. If markers are duplicated or malformed, repair them
so exactly one complete block remains. Save the entire file as UTF-8 without a BOM, with LF line
endings and a newline after the closing marker. Then restart the shared server,
or call `shared_refresh` if another available base allowed the server to stay running.

```gitignore
# >>> Scrinia managed: project-git derived files >>>
/embeddings/
/indexes/
/exports/
/versions/
/facts.json
/.restore-journal.json
/[mM][eE][mM][oO][rR][iI][eE][sS]/**/[vV][eE][rR][sS][iI][oO][nN][sS]/
/[mM][eE][mM][oO][rR][iI][eE][sS]/**/.[lL][oO][cC][kK]
/[mM][eE][mM][oO][rR][iI][eE][sS]/**/.[rR][eE][sS][tT][oO][rR][eE]-[jJ][oO][uU][rR][nN][aA][lL].[jJ][sS][oO][nN]
/[mM][eE][mM][oO][rR][iI][eE][sS]/**/.[mM][uU][lL][tT][iI]-[mM][eE][mM][oO][rR][yY]-[tT][rR][aA][nN][sS][aA][cC][tT][iI][oO][nN].[jJ][sS][oO][nN]
/[sS][kK][iI][lL][lL][sS]/[vV][eE][rR][sS][iI][oO][nN][sS]/
/[aA][gG][eE][nN][tT]/[vV][eE][rR][sS][iI][oO][nN][sS]/
/[sS][kK][iI][lL][lL][sS]/.[lL][oO][cC][kK]
/[aA][gG][eE][nN][tT]/.[lL][oO][cC][kK]
/[sS][kK][iI][lL][lL][sS]/.[fF][iI][lL][eE]-[eE][nN][tT][iI][tT][yY]-*.[rR][eE][cC][oO][vV][eE][rR][yY]
/[aA][gG][eE][nN][tT]/.[fF][iI][lL][eE]-[eE][nN][tT][iI][tT][yY]-*.[rR][eE][cC][oO][vV][eE][rR][yY]
/.[bB][uU][nN][dD][lL][eE]-[rR][eE][sS][tT][oO][rR][eE]-[jJ][oO][uU][rR][nN][aA][lL].[jJ][sS][oO][nN]
**/*.tmp
/*.lock
# <<< Scrinia managed: project-git derived files <<<
```

The bracket pairs cover Windows case aliases such as `Versions` without relying on Git's case
configuration. These rules reserve archives and operational files only in the listed namespaces;
an unrelated `topic/Versions` directory outside them remains authoritative. They affect fingerprinting,
inventory and printed cleanup guidance. Retained recovery journals still require recovery admission;
ignoring a journal in Git does not make it safe to delete. Existing tracked files remain tracked until
you run the appropriate printed cleanup commands yourself. Scrinia never runs Git.

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
