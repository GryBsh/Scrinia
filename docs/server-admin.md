# Scrinium Administration

Scrinium is an ASP.NET Core HTTP API server providing multi-user, multi-store persistent memory with API key authentication, a REST API, MCP over HTTP, and a web UI.

## Running the Server

### Development

```bash
dotnet run --project src/Scrinia.Server
```

Starts on `http://localhost:5000`. On first run, a bootstrap API key with full permissions is generated and written to `{dataDir}/BOOTSTRAP_KEY`.

### With .NET Aspire

```bash
dotnet run --project src/Scrinia.AppHost
```

`Scrinia.AppHost` is a thin Aspire orchestrator (`Aspire.AppHost.Sdk` 13.x) that wires up
`Scrinia.Server` as a managed `scrinium` resource. Running it opens the Aspire dashboard
where you can:

- See the server's stdout / structured logs alongside HTTP request traces.
- Inspect distributed-trace spans across Scrinium and any sidecars you add (e.g. a database
  resource, a worker, a downstream API mock).
- Restart the server with state preserved in the data directory.

Use this for **local development** when you want richer telemetry than a plain
`dotnet run` provides, or as a starting point for multi-resource topologies (Scrinium +
custom workers). For production deployments, the Docker or reverse-proxy paths below are
the supported routes — Aspire's role here is developer UX, not deployment.

### Docker

```bash
docker compose up -d
```

The included `docker-compose.yml` maps port 8080, mounts a persistent data volume, and sets `Scrinia__DataDir=/data`.

```yaml
services:
  scrinium:
    build:
      context: .
      dockerfile: src/Scrinia.Server/Dockerfile
    ports:
      - "8080:8080"
    volumes:
      - scrinium-data:/data
    environment:
      - Scrinia__DataDir=/data
    restart: unless-stopped

volumes:
  scrinium-data:
```

### Production with Reverse Proxy

**nginx:**

```nginx
server {
    listen 443 ssl http2;
    server_name scrinia.example.com;

    ssl_certificate     /etc/ssl/certs/scrinia.crt;
    ssl_certificate_key /etc/ssl/private/scrinia.key;

    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        client_max_body_size 50m;
    }
}
```

**Caddy:**

```
scrinia.example.com {
    reverse_proxy localhost:5000
}
```

## Authentication

### API Keys

All API requests require a Bearer token:

```
Authorization: Bearer scri_<base64url-encoded-key>
```

Keys are generated as `scri_` + 32 random bytes (Base64url, no padding). Only the SHA-256 hash is stored in the database -- the raw key is shown exactly once at creation time.

### Bootstrap Key

On first startup (when no keys exist), the server auto-generates an admin key for all stores. The raw key is written to `{dataDir}/BOOTSTRAP_KEY`. Use it to create scoped keys, then revoke the bootstrap key for security.

The bootstrap key carries every permission in the table below — an explicit list in `Program.cs`, not a wildcard. It is the only key on a fresh server, so anything omitted from that list is a permission no operator can reach; if you add a permission to the server, add it there too.

### Permissions

Each API key carries a set of granular permissions:

| Permission | Allows |
|------------|--------|
| `read` | Show memory content, get chunks |
| `search` | Search memories, list memories |
| `store` | Create new memories |
| `append` | Append chunks to existing memories |
| `forget` | Delete memories |
| `copy` | Copy memories between scopes |
| `export` | Export topics to bundles |
| `import` | Import memories from bundles |
| `manage_keys` | Create, list, and revoke API keys |
| `manage_roles` | Manage roles (integration point for auth plugins) |
| `health` | Access `/health/details` endpoint for detailed diagnostics |
| `chat` | Use the agent chat endpoint to query store memories via LLM |
| `reindex` | Trigger a full-store re-embed via `POST .../embeddings/reindex` |

### Privilege Escalation Prevention

When creating a new key, the caller cannot grant:
- Stores they don't have access to
- Permissions they don't hold

### Multi-Store Access

Each key is scoped to specific stores. Use `*` for access to all stores:

