# Scrinia Server Administration

Scrinia.Server is an ASP.NET Core HTTP API server providing multi-user, multi-store persistent memory with API key authentication, a REST API, MCP over HTTP, and a web UI.

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

Opens the Aspire dashboard with telemetry and orchestration for Scrinia.Server.

### Docker

```bash
docker compose up -d
```

The included `docker-compose.yml` maps port 8080, mounts a persistent data volume, and sets `Scrinia__DataDir=/data`.

```yaml
services:
  scrinia-server:
    build:
      context: .
      dockerfile: src/Scrinia.Server/Dockerfile
    ports:
      - "8080:8080"
    volumes:
      - scrinia-data:/data
    environment:
      - Scrinia__DataDir=/data
    restart: unless-stopped

volumes:
  scrinia-data:
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

On first startup (when no keys exist), the server auto-generates an admin key with all permissions for all stores. The raw key is written to `{dataDir}/BOOTSTRAP_KEY`. Use it to create scoped keys, then revoke the bootstrap key for security.

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

### Store a Memory

```
POST /api/v1/stores/{store}/memories
```

**Permission:** `store`

**Request:**

```json
{
  "name": "api:auth-flow",
  "content": ["Full text content here..."],
  "description": "OAuth2 authentication flow",
  "tags": ["auth", "oauth"],
  "keywords": ["token", "refresh"],
  "reviewAfter": "2026-06-01",
  "reviewWhen": "when auth system changes"
}
```

The `content` array controls chunking: a single element creates a single-chunk memory; multiple elements create independently retrievable chunks.

**Response (201):**

```json
{
  "name": "auth-flow",
  "qualifiedName": "api:auth-flow",
  "chunkCount": 1,
  "originalBytes": 1234,
  "message": "Stored api:auth-flow (1 chunk, 1234 bytes)"
}
```

### List Memories

```
GET /api/v1/stores/{store}/memories[?scopes=local,api]
```

**Permission:** `search`

**Response (200):**

```json
{
  "memories": [
    {
      "name": "auth-flow",
      "qualifiedName": "api:auth-flow",
      "scope": "local-topic:api",
      "chunkCount": 1,
      "originalBytes": 1234,
      "createdAt": "2026-03-01T10:00:00Z",
      "updatedAt": null,
      "description": "OAuth2 authentication flow",
      "tags": ["auth", "oauth"]
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
  "destination": "arch:auth-flow",
  "overwrite": false
}
```

### Search

```
GET /api/v1/stores/{store}/search?query=authentication[&scopes=local,api][&limit=20]
```

**Permission:** `search`

**Response (200):**

```json
[
  {
    "type": "entry",
    "name": "api:auth-flow",
    "score": 85.5,
    "description": "OAuth2 authentication flow",
    "chunkIndex": null,
    "totalChunks": null
  }
]
```

Result types: `entry`, `chunk`, `topic`.

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

All 18 MCP tools are available through this endpoint, authenticated with the same API key scheme.

## Health Endpoints

| Endpoint | Purpose | Auth Required |
|----------|---------|---------------|
| `GET /health/live` | Liveness probe (always 200) | No |
| `GET /health/ready` | Readiness probe (503 if degraded) | No |
| `GET /health` | Backward-compatible alias for ready | No |

Readiness checks: SQLite connectivity, storage backend availability, per-store health, loaded plugins.

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
| `Scrinia:DataDir` | `Scrinia__DataDir` | `%LOCALAPPDATA%/scrinia-server` | Root data directory |
| `Scrinia:CorsOrigins` | `Scrinia__CorsOrigins` | Allow any | Allowed CORS origins |
| `Scrinia:Stores` | (section) | `{"default": ""}` | Named stores with custom paths |

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

## Server Plugins

Server plugins are .NET class libraries loaded at startup from `{dataDir}/plugins/`. They run in-process using isolated `AssemblyLoadContext`.

See [Embeddings Architecture](architecture/embeddings.md) for details on the built-in embeddings plugin, and [Server Architecture](architecture/server.md) for the plugin loading system.
