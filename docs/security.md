# Security & Threat Model

This document describes what Scrinia's security mechanisms defend against, what they
explicitly do **not** defend against, and where operators are expected to provide
additional controls. For the *what's implemented* view, see
[Server Administration → Authentication](server-admin.md#authentication) and
[Security Hardening](server-admin.md#security-hardening).

## Trust boundaries

Scrinia's threat model assumes three classes of principals:

| Principal | Trust | Authentication |
|---|---|---|
| **Operator** | Trusted: writes config, owns `{dataDir}`, holds the bootstrap key. | Filesystem ownership of `{dataDir}`. |
| **API caller (human or agent)** | Partially trusted: only what their API key grants. | Bearer token (`scri_…` Base64Url). |
| **Stored content** | Untrusted: memory bodies are user / agent input. | None — content is data, not code. |

Anything outside the API caller boundary is treated as adversarial — in particular,
the contents of memories and the values of API requests are validated, size-capped,
and never deserialised as code paths.

## What the API key protects

A valid bearer token authenticates the caller and binds them to a `(stores[], permissions[])`
tuple. Permissions are checked *per endpoint*, not per resource. The key:

- **Identifies** the caller for audit logging and rate-limit attribution.
- **Authorises** specific operation classes (`store`, `read`, `search`, `forget`, `chat`,
  `manage_keys`, …). See [server-admin.md → Permissions](server-admin.md#permissions).
- **Scopes** access to one or more named stores (`*` for all).

Privilege escalation is structurally prevented: callers cannot grant stores or
permissions they don't themselves hold.

### What the API key does NOT protect

- **Per-memory ACLs**. There is no row-level authorisation. Any key with `read` on a
  store can read every memory in that store. If you need data isolation between
  users, give each user a separate store.
- **Content classification**. The server doesn't inspect memory bodies for secrets.
  An attacker with `store` permission can write data that an attacker with `read`
  permission will later see. Pair `store`/`read` carefully.
- **Replay attacks**. Bearer tokens are not bound to request payloads, timestamps,
  or nonces. A leaked token is valid until revoked. Treat tokens like passwords —
  rotate on suspicion, revoke immediately on confirmed leak.

## Threats addressed

### Authentication & authorisation
- **Anonymous read/write** — every `/api/v1/*` route requires `Authorize` middleware. The
  only public route is the static SPA login page.
- **Lost bearer token** — operators can revoke a key via `manage_keys`. Hashed storage
  means a database leak doesn't expose live tokens.
- **Privilege escalation via key creation** — `ApiKeyAuthHandler` constrains new keys to a
  subset of the caller's own permissions/stores.

### Resource exhaustion
- **Memory-pressure DoS via large requests** — Kestrel body limit (10 MB), per-content
  element cap (5 MB), bundle multipart cap (50 MB), and `Nmp2Strategy.MaxDecodedBytes`
  (64 MB) prevent unbounded allocation.
- **Decompression bombs** — `Nmp2Strategy.BrotliDecompressBounded` rejects artifacts
  whose Brotli output exceeds the cap *during* decompression, not after.
- **Chunk-count abuse** — multi-chunk artifacts declaring more than `MaxChunkCount`
  (100k) chunks are rejected up front.
- **Per-IP / per-user flood** — sliding-window rate limit at 100 req/min on `/api/v1/*`.

### Input validation
- **Path traversal** — `PathParser` rejects `\`, `:`, `..`, and control characters. Bundle
  import paths are constrained to the workspace root before file I/O. The CLI store
  enforces the workspace sandbox identically.
- **XSS / response smuggling** — responses are emitted as YAML (MCP) or JSON (REST). Hand-built
  YAML serialisation escapes control chars and quotes problematic characters; JSON
  serialisation uses source-generated `JsonSerializerContext` so trimming-removed members
  cannot be exploited.
- **API-key forgery** — keys are random 32 bytes (Base64Url); only SHA-256 hashes are
  persisted. Even with full database read, an attacker cannot reconstruct live tokens.

### Concurrency
- **Cross-process write races** — OS-enforced file locks (`FileLock`) serialise index
  mutations across processes. Contention is logged at `Warning` and timeouts at `Error`
  with structured `{LockPath}` and `{ElapsedMs}` fields for SIEM ingestion.
- **In-process read/write conflicts** — `HnswIndex` uses `ReaderWriterLockSlim`;
  `FileMemoryStore` uses per-scope reader-writer locks.

### Network surface
- **CORS** — restrictive by default. Configure allowed origins via `Scrinia:CorsOrigins`.
  A `*` wildcard is supported but explicit per-origin lists are recommended.
- **HTTPS / HSTS** — enforced in production. Use a reverse proxy (nginx, Caddy)
  for TLS termination; configurations are in `server-admin.md`.
- **Security headers** — `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: strict-origin-when-cross-origin`, `X-XSS-Protection: 0`.

## Threats NOT addressed

These are out of scope for the current design. Operators must compensate.

### Encryption at rest
- `.scrinia/` is **plaintext on disk** (NMP/2-compressed, but not encrypted). A
  malicious actor with filesystem access reads memories directly. Encrypt the data
  volume (LUKS, BitLocker, EFS, cloud-provider disk encryption) if the threat model
  includes physical or backup-tape attackers.
- API keys are SHA-256 hashed in `apikeys.db`, but the SQLite database itself is
  unencrypted. Same advice — encrypt the volume.

### Secret detection in stored content
- Scrinia does not scan memory bodies for tokens, credentials, or PII. Agents may
  inadvertently store secrets. Recommend a pre-commit hook (e.g. `gitleaks`) on the
  workspace `.scrinia/` directory if you commit it to version control.

### Audit logging
- The structured log (`ILogger`) records request handling and lock contention but is
  **not a tamper-evident audit log**. Forward logs to an append-only sink (cloud
  logging service, syslog with WORM storage) if you need compliance-grade audit.
- API-key creation/revocation events are logged but **not signed**. An operator with
  log-write access can rewrite history.

### Multi-tenancy
- Stores provide *scoping*, not *isolation*. A privileged operator can read any
  store; the server process trusts itself. If you need cross-tenant isolation,
  run one Scrinia process per tenant (or use the CLI mode where each workspace is
  filesystem-isolated).

### Network confidentiality
- The MCP-over-HTTP transport accepts the bearer token in the `Authorization` header.
  Without TLS this is over the wire in plaintext. **Do not run Scrinia HTTP without
  TLS** outside `127.0.0.1`.
- The MCP-over-stdio transport (CLI) inherits parent-process trust. Anyone who can
  attach a debugger or read the stdio pipe is effectively privileged.

### Plugin trust
- Loaded plugins run in-process with full server permissions. There is no plugin
  sandboxing. Treat the plugin directory like an executable allowlist:
  - Restrict filesystem permissions on `{dataDir}/plugins/` to the operator account.
  - Only install plugins you have read and audited.
  - Pin plugin versions in source control.

### Token-based DoS amplification
- `chat` permission allows the caller to drive upstream LLM API calls. A leaked
  `chat` token amplifies into provider cost. Mitigate with:
  - Tight rate limits on `chat`-permitted keys.
  - Per-provider spend caps at the upstream provider (OpenAI / Anthropic / Gemini).
  - Audit the `MaxTokens` / temperature defaults in `Scrinia:Chat`.

## Data at rest

```
{dataDir}/
  apikeys.db                   # SQLite, SHA-256 key hashes + permissions
  BOOTSTRAP_KEY                # plaintext, written once on first start
  stores/{name}/.scrinia/
    {topic}/{name}.nmp2        # Brotli + Base64Url, plaintext content
    {topic}/{name}.meta.json   # plaintext sidecar (description, tags, refs)
    skills/{name}.md           # plaintext Markdown
    agent/{name}.md            # plaintext Markdown
    embeddings/                # binary vector cache (provider-specific)
```

**Sensitive files** (encrypt the parent volume):
- `BOOTSTRAP_KEY` — full-permission token. Delete after creating scoped keys.
- `apikeys.db` — key hashes (limits damage on theft) plus key metadata (userId,
  stores, permissions) which itself can be sensitive in a multi-tenant deployment.
- Memory contents — whatever your agents have stored.

**Not sensitive** (safe to commit to source control if your team agrees):
- `skills/`, `agent/` markdown files — these are reusable prompts.
- `.meta.json` sidecars when memory contents are public-by-design (e.g. architecture
  docs). The agent-facing `memory('list')` only shows metadata, never bodies.

## Recommendations

1. **Bootstrap discipline.** Use the bootstrap key once to create scoped keys,
   then **delete `BOOTSTRAP_KEY` and revoke the bootstrap key entry**. Track this
   in your deployment runbook.
2. **Per-purpose keys.** Don't reuse keys across humans and CI. A leaked CI key
   shouldn't be useful to a human attacker on a different network.
3. **Minimum permissions per key.** A CI key that only stores memories needs
   `store`, not `manage_keys`. The `*` store wildcard is rarely needed.
4. **Run behind TLS in production.** No exceptions. The MCP-over-HTTP transport
   is bearer-authenticated and that token will be replayed for the lifetime
   of the key.
5. **Encrypt the data volume.** Standard OS / cloud-provider disk encryption is
   sufficient — the threat is offline attackers, not online ones.
6. **Audit your plugins.** Read every plugin DLL you load — they run with full
   server permissions inside the same process.
7. **Forward logs to an append-only sink** if you need compliance-grade audit.
   The local log file is not tamper-evident.
8. **Rotate keys on personnel changes.** There is no SSO integration — keys are
   bearer tokens, not federated identities.
