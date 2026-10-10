# Security & Threat Model

What Scrinia defends against, what it explicitly does **not**, and where you are expected to
provide the control yourself.

Scrinia's server surface is the local `scri serve` process, which exposes MCP over stdio and reads
and writes the configured workspace's `.scrinia/` directory. There is no Scrinia HTTP API,
server-side API-key authentication, or multi-tenant deployment model. API keys mentioned below are
only credentials for optional outbound embedding providers. The operating system identity and
filesystem permissions therefore form the primary isolation boundary; what remains below is
genuinely Scrinia's problem.

## Trust boundaries

| Principal | Trust | Enforced by |
|---|---|---|
| **You** | Trusted: you own the workspace and launch the process. | Filesystem ownership of the repository. |
| **The agent driving the MCP tools** | Trusted exactly as far as the process is — it runs as you. | Nothing. See below. |
| **Stored content** | Untrusted: memory bodies are agent and user input. | Content is data, never a code path. |

**The agent boundary is not a boundary.** `scri serve` speaks MCP over stdio to whatever launched
it, and inherits that process's identity. Any agent that can call `remember` can write anywhere the
workspace sandbox allows, and any agent that can call `recall` can read every memory in the
workspace. There is no per-memory authorisation, and there is no mechanism by which there could be:
one user, one workspace, one process. If you need memories one agent cannot see, use a separate
workspace — the isolation is the directory.

## Threats addressed

### Untrusted artifact content

An NMP/2 artifact can arrive from a bundle you did not create, so decoding is bounded rather than
trusting the header:

- **Decompression bombs** — `Nmp2Strategy.BrotliDecompressBounded` rejects an artifact whose Brotli
  output exceeds `MaxDecodedBytes` (64 MB) *during* decompression, not after.
- **Chunk-count abuse** — an artifact declaring outside `[1, MaxChunkCount]` (100,000) chunks is
  refused before any allocation.
- **Integrity** — a CRC-32 or length mismatch fails the decode rather than yielding partial content.

Length is a mandatory nonnegative decimal value; CRC32 is exactly eight hexadecimal characters,
and the codec must be `BR+B64`. An optional `C` must be positive and no greater than 100,000.
Valid empty content and CRLF artifacts remain supported within the unchanged 64 MiB decoded limit.

`BundleFormatService` applies all three on import, which is the one path where bytes you did not
write reach the decoder.

### Bundle and journal limits

Bundle admission bounds compressed CLI input and actual expanded entries, in addition to the
NMP/2 decode constraints above. MiB means 1,048,576 bytes. These fixed limits have no configuration
override.

| Resource | Limit |
|---|---:|
| Compressed CLI bundle input | 256 MiB |
| Physical ZIP entries, including unused entries | 4,096 |
| Logical memories plus file entities across the archive | 1,000 |
| Each manifest, topic-index, or metadata JSON entry | 16 MiB |
| Each expanded artifact or Markdown body | 96 MiB |
| Aggregate distinct expanded archive entries | 128 MiB |
| Aggregate prior bytes in the workspace bundle journal | 128 MiB |
| Workspace bundle journal members | 2,000 |
| Encoded workspace bundle journal JSON | 230,161,068 bytes |

Repeated reads of one ZIP entry do not double-charge the expanded total. All declared topics and
file entities are validated even when a topic filter excludes them from publication.
Prior-state journal capacity is checked before publishing recovery evidence or changing members.

