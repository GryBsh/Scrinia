# Server Architecture

Scrinium is an ASP.NET Core minimal API application providing multi-user, multi-store persistent memory with API key authentication, REST endpoints, MCP over HTTP, a web UI, and an in-process plugin system.

## Project Layout

```
src/Scrinia.Server/
  Scrinia.Server.csproj     net10.0 web (refs Core + Mcp + Abstractions)
  Program.cs                Startup, middleware pipeline, endpoint mapping
  Auth/
    ApiKeyStore.cs          SQLite-backed API key storage
    ApiKeyAuthHandler.cs    ASP.NET Core auth handler
    ApiKeyOptions.cs        Auth scheme options
    RequestContext.cs        Per-request scoped context
  Endpoints/
    MemoryEndpoints.cs      10 memory operation endpoints
    KeyEndpoints.cs         4 key management endpoints
    HealthEndpoints.cs      3 health probe endpoints
  Services/
    StoreManager.cs         Multi-store factory + cache
    MemoryOrchestrator.cs   Stateless business logic
    BundleService.cs        Export/import operations
    PluginLoader.cs         Plugin DLL discovery + loading
    PluginPipeline.cs       Before/after hook dispatch
  Models/
    ApiDtos.cs              Request/response DTOs
    ServerJsonContext.cs     Source-gen JSON for all API types
  Middleware/
    RequestTimingMiddleware.cs  Request logging
  wwwroot/                  Built React SPA assets
```

## Startup Sequence

`Program.cs` configures the server in this order:

1. **Kestrel limits**: 10 MB max request body, 50 MB max multipart form
2. **Data directory resolution**: `Scrinia:DataDir` config or `%LOCALAPPDATA%/scrinium`
3. **Store definitions**: Read `Scrinia:Stores` section (name-to-path mapping)
4. **Plugin loading**: Scan `{dataDir}/plugins/*.dll` via `PluginLoader`
5. **Service registration**: `ApiKeyStore` (singleton, SQLite), `StoreManager` (singleton), `PluginPipeline` (singleton), `IStorageBackend` (singleton, `FilesystemBackend`)
6. **Authentication**: API key scheme via `ApiKeyAuthHandler`, `ManageKeys` authorization policy
7. **CORS**: Configurable origins from `Scrinia:CorsOrigins`
8. **Rate limiting**: Sliding window (100/min, 6 segments) on `/api/v1/*`
9. **MCP over HTTP**: Streamable HTTP at `/mcp` endpoint
10. **Plugin services**: Each plugin's `ConfigureServices` called
11. **Bootstrap key**: If no keys exist, create admin key and write to `BOOTSTRAP_KEY` file

## Middleware Pipeline

Requests flow through middleware in this order:

```
1. Global Exception Handler       Catches unhandled exceptions, returns sanitized JSON
2. HSTS + HTTPS Redirect          Production only
3. Security Headers               X-Content-Type-Options, X-Frame-Options, etc.
4. Static Files                   Web UI assets (no auth required)
5. RequestTimingMiddleware         Logs method, path, status code, elapsed time
6. CORS                           Cross-origin policy
7. Rate Limiter                   100/min sliding window
8. Authentication                 API key validation
9. Authorization                  Permission checks
10. RequestContext Population      Resolve user, stores, permissions, active store
11. Plugin Middleware              Each plugin's ConfigureMiddleware hook
12. Endpoints                     REST API + MCP
13. SPA Fallback                  index.html for React Router client-side routing
```

## Authentication

### ApiKeyAuthHandler

Custom `AuthenticationHandler<ApiKeyOptions>` that:

1. Extracts Bearer token from `Authorization` header
2. Computes SHA-256 hash of the raw token
3. Queries SQLite: lookup by hash, verify not revoked
4. Populates `ClaimsPrincipal` with:
   - `NameIdentifier` claim (userId)
   - Store access claims
   - Permission claims
5. Updates `last_used_at` timestamp

### ApiKeyStore

SQLite-backed key storage at `{dataDir}/scrinia-keys.db`.

**Schema:**

