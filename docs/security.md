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

`BundleFormatService` applies all three on import, which is the one path where bytes you did not
write reach the decoder.

### Path traversal

`PathParser` rejects `\`, `:`, `..`, and control characters in memory names. Bundle import paths are
constrained to the workspace root before any file I/O. A memory name cannot address a file outside
`.scrinia/`.

Memory addresses are logical and validated before they reach storage. `file://` and other URI
forms, drive-letter paths, query/fragment syntax, and physical filesystem paths are rejected rather
than resolved. Persisted `ArtifactKey` values are opaque store-relative keys; callers must not use
them as URIs or bypass the store's path validation.

### Response injection

MCP responses are hand-built YAML. The serialiser escapes control characters and quotes problematic
ones, so a memory body containing YAML syntax cannot restructure the envelope around it. JSON output
(`--json`) uses source-generated `JsonSerializerContext`.

### Concurrent writes

- **Cross-process races** — OS-enforced file locks (`FileLock`) serialise index mutations across
  processes. Contention logs at `Warning`, timeouts at `Error`, with structured `{LockPath}` and
  `{ElapsedMs}`.
- **In-process** — `VectorStore` serialises writes with a per-scope `SemaphoreSlim`;
  `FileMemoryStore` uses per-scope reader-writer locks.

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

### Secrets in stored content

Scrinia does not scan memory bodies for tokens, credentials, or PII, and **agents store what they
are told**. This matters more here than in most systems because `.scrinia/` is designed to be
committed: a secret an agent writes into a memory is a secret in your git history.

Run a secret scanner (`gitleaks` or equivalent) over `.scrinia/` in a pre-commit hook if agents
write anything derived from a live environment.

### Encryption at rest

`.scrinia/` is **plaintext on disk**. NMP/2 is Brotli-compressed and Base64Url-encoded, which is not
encryption and is trivially reversible — `scri recall` does it. Anyone with read access to the
directory reads your memories. Encrypt the volume (LUKS, BitLocker, FileVault, cloud disk
encryption) if your threat model includes offline attackers or backup media.

### The stdio channel

`scri serve` inherits its parent's trust. Anyone who can attach a debugger to the parent, or read
the stdio pipe, is effectively you. This is the same property every MCP server has.

### Plugin trust

The embeddings plugin is launched as a **child process** — `{exeDir}/plugins/{name}/{name}[.exe]`,
resolved from the `plugins:embeddings` config key — and runs with your permissions, not a
sandbox's. Treat the plugin directory as an executable allowlist:

- Restrict write permission on `{exeDir}/plugins/` to your own account. Anyone who can write there
  can run code as you the next time `scri serve` starts.
- Only install plugins you have read.
- Pin plugin versions in source control.

### Remote embedding providers

Configuring `Scrinia:Embeddings:Provider` as `ollama`, `openai`, `voyageai`, `azure`, or `google`
sends **memory content** to that provider for embedding, along with your API key for it. That is the
only outbound network call Scrinia makes, and it is off by default: the built-in `model2vec`
provider runs in-process and sends nothing.

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