An invalid archive returns `INVALID_BUNDLE`. Invalid retained journal evidence returns
`BUNDLE_RECOVERY_REQUIRED`. A valid archive whose destination's prior state cannot fit the
journal returns `BUNDLE_IMPORT_FAILED` before mutation. The journal document bound is
`4 * ceil(128 MiB / 3) + 2,000 * (6 * 4,096 + 1,024) + 4,096`; relative member paths are bounded
to 4,096 UTF-8 bytes, operation IDs to 64, and SHA-256 hashes to fixed lowercase text.
The standalone scope journal has its own 181,009,068-byte document bound; see
[recovery protocols](architecture/core.md#journaled-bundle-restore-recovery).

### Path traversal

`PathParser` rejects `\`, `:`, `..`, and control characters in memory names. Bundle import paths are
constrained to the workspace root before any file I/O. A memory name cannot address a file outside
`.scrinia/`.

Memory addresses are logical and validated before they reach storage. Persistent addresses use
`/topic/subject`, while ephemeral addresses use `~name` (`/temp/name` is its accepted alias).
`file://` and other URI forms, drive-letter paths, query/fragment syntax, and physical filesystem
paths are rejected rather than resolved. Persisted `ArtifactKey` values are opaque store-relative
keys; callers must not use them as URIs or bypass the store's path validation. Shared roots are
registered with `scri shared base add <name> <absolute-root>` and always use `project-git` tracking.

Recovery validates the complete owned manifest and preflights all current members before any
replay. Only approved ordinary-memory and skill/agent body-metadata locations are
admitted; physical collisions and non-regular members are refused. Compensation touches only the
operation's members. SHA-256 hashes prove consistency with recorded states, not authenticity
against a hostile editor who can alter workspace evidence. An uncoordinated external process can
still race between a path/hash check and its open, rename, or delete.

### Response injection

MCP responses are hand-built YAML. The serialiser escapes control characters and quotes problematic
ones, so a memory body containing YAML syntax cannot restructure the envelope around it. JSON output
(`--json`) uses source-generated `JsonSerializerContext`.

### Concurrent writes

Persistent operations take shared workspace admission before scope/entity locks; bundle
publication and recovery take exclusive admission at `.scrinia/.bundle-restore.lock`. Permission
errors fail closed. Scope locks and in-process gates then serialize the relevant mutations.
Contention logs at `Warning`, timeouts at `Error`, with structured lock path and elapsed time.

Stop all old `scri serve` and CLI writers before reopening a workspace with the repaired build.
Old writers cannot participate in the workspace admission protocol. Direct filesystem writers
remain outside it.

Windows configuration-lock identity also normalizes absolute path case, changing the lock
filenames used by older builds. Stop older configuration writers; mixed-version coordination is
not assumed. Tracking journals are version 1 and own exactly `config.json`, `.gitignore`,
`.gitattributes`, and `merge.config`: 16 MiB per member state, 128 MiB total decoded state,
192 MiB serialized journal, and 64 UTF-8 bytes per operation ID. Ambiguous legacy `complete`
or unreadable evidence is retained for investigation, never fixed by blindly deleting a journal.

A workspace bundle transaction owns its ordinary-memory and file-entity manifest, not every file
under `.scrinia`. Configuration and derived facts/vectors are outside rollback. Persistent vector
mutations independently reread fresh scope state under an exclusive file lock and publish a
complete replacement before their local cache. Other warm readers can retain earlier vector
snapshots until mutation or reload.

A durable committed bundle remains committed if evidence cleanup fails. Unsupported legacy
bundle evidence is retained and refused rather than converted automatically. Preserve the whole
workspace before intervention; [recovery guidance](troubleshooting.md#bundle-recovery-required)
describes the supported diagnostic and operator boundaries. New scope mutations and legacy index
rewrites refuse retained terminal scope/coordinator evidence. File-entity cleanup proves expected
absence separately from empty content; existing entries or failed inspection cannot authorize
evidence deletion.

## Threats NOT addressed

### Configuration and shared-base boundaries

The workspace configuration remains a flat dictionary at `.scrinia/config.json`; the per-user
shared-base registry is the typed `SCRINIA_HOME/config.json`. `SCRINIA_HOME` accepts only a non-empty
absolute override and fails closed for empty or relative values. Shared roots must be validated
repository/worktree roots, and shared tracking is fixed to `project-git`.

Registry and workspace mutations are serialized by an interprocess lock and committed with
same-directory atomic replacement. Failed writes preserve the old bytes or leave a recovery journal
for deterministic restoration. Scrinia performs filesystem validation only: it does not invoke Git,
inspect the Git index, or claim that a file is tracked. Results use `gitIndexStatus: UNVERIFIED`.

Disabled shared-base names are deliberately private. Workspace startup, resolution, warnings,
diagnostics, and errors do not enumerate disabled registry entries or roots; the explicit user
`scri shared base list --all` command is the sole complete registry listing surface, including
disabled entries. The default list does not disclose disabled names or roots.

The declared configuration/tracking error inventory includes `CONFIG_RECOVERY_REQUIRED` (exit
class 4): a compensation or recovery attempt failed, so the caller must preserve and replay the
recovery evidence before treating the configuration transaction as complete.

### Hook-only advisory and prompt isolation

Scrinia's shared MCP surface has no prompt or correlation field. The commit/push advisory is the
fail-silent `scri hint` transport, delivered only through a supported agent's `UserPromptSubmit`
hook (`scri hint --client <host>`); the shared server never receives, requests, or correlates the
initiating prompt, and no response branch depends on it. Because the advisory is hook-only, an
unsupported client or a client without a verified hook emits nothing and exits 0 — the advisory
never blocks a prompt.

### Secrets in stored content

Scrinia does not scan memory bodies for tokens, credentials, or PII, and **agents store what they
are told**. This matters more here than in most systems because `.scrinia/` is designed to be
committed: a secret an agent writes into a memory is a secret in your git history.

Run a secret scanner (`gitleaks` or equivalent) over `.scrinia/` in a pre-commit hook if agents
write anything derived from a live environment.

### Encryption at rest

`.scrinia/` is **plaintext on disk**. NMP/2 is Brotli-compressed and Base64Url-encoded, which is not
encryption and is trivially reversible — `scri show` does it. Anyone with read access to the
directory reads your memories. Encrypt the volume (LUKS, BitLocker, FileVault, cloud disk
encryption) if your threat model includes offline attackers or backup media.

### The stdio channel

`scri serve` inherits its parent's trust. Anyone who can attach a debugger to the parent, or read
the stdio pipe, is effectively you. This is the same property every MCP server has.

### Immutable tool package

The `scri` global tool is an **immutable** NuGet package: once `dotnet tool install --global
scrinia` completes, the tool directory is never written at runtime. The package ships only the
managed `scri` command, its managed dependencies, the offline-required embedded prompts/skills,
and the package readme — no Merge driver, no Vulkan/LLamaSharp assembly, no native RID asset, no
downloaded model, no repository data, and no `.scrinia`. The per-user registry, models, installed
plugin payloads, and plugin state live below `SCRINIA_HOME`; workspace configuration, memories,
and derived vectors/facts live in `<workspace-root>/.scrinia/`. Scrinia does not mutate installed
package bytes at runtime. Filesystem access still governs who can modify those bytes externally.

### Plugin trust

The embeddings plugin is a **separately installed** child process launched from the mandatory
`SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — never the immutable package directory
or a flat `plugins/<plugin>` fallback — resolved from the `plugins:embeddings` config key, and it
runs with your permissions, not a sandbox's. Treat the plugin directory as an executable allowlist:

- Restrict write permission on `SCRINIA_HOME/plugins/` to your own account. Anyone who can write there
  can run code as you the next time `scri serve` starts.
- Only install plugins you have read.
- Pin plugin versions in source control.

Explicit setup validates the embeddings manifest and copies its exact payload without changing
the extracted source. Manifest version 1 is bounded to 64 KiB and 32 file records, with 512 MiB
per file and 1 GiB total. The fixed plugin name, exact host version, and current supported RID are
required. A checksum detects changed bundle contents; it does not authenticate the publisher.
macOS uses CPU/Metal; Windows/Linux use Vulkan. See [installation](installation.md#embeddings-plugin-installation).

### Remote embedding providers

Configuring `Scrinia:Embeddings:Provider` as `ollama`, `openai`, `voyageai`, `azure`, or `google`
sends **memory content** to that provider for embedding, along with your API key for it.
First-run Model2Vec and plugin GGUF downloads
also use the network, storing files below `SCRINIA_HOME/models`. After download, built-in
`model2vec` inference runs in-process without sending memory content. Optional HTTP providers use
their configured endpoint, including a local Ollama endpoint when configured.

Provider API keys live in `.scrinia/config.json` or environment variables. If you commit
`.scrinia/`, put the key in the environment, not the config file.

### Audit

Structured logs (`ILogger`) record lock contention and errors. This is not a tamper-evident audit
log, and there is no record of which agent read which memory.

## Data at rest

```
{repo}/.scrinia/
  memories/{scope}/{name}.nmp2       Brotli + Base64Url, plaintext content
  memories/{scope}/{name}.meta.json  plaintext sidecar (description, keywords, attributes, refs)
  memories/{scope}/versions/         rollback archives, gitignored by default
  skills/{name}.md                   plaintext Markdown
  agent/{name}.md                    plaintext Markdown
  embeddings/                        binary vector cache (provider-specific)
  config.json                        workspace config — may hold provider API keys
```

**Sensitive:** memory contents, and `config.json` if you put a provider key in it.

**Safe to commit, and meant to be:** `skills/` and `agent/` markdown, `.meta.json` sidecars, and the
memory bodies themselves when they are project knowledge rather than anything derived from a live
environment. That is the design — memories travel with the code.

`versions/` is gitignored by default (`.scrinia/.gitignore` includes `**/versions/`).

## Recommendations

1. **Scan before you commit.** `.scrinia/` goes into git by design, and agents write into it
   without review. A `gitleaks` pre-commit hook is the single highest-value control here.
2. **Keep provider API keys in the environment**, not `.scrinia/config.json`, if the workspace is
   committed or shared.
3. **Encrypt the volume** if offline access to the disk is in your threat model. Standard OS disk
   encryption is sufficient.
4. **Audit your plugins**, and lock down write access to the plugin directory — a plugin is an
   executable you launch as yourself.
5. **Use a separate workspace for anything an agent should not see.** The directory is the
   isolation boundary; there is no finer one.