```json
{
  "userId": "admin",
  "stores": ["*"],
  "permissions": ["read", "search", "store", "append", "forget", "manage_keys"]
}
```

## REST API Reference

All memory endpoints are scoped to a store:

```
/api/v1/stores/{store}/...
```

Every response under `/api/v1/stores` — success or error, including a `500` — carries an
`X-Scrinia-Api-Version` response header identifying the version of the REST contract the server
speaks. A client that doesn't recognise the header's value, or doesn't see the header at all,
should treat the server as untrusted rather than assume compatibility: an older or mismatched
server can silently ignore a parameter it doesn't understand (e.g. `excludeTopics`, `pathPrefix`)
instead of rejecting it, which would return an unfiltered result set that looks filtered.

### Store a Memory

```
POST /api/v1/stores/{store}/memories
```

**Permission:** `store`

**Request:**

```json
{
  "name": "/api/auth-flow",
  "content": ["Full text content here..."],
  "description": "OAuth2 authentication flow",
  "attributes": {
    "tag": ["auth", "oauth"],
    "review": ["2026-06-01T00:00:00.0000000+00:00", "when auth system changes"]
  },
  "keywords": ["token", "refresh"]
}
```

The `content` array controls chunking: a single element creates a single-chunk memory; multiple elements create independently retrievable chunks.

`attributes` is the searchable facet map — any key, each with an array of values. Up to 32 keys, up to 64 values per key; requests past either bound are rejected with a 400. `tag` and `review` are ordinary keys in that map: they replace the former top-level `tags`, `reviewAfter` and `reviewWhen` fields. Those three fields are no longer read — unknown members are ignored rather than rejected, so a stale client still sending them gets a 201 and the values are silently dropped. Check that your client sends `attributes` rather than relying on an error to tell you.

**`review` dates must be round-trip format on this endpoint.** A `review` value that parses as an ISO 8601 round-trip ("o") timestamp — `2026-06-01T00:00:00.0000000+00:00` — is read as the review date; any other value, including a bare `2026-06-01`, is read as the review *condition*. The MCP `remember` tool accepts the bare form and normalises it before storing; this endpoint does not, so the same string means different things on the two surfaces. A memory written here with a bare date will report as needing review indefinitely instead of going stale when the date passes.

**Response (201):**

```json
{
  "name": "auth-flow",
  "qualifiedName": "/api/auth-flow",
  "chunkCount": 1,
  "originalBytes": 1234,
  "message": "Stored /api/auth-flow (1 chunk, 1234 bytes)"
}
```

### List Memories

```
GET /api/v1/stores/{store}/memories[?scopes=local&scopes=api][?offset=0][&limit=200]
```

**Permission:** `search`

- `scopes` — optional repeated scope filter (`?scopes=a&scopes=b`), matching `/search`'s encoding
  below. A plain `,` inside one value now names a scope containing a literal comma, not two
  scopes — `?scopes=local,api` requests one scope named `local,api`, not `local` and `api`
  separately. Bounded at 64 entries / 1024 characters each, the same as `/search`'s `scopes`.
  Omitted or empty means every readable scope.
- `offset` / `limit` — pagination; `limit` is clamped to `[1, 1000]`, default 200.

**Response (200):**

```json
{
  "memories": [
    {
      "name": "auth-flow",
      "qualifiedName": "/api/auth-flow",
      "scope": "local-topic:api",
      "chunkCount": 1,
      "originalBytes": 1234,
      "createdAt": "2026-03-01T10:00:00Z",
      "updatedAt": null,
      "description": "OAuth2 authentication flow",
      "attributes": {
        "tag": ["auth", "oauth"]
      }
    }
  ],
  "total": 1
}
```

### Show a Memory

```
GET /api/v1/stores/{store}/memories/{name}
```

**Permission:** `read`

**Response (200):**

```json
{
  "name": "auth-flow",
  "content": "Full decoded text content...",
  "chunkCount": 1,
  "originalBytes": 1234
}
```

### Get a Specific Chunk

