# Scrinia Server

> HTTP API + MCP over HTTP + web UI for multi-user and remote scrinia access.

## Running

```bash
# Development
dotnet run --project src/Scrinia.Server      # API on :5000

# With web UI hot-reload
cd web && npm run dev &                      # Vite on :5173, proxies to :5000
dotnet run --project src/Scrinia.Server

# Production
cd web && npm run build                      # builds to Scrinia.Server/wwwroot/
dotnet run --project src/Scrinia.Server

# Docker
docker compose up -d
docker compose logs                          # shows bootstrap API key

# Aspire (development dashboard)
dotnet run --project src/Scrinia.AppHost
```

On first startup, a bootstrap admin API key is written to `BOOTSTRAP_KEY` in the data directory. Read it, then delete the file.

## Authentication

API keys use Bearer token authentication. Keys are SHA-256 hashed in SQLite. Each key is scoped to specific stores and granular permissions.

```bash
# Create a key
curl -X POST http://localhost:5000/api/v1/keys \
  -H "Authorization: Bearer $BOOTSTRAP_KEY" \
  -H "Content-Type: application/json" \
  -d '{"userId": "alice", "stores": ["default"], "permissions": ["read","search","store","append"]}'

# List keys
curl http://localhost:5000/api/v1/keys -H "Authorization: Bearer $KEY"

# Revoke a key
curl -X DELETE http://localhost:5000/api/v1/keys/{id} -H "Authorization: Bearer $KEY"
```

### Permissions

| Permission | Description |
|---|---|
| `read` | View memory content and chunks |
| `search` | Search and list memories |
| `store` | Create and overwrite memories |
| `append` | Append chunks to memories |
| `forget` | Delete memories |
| `copy` | Copy between scopes |
| `export` | Export topic bundles |
| `import` | Import bundles |
| `manage_keys` | API key CRUD |

## MCP over HTTP

MCP Streamable HTTP transport is available at `/mcp`:

```json
{
  "mcpServers": {
    "scrinia": {
      "url": "http://localhost:5000/mcp?store=default",
      "headers": { "Authorization": "Bearer YOUR_API_KEY" }
    }
  }
}
```

All 17 MCP tools are available. The `?store=` query parameter selects the target memory store.

## REST API

### Memory operations

| Method | Path | Permission | Description |
|---|---|---|---|
| POST | `/api/v1/stores/{store}/memories` | `store` | Store a memory |
| GET | `/api/v1/stores/{store}/memories` | `read` | List memories |
| GET | `/api/v1/stores/{store}/memories/{name}` | `read` | Show memory |
| DELETE | `/api/v1/stores/{store}/memories/{name}` | `forget` | Delete memory |
| POST | `/api/v1/stores/{store}/memories/{name}/append` | `append` | Append chunk |
| POST | `/api/v1/stores/{store}/memories/{name}/copy` | `copy` | Copy memory |
| GET | `/api/v1/stores/{store}/memories/{name}/chunks/{i}` | `read` | Get chunk |
| GET | `/api/v1/stores/{store}/search?q=...` | `search` | Search |
| POST | `/api/v1/stores/{store}/export` | `export` | Export topics |
| POST | `/api/v1/stores/{store}/import` | `import` | Import bundle |

### Key management

| Method | Path | Permission | Description |
|---|---|---|---|
| POST | `/api/v1/keys` | `manage_keys` | Create API key |
| GET | `/api/v1/keys` | `manage_keys` | List keys |
| DELETE | `/api/v1/keys/{id}` | `manage_keys` | Revoke key |

### Health and discovery

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/health`, `/health/live`, `/health/ready` | None | Health probes |
| GET | `/openapi/v1.json` | None | OpenAPI specification |
| GET | `/scalar/v1` | None | Interactive API explorer |

## Web UI

Available at the server root (`/`) when built:

- **Login** — API key entry
- **Dashboard** — health status and store overview
- **Memory Browser** — list, search, scope filtering
- **Memory Detail** — content view with chunk navigation
- **Key Management** — create, list, revoke API keys

Build the web UI:
```bash
cd web && npm ci && npm run build
```

## Configuration

| Setting | Env var | Default |
|---|---|---|
| `Scrinia:DataDir` | `Scrinia__DataDir` | `{LocalAppData}/scrinia-server` |
| `Scrinia:Stores:{name}` | `Scrinia__Stores__{name}` | `{DataDir}/stores/{name}` |
| `Scrinia:PluginsDir` | `Scrinia__PluginsDir` | `{DataDir}/plugins` |
| `Scrinia:CorsOrigins` | `Scrinia__CorsOrigins__0` etc. | `[]` (allows all) |

## Deployment

### Docker Compose

```bash
docker compose up -d
```

The included `docker-compose.yml` runs the server with a persistent data volume.

### Reverse proxy (nginx)

```nginx
server {
    listen 443 ssl;
    server_name scrinia.example.com;

    ssl_certificate /etc/letsencrypt/live/scrinia.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/scrinia.example.com/privkey.pem;

    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;
    }
}
```

### Reverse proxy (Caddy)

```
scrinia.example.com {
    reverse_proxy localhost:5000
}
```

## Security

The server includes production hardening:

- **HTTPS/HSTS** in production environments
- **Security headers** — `X-Content-Type-Options`, `X-Frame-Options`, strict referrer policy
- **Request size limits** — 10 MB body, 50 MB for bundle imports
- **Input validation** — 256 char name limit, 5 MB content limit
- **Rate limiting** — 100 req/min sliding window on API endpoints
- **Path traversal protection** — names sanitized to prevent directory escape
- **Error sanitization** — unhandled exceptions return generic 500 responses
- **Graceful shutdown** — clean resource disposal on SIGTERM

## Multi-store

The server supports multiple named memory stores, each backed by an independent `FileMemoryStore`. Configure additional stores via settings:

```json
{
  "Scrinia": {
    "Stores": {
      "default": "/data/stores/default",
      "team": "/data/stores/team"
    }
  }
}
```

API keys are scoped to specific stores. A key with access to `["default"]` cannot access the `team` store.