```sql
CREATE TABLE api_keys (
    id           TEXT PRIMARY KEY,
    key_hash     TEXT NOT NULL UNIQUE,
    user_id      TEXT NOT NULL,
    permissions  TEXT NOT NULL DEFAULT '[]',
    label        TEXT,
    created_at   TEXT NOT NULL,
    last_used_at TEXT,
    revoked      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE key_stores (
    key_id     TEXT NOT NULL REFERENCES api_keys(id) ON DELETE CASCADE,
    store_name TEXT NOT NULL,
    PRIMARY KEY (key_id, store_name)
);
```

**Key format:** `scri_` + 32 random bytes (Base64url, no padding)
**Key IDs:** First 16 characters of `Guid.NewGuid().ToString("N")`

### RequestContext

Per-request scoped service populated after authentication:

```csharp
public sealed class RequestContext
{
    public string UserId { get; set; }
    public string[] Stores { get; set; }
    public string[] Permissions { get; set; }
    public string? ActiveStore { get; set; }
    public IMemoryStore? Store { get; set; }

    public bool HasPermission(string permission);
    public bool CanAccessStore(string store);       // checks for store name or "*" wildcard
    public string GetStoreAccessLevel(string store); // "read-only" or "read-write"
}
```

The `StoreAccessLevels` and `GetStoreAccessLevel()` are integration points for the separate auth plugin (Scrinia.Plugin.Auth), which can set per-store SSO-based access levels.

## Multi-Store Architecture

### StoreManager

Singleton service that manages named `FileMemoryStore` instances:

```csharp
public sealed class StoreManager
{
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores;
    private readonly Dictionary<string, string> _storeConfig;  // name → path
    private readonly IStorageBackend _backend;

    public IMemoryStore GetStore(string storeName);
    public bool StoreExists(string storeName);
    public IReadOnlyCollection<string> StoreNames { get; }
}
```

Stores are lazily created on first access. Each store is an independent `FileMemoryStore` with its own workspace directory, index, topics, and embeddings data.

### Store Resolution Per Request

1. Extract `{store}` from URL path parameter
2. Check `RequestContext.CanAccessStore(store)`
3. Resolve via `StoreManager.GetStore(store)`
4. Set `RequestContext.Store` for use by endpoints

## Endpoints

### MemoryEndpoints

All memory endpoints require store resolution and appropriate permissions. They delegate to `PluginPipeline` (which wraps `MemoryOrchestrator`) for hook integration.

| Method | Path | Permission |
|--------|------|------------|
| POST | `/memories` | `store` |
| GET | `/memories` | `search` |
| GET | `/memories/{name}` | `read` |
| DELETE | `/memories/{name}` | `forget` |
| POST | `/memories/{name}/append` | `append` |
| POST | `/memories/{name}/copy` | `copy` |
| GET | `/memories/{name}/chunks/{i}` | `read` |
| GET | `/search` | `search` |
| POST | `/export` | `export` |
| POST | `/import` | `import` |

### KeyEndpoints

Key management requires the `manage_keys` permission.

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/keys` | Create key (with escalation prevention) |
| GET | `/keys` | List all keys |
| GET | `/keys/{keyId}` | Get key details |
| DELETE | `/keys/{keyId}` | Revoke key |

### HealthEndpoints

No authentication required.

| Path | Purpose |
|------|---------|
| `/health/live` | Always 200 (liveness) |
| `/health/ready` | Checks SQLite, storage, stores, plugins (readiness) |
| `/health` | Alias for `/health/ready` |

## Services

### MemoryOrchestrator

Static class with stateless business logic for all memory operations:

- `StoreAsync` -- Compress content, extract keywords, compute TF, persist, index
- `AppendAsync` -- Append chunk, recompute keywords/TF
- `ShowAsync` -- Read and decode artifact
- `ForgetAsync` -- Delete artifact and index entry
- `CopyAsync` -- Copy between scopes
- `SearchAsync` -- Hybrid search with optional plugin scores
- `ExportAsync`, `ImportAsync` -- Bundle operations

Text analysis on store/append:
1. Extract keywords from content (top 25 by frequency)
2. Merge with user-provided keywords (agent keywords get +5 TF boost, auto +2)
3. Compute term frequencies
4. Generate content preview (first 500 chars)
5. Generate chunk previews for multi-chunk artifacts

### PluginPipeline

Wraps `MemoryOrchestrator` with before/after hook invocation from loaded plugins:

```csharp
public sealed class PluginPipeline
{
    private readonly IReadOnlyList<IMemoryOperationHook> _hooks;  // sorted by Order