```
GET /api/v1/stores/{store}/memories/{name}/chunks/{index}
```

**Permission:** `read`. Chunk index is 1-based.

**Response (200):**

```json
{
  "content": "Decoded chunk text...",
  "chunkIndex": 1,
  "totalChunks": 3
}
```

### Append to a Memory

```
POST /api/v1/stores/{store}/memories/{name}/append
```

**Permission:** `append`

**Request:**

```json
{
  "content": "New chunk content to append..."
}
```

**Response (200):**

```json
{
  "name": "session-notes",
  "chunkCount": 3,
  "originalBytes": 4567,
  "message": "Appended to session-notes (now 3 chunks)"
}
```

### Delete a Memory

```
DELETE /api/v1/stores/{store}/memories/{name}
```

**Permission:** `forget`

**Response:** `204 No Content`

### Copy a Memory

```
POST /api/v1/stores/{store}/memories/{name}/copy
```

**Permission:** `copy`

**Request:**

```json
{
  "destination": "/arch/auth-flow",
  "overwrite": false
}
```

Here `{name}` is the **source** memory and `destination` in the body names the copy.

### Copy a Memory From the Client

```
POST /api/v1/stores/{store}/memories/{name}/copy-from-client
```

**Permission:** `copy`

Promotes an ephemeral memory the server has never seen: the client sends the artifact content plus
the metadata it already derived, and the server stores both verbatim, deriving nothing itself.

**`{name}` is the DESTINATION here — the opposite of `POST .../copy`, where it names the source.**
There is no server-side source to name: the whole reason this route exists is that the source is a
client-side ephemeral this process cannot see. The handler performs no name resolution against its
own store, so a persistent memory that happens to share the destination name is never read from —
only ever (optionally) overwritten.

**Request:**

```json
{
  "content": ["chunk one", "chunk two"],
  "entry": {
    "updatedAt": "2026-08-14T00:00:00Z",
    "description": "a curated preview",
    "attributes": { "file": ["src/A.cs"] },
    "keywords": ["kw"],
    "termFrequencies": { "kw": 2 },
    "chunkEntries": [{ "chunkIndex": 1, "contentPreview": "preview", "keywords": ["kw"] }],
    "fileHashes": { "src/A.cs": "deadbeef" },
    "agentKeywords": ["kw"]
  },
  "overwrite": false
}
```