    // Each method: run before hooks → execute operation → run after hooks
    public async Task<StoreResponse> StoreAsync(IMemoryStore store, StoreRequest req);
    public async Task<AppendResponse> AppendAsync(IMemoryStore store, string name, string content);
    public async Task ForgetAsync(IMemoryStore store, string name);
}
```

Before-hooks can cancel operations by setting `Cancel = true` with a `CancelReason`, which returns 409 Conflict to the client.

### PluginLoader

Discovers and loads plugin DLLs from `{dataDir}/plugins/`:

1. Scan for `*.dll` files
2. Each DLL gets an isolated `AssemblyLoadContext` (PluginAssemblyLoadContext)
3. ALC falls back to Default for shared assemblies (Scrinia.*, Microsoft.AspNetCore.*, System.*)
4. Activate all types implementing `IScriniaPlugin` (non-abstract, non-interface)
5. Sort by `Order` property (ascending)

Failures are logged and skipped -- a broken plugin doesn't prevent server startup.

## Plugin System

### IScriniaPlugin Interface

```csharp
public interface IScriniaPlugin
{
    string Name { get; }
    string Version { get; }
    int Order { get; }  // lower runs first

    void ConfigureServices(IServiceCollection services, IConfiguration config);
    void ConfigureMiddleware(IApplicationBuilder app);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
```

### IMemoryOperationHook

```csharp
public interface IMemoryOperationHook
{
    Task OnBeforeStoreAsync(BeforeStoreContext ctx);
    Task OnAfterStoreAsync(AfterStoreContext ctx);
    Task OnBeforeAppendAsync(BeforeAppendContext ctx);
    Task OnAfterAppendAsync(AfterAppendContext ctx);
    Task OnBeforeForgetAsync(BeforeForgetContext ctx);
    Task OnAfterForgetAsync(AfterForgetContext ctx);
}
```

`ScriniaPluginBase` provides virtual empty defaults for all hooks.

Hook contexts include:
- `BeforeStoreContext`: Name, Content, Description, Tags, Keywords, Cancel, CancelReason
- `AfterStoreContext`: Name, Content, QualifiedName, ChunkCount, OriginalBytes
- `BeforeAppendContext`: Name, Content, Cancel, CancelReason
- `AfterAppendContext`: Name, Content, QualifiedName, ChunkCount
- `BeforeForgetContext`: Name, Cancel, CancelReason
- `AfterForgetContext`: Name, WasDeleted

### Plugin Integration Points

A server plugin can integrate at 8 points:

1. **ConfigureServices** -- Register DI services
2. **ConfigureMiddleware** -- Add middleware to the pipeline
3. **MapEndpoints** -- Add custom REST endpoints
4. **OnBeforeStore/Append/Forget** -- Intercept and optionally cancel operations
5. **OnAfterStore/Append/Forget** -- React to completed operations
6. **ISearchScoreContributor** -- Provide supplemental search scores
7. **IMemoryEventSink** -- Receive event notifications (MCP path)
8. **StoreAccessLevels** in RequestContext -- SSO integration for per-store access control

## MCP over HTTP

The server exposes MCP Streamable HTTP at `/mcp` via `ModelContextProtocol.AspNetCore`:

```csharp
app.MapMcp("/mcp").RequireAuthorization();
```

All 30 MCP tools from `ScriniaMcpTools` (18 memory) and `ScriniaProjectTools` (12 planning) are available. The MCP path uses `IMemoryEventSink` for event hooks (not `IMemoryOperationHook`) to avoid double-firing.

## Web UI Integration

The React SPA is built into `wwwroot/` and served as static files. A SPA fallback serves `index.html` for all unmatched routes, enabling React Router client-side navigation.

The Vite build targets `../src/Scrinia.Server/wwwroot/` as its output directory. During development, Vite's dev server proxies API calls to `http://localhost:5000`.

## API Documentation

The server generates an OpenAPI spec at `/openapi/v1.json` and hosts a Scalar API explorer at `/scalar/v1`.

## Test Coverage

53 tests in `Scrinia.Server.Tests` using:
- `WebApplicationFactory` for in-process HTTP testing
- In-memory SQLite for key storage
- FluentAssertions for readable assertions

Tests cover all endpoints, authentication, authorization, key management, health probes, and error handling.