`entry` carries exactly the metadata the server can neither derive from the artifact nor compute
from the destination name or the artifact — `name`, `qualifiedName`, `scope`, `chunkCount`,
`createdAt` and `originalBytes` are all deliberately absent (RULED 2026-08-14: `originalBytes` is
the sum of each `content` element's UTF-8 byte length, which the server computes itself), so none
of them can disagree with the request that carries them.

Every field in `entry` is bounded. This route is the only one that accepts these fields at all, so
nothing else bounds them. Two groups apply, because a top-level field can be curated by hand while
a chunk entry's is always auto-generated:

- **Top level** — matched to what a real local `remember()` can legitimately produce, so a bound
  here can never reject a memory the local path already accepted: `keywords`/`agentKeywords` to 30
  entries (the local merge ceiling); `description` to 50,000 characters and `termFrequencies` to
  8,324 terms (resource guards — no local ceiling exists for either, so these are sized to a value
  no realistic local memory could reach, not to a derivation constant).
- **Per chunk entry** (`chunkEntries[].contentPreview`/`.keywords`/`.termFrequencies`) — 500
  characters / 25 entries / 100 terms, matching the auto-derivation every chunk entry always goes
  through. `chunkEntries` itself carries at most one entry per `content` element, each one's
  `chunkIndex` addressing an actual element (1-based).

### Search

```
GET /api/v1/stores/{store}/search?q=authentication[&scopes=local&scopes=api][&limit=20][&excludeTopics=goal&excludeTopics=task][&pathPrefix=/api/]
```

**Permission:** `search`

- `q` (required) — the search query.
- `scopes` — optional repeated scope filter (`?scopes=a&scopes=b`), ASP.NET Core's native binding
  for a `string[]?` parameter. A plain `,` inside one value names a scope containing a literal
  comma, not two scopes — `?scopes=local,api` requests one scope named `local,api`. Bounded at 64
  entries / 1024 characters each. Omitted or empty means every readable scope.
- `limit` — max results, clamped to `[1, 1000]`, default 20.
- `excludeTopics` — optional repeated topic-name filter, same encoding as `scopes`, excluded from
  candidates and results before the top-K cut. Same 64/1024 bound, independent of `scopes`'.
- `pathPrefix` — optional case-insensitive prefix match on each result's qualified name,
  also applied before the top-K cut.

**Response (200):**

```json
[
  {
    "type": "entry",
    "name": "/api/auth-flow",
    "score": 85.5,
    "description": "OAuth2 authentication flow",
    "chunkIndex": null,
    "totalChunks": null,
    "originalBytes": 1234,
    "createdAt": "2026-03-01T10:00:00Z"
  }
]
```

Result types: `entry`, `chunk`, `topic`. `originalBytes` and `createdAt` are present for `entry`
and `chunk` results and `null` for `topic` results — a topic is a scope label, not a memory, and
has no real byte count or creation time.

#### POST form (attribute filtering)

`attributeFilter` is a dictionary of arrays, which does not fit a query string, so it is
only available via `POST /search`:

```
POST /api/v1/stores/{store}/search
```

**Permission:** `search`

**Request:**

```json
{
  "q": "authentication",
  "scopes": ["local", "api"],
  "limit": 20,
  "excludeTopics": ["goal", "task"],
  "pathPrefix": "/api/",
  "attributeFilter": { "tag": ["priority"] }
}
```

`scopes`/`excludeTopics` are JSON arrays here, mirroring the GET route's repeated query params —
this is what lets either one express a scope or topic name containing a literal comma, which a
comma-joined string could never distinguish from two names. `attributeFilter` matching is AND
across keys, OR within each key's values, case-insensitive; it excludes topics, which carry no
attributes. `attributeFilter` may not exceed 64 keys. Response shape is identical to `GET /search`.

### Export Topics

```
POST /api/v1/stores/{store}/export
```

**Permission:** `export`

**Request:**

```json
{
  "topics": ["api", "arch"],
  "filename": "project-knowledge"
}
```

**Response:** `.scrinia-bundle` file download.

### Import Bundle

```
POST /api/v1/stores/{store}/import
```

**Permission:** `import`. Accepts multipart form upload of a `.scrinia-bundle` file with optional topic filter and overwrite flag.

### Embeddings Status

```
GET /api/v1/stores/{store}/embeddings/status
```

**Permission:** `read`. Returns the active provider, its hardware (`CPU` or `Vulkan GPU`),
availability, vector dimensionality, the semantic weight applied in hybrid scoring, and the
total stored vector count. When no provider is installed and no plugin is loaded, returns a
`"none"` provider rather than an error.

```json
{
  "provider": "Model2VecProvider",
  "hardware": "CPU",
  "available": true,
  "dimensions": 384,
  "semanticWeight": 50.0,
  "vectorCount": 1284
}
```

### Embeddings Settings

```
GET /api/v1/stores/{store}/embeddings/settings
```

**Permission:** `read`. Read-only — embedding configuration comes from `Scrinia:Embeddings:*`
and takes effect on restart. There is no `PUT`.

```json
{ "provider": "Model2VecProvider", "hardware": "CPU", "semanticWeight": 50.0 }
```

### Reindex Embeddings

```
POST /api/v1/stores/{store}/embeddings/reindex
```

**Permission:** `reindex`, plus a writable grant on `{store}`. Re-embeds every memory in the
store through the active provider, replacing each memory's vectors atomically. This is the
manual migration path after a provider switch or a chunk-config change — see "Vector data
isolation" below. Returns `400` when no provider is available.

```json
{ "message": "Reindexed 1284 memories." }
```

These three routes were previously mounted under `/api/v1/plugins/embeddings/`, where the
handler had no `{store}` in scope and the reindex route failed on every request. The old paths
are gone.

## Key Management API

Requires the `manage_keys` permission.

### Create Key

```
POST /api/v1/keys
```

**Request:**

```json
{
  "userId": "dev-team",
  "stores": ["default", "shared"],
  "permissions": ["read", "search", "store", "append"],
  "label": "Development team key"
}
```

**Response (201):**

```json
{
  "rawKey": "scri_abc123...",
  "keyId": "a1b2c3d4e5f6g7h8",
  "userId": "dev-team"
}
```

The `rawKey` is shown exactly once. Store it securely.

### List Keys

```
GET /api/v1/keys
```

**Response (200):** Array of key summaries (id, userId, stores, permissions, label, dates, revoked status). Raw keys are never returned.

### Get Key Details

```
GET /api/v1/keys/{keyId}
```

### Revoke Key

```
DELETE /api/v1/keys/{keyId}
```

Soft-deletes the key (sets `revoked = true`). Revoked keys immediately stop authenticating.

## MCP over HTTP

The server exposes MCP Streamable HTTP at `/mcp`. This allows MCP clients that support HTTP transport to connect directly to the server without the CLI.

All of Scrinia's MCP tools (`guide` plus the per-verb memory tools and `facts`) are available through this endpoint, authenticated with the same API key scheme.

## Health Endpoints

| Endpoint | Purpose | Auth Required |
|----------|---------|---------------|
| `GET /health/live` | Liveness probe (always 200) | No |
| `GET /health/ready` | Readiness probe — status only (503 if degraded) | No |
| `GET /health` | Backward-compatible alias for ready | No |
| `GET /health/details` | Detailed diagnostics (store names, plugins, backend) | Yes |

Unauthenticated health endpoints return status only (`ok` or `degraded`). The `/health/details` endpoint requires authentication and returns the full check array including store names, plugin names, and backend info.

### Agent Chat

| Endpoint | Purpose | Auth Required |
|----------|---------|---------------|
| `POST /api/v1/stores/{store}/chat` | Stream an agent chat response (SSE) | Yes (`chat` permission) |
| `GET /api/v1/stores/{store}/chat/providers` | List configured LLM providers | Yes (`chat` permission) |

The chat endpoint runs a server-side agent that can search and recall memories from the selected store. Responses stream as Server-Sent Events (SSE) with event types: `chunk` (text), `tool-start`, `tool-result`, `done`, `error`.

**Request body** (POST):
```json
{
  "messages": [
    { "role": "user", "content": "What do we know about auth?" }
  ],
  "provider": "anthropic"
}
```

If `provider` is omitted, the first available provider is used. Returns 503 if no providers are configured.

**Configuration** — add to `Scrinia:Chat` in appsettings.json:

```json
"Chat": {
  "Providers": "anthropic,openai,gemini",
  "AnthropicApiKey": "sk-ant-...",
  "AnthropicModel": "claude-sonnet-4-20250514",
  "OpenAiApiKey": "sk-...",
  "OpenAiModel": "gpt-4o-mini",
  "GeminiApiKey": "...",
  "GeminiModel": "gemini-2.0-flash",
  "MaxTokens": 4096,
  "Temperature": 0.7
}
```

Only providers with API keys configured are available. Set `"Providers": "none"` to disable the feature entirely.

### Resilience Configuration

Both the Chat and Embeddings sections support resilience properties for transient failure handling. These control retry behavior and circuit breaker thresholds for API-based providers:

| Property | Default | Description |
|----------|---------|-------------|
| `MaxRetries` | `3` | Maximum retry attempts for transient failures (0 = no retries, max 10) |
| `RetryBaseDelayMs` | `200` | Base delay in milliseconds for exponential backoff with jitter |
| `CircuitBreakerThreshold` | `5` | Consecutive failures before the circuit breaker opens |
| `CircuitBreakerCooldownSeconds` | `30` | Seconds to wait before transitioning from open to half-open |

Example:

```json
"Chat": {
  "Providers": "anthropic",
  "AnthropicApiKey": "sk-ant-...",
  "MaxRetries": 5,
  "RetryBaseDelayMs": 500,
  "CircuitBreakerThreshold": 3,
  "CircuitBreakerCooldownSeconds": 60
},
"Embeddings": {
  "Provider": "openai",
  "OpenAiApiKey": "sk-...",
  "MaxRetries": 3,
  "RetryBaseDelayMs": 200,
  "CircuitBreakerThreshold": 5,
  "CircuitBreakerCooldownSeconds": 30
}
```

Circuit breaker state is visible via the `/health/details` endpoint (requires `health` permission).

## Web UI

The server includes a built-in React web UI served from `/`. Pages:

| Page | Purpose |
|------|---------|
| Login | API key authentication |
| Dashboard | Overview of stores and memory counts |
| Memory Browser | Browse, search, and filter all memories |
| Memory Detail | View full memory content with chunk navigation |
| Key Management | Create and revoke API keys |
| Settings | Workspace configuration |

### Building the Web UI

For development:

```bash
cd web
npm install
npm run dev    # Vite dev server on :5173, proxies API to :5000
```

For production, the web UI is built into `src/Scrinia.Server/wwwroot/` and served as static files:

```bash
cd web
npm ci
npm run build  # Outputs to ../src/Scrinia.Server/wwwroot/
```

## API Documentation

The server exposes an OpenAPI spec at `/openapi/v1.json` and a Scalar API explorer at `/scalar`.

## Server Configuration

Configuration via `appsettings.json` or environment variables:

| Setting | Env Var | Default | Description |
|---------|---------|---------|-------------|
| `Scrinia:DataDir` | `Scrinia__DataDir` | `%LOCALAPPDATA%/scrinium` | Root data directory |
| `Scrinia:CorsOrigins` | `Scrinia__CorsOrigins` | Deny all (empty) | Allowed CORS origins (`["*"]` for allow-all) |
| `Scrinia:Stores` | (section) | `{"default": ""}` | Named stores with custom paths |
| `Scrinia:RateLimit:PermitLimit` | `Scrinia__RateLimit__PermitLimit` | `100` | Requests per window per user/IP |
| `Scrinia:RateLimit:WindowSeconds` | `Scrinia__RateLimit__WindowSeconds` | `60` | Rate limit window in seconds |
| `Scrinia:Chat:Providers` | `Scrinia__Chat__Providers` | `none` | Comma-separated LLM providers (anthropic,openai,gemini) |
| `Scrinia:Chat:AnthropicApiKey` | `Scrinia__Chat__AnthropicApiKey` | (none) | Anthropic API key |
| `Scrinia:Chat:OpenAiApiKey` | `Scrinia__Chat__OpenAiApiKey` | (none) | OpenAI API key |
| `Scrinia:Chat:GeminiApiKey` | `Scrinia__Chat__GeminiApiKey` | (none) | Google Gemini API key |

### Multi-Store Configuration

Map store names to filesystem paths. Empty string defaults to `{dataDir}/stores/{name}`:

```json
{
  "Scrinia": {
    "DataDir": "/var/lib/scrinia",
    "Stores": {
      "default": "",
      "projects": "/mnt/shared/scrinia-projects",
      "archive": "/mnt/archive/scrinia"
    }
  }
}
```

### Request Limits

| Limit | Value |
|-------|-------|
| Max request body | 10 MB |
| Max multipart form (bundle import) | 50 MB |
| Rate limit | 100 requests/minute (sliding window) |
| Memory name max length | 256 characters |
| Per content element max | 5 MB |
| Search query max length | 10 KB |
| `scopes` / `excludeTopics` (search, `/memories`) | ≤64 entries, ≤1024 characters each |
| Search limit range | 1–1000 (clamped) |

## Security Hardening

The server includes production-ready security features:

- **HTTPS/HSTS** enforcement in production
- **Security headers**: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-XSS-Protection: 0`
- **Rate limiting**: 100 requests/minute sliding window on `/api/v1/*`
- **Request size limits**: 10 MB body, 50 MB multipart
- **Input validation**: 256-char names, 5 MB content elements
- **Global exception handler**: Sanitized JSON error responses (no stack traces)
- **API key hashing**: Only SHA-256 hashes stored, raw keys shown once
- **Privilege escalation prevention**: Can't grant permissions you don't have
- **Graceful shutdown**: Clean resource disposal on SIGTERM

## Data Directory Layout

```
{dataDir}/
  stores/
    default/            Store "default" (.scrinia/ workspace)
    projects/           Store "projects" (or custom path)
  plugins/              Server plugin DLLs
  scrinia-keys.db       SQLite database for API keys
  BOOTSTRAP_KEY         Bootstrap API key (created on first run)
```

Each store is a full `.scrinia/` workspace with its own index, artifacts, topics, embeddings, and exports.

**Vector data isolation:** Embedding vectors are stored per-store in `.scrinia/embeddings/` within each store's workspace directory — never shared across stores. The Model2Vec model binary lives at `{exeDir}/models/m2v-MiniLM-L6-v2/` (shared, read-only). Both the built-in Model2Vec and optional Vulkan plugin produce 384-dim vectors in the same MiniLM embedding space, but they report different embedding signatures (`model2vec:m2v-MiniLM-L6-v2` vs `vulkan:{ggufFileName}`) and write to the same per-store `.scrinia/embeddings` directory — so **switching providers does require a reindex**. The vector files self-quarantine on the first load after a switch; rebuild each store with `POST /api/v1/stores/{store}/embeddings/reindex`. Until then those memories fall back to keyword-only search.

### Version Retention

There is **no limit on the number of memories** you can store, and the currently-active body of every memory is preserved indefinitely. The retention discussed here is the size of the **rollback history** kept beside each memory — used for recovery after an accidental overwrite, not for steady-state reads.

Every overwrite / append to a memory `foo` copies the *prior* body to a sibling `versions/` directory as `foo_{YYYYMMDD-HHmmss}.{ext}` before the new body is written. The active file (`foo.nmp2`, `foo.md`) is never touched by the retention logic.

| Memory class | Active file (kept forever) | Rollback history location | History size |
|---|---|---|---|
| NMP/2 memory body | `{scope}/{name}.nmp2` | `{scope}/versions/{name}_{ts}.nmp2` | Capped at 10 newest per memory (oldest archives auto-deleted on each overwrite). |
| Skill markdown (`/skill/*`) | `.scrinia/skills/{name}.md` | `.scrinia/skills/versions/{name}_{ts}.md` | Unbounded — every prior version is retained until you prune manually. |
| Agent config markdown (`/agent/*`) | `.scrinia/agent/{name}.md` | `.scrinia/agent/versions/{name}_{ts}.md` | Unbounded — every prior version is retained until you prune manually. |

The `versions/` directories are gitignored by default (`.scrinia/.gitignore` includes `**/versions/`) so they don't pollute commits. Bundles produced by `scri bundle export` / `bundle('export')` do not include `versions/`.

**Manual prune** for skill and agent rollback history (NMP/2 history is already self-managing):

```bash
# Keep the 5 newest skill rollback snapshots per skill name
for f in $(ls .scrinia/skills/versions/ | sed 's/_[0-9]*-[0-9]*\.md//' | sort -u); do
    ls -1t .scrinia/skills/versions/${f}_*.md | tail -n +6 | xargs -r rm
done
```

The equivalent for `agent/versions/` is the same with the directory swapped. Pruning rollback history is safe — only the currently-active file is read at load time; archived copies exist solely for human recovery.

## Server Plugins

Server plugins are .NET class libraries loaded at startup from `{dataDir}/plugins/`. They run in-process using isolated `AssemblyLoadContext`.

See [Embeddings Architecture](architecture/embeddings.md) for details on the built-in semantic search (Model2Vec in Core) and the optional Vulkan GPU plugin, and [Server Architecture](architecture/server.md) for the plugin loading system.
