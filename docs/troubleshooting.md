# Troubleshooting

Common issues and solutions for the Scrinia CLI, server, and plugins.

## CLI Issues

### "No .scrinia/ directory found"

The CLI walks up from the current directory looking for `.scrinia/`. If not found, it uses `cwd` as the workspace root and creates `.scrinia/` there.

**Fix:** Run `scri` from within your project directory, or use `--workspace-root /path/to/project`.

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

## Plugin Issues

### Vulkan plugin not loading

Symptoms: Semantic search works but uses CPU (Model2Vec) instead of GPU.

**Checklist:**
1. Plugin binary exists at `{exeDir}/plugins/scri-plugin-embeddings[.exe]`
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

Plugin discovery looks for executables at `{exeDir}/plugins/scri-plugin-{name}[.exe]`. The plugin name comes from the `plugins:embeddings` config setting (default: `scri-plugin-embeddings`).

**Fix:**
```bash
scri config plugins:embeddings    # Check current setting
ls $(dirname $(which scri))/plugins/  # List discovered plugins
```

## Server Issues

### Bootstrap key not working

On first startup, the server writes a bootstrap key to `{dataDir}/BOOTSTRAP_KEY`. This key has full permissions for all stores.

**Checklist:**
1. Read the key from the file (not the server logs — it's never logged)
2. Use it as `Authorization: Bearer {key}` (include the `scri_` prefix)
3. Key may have been revoked if you created scoped keys and revoked it

**Fix:** If the bootstrap key was revoked and you have no other admin keys, delete `{dataDir}/scrinia-keys.db` to reset. The server will create a new bootstrap key on next startup.

### "Address already in use" / Port conflict

The server defaults to port 5000.

**Fix:**
```bash
# Use a different port
dotnet run --project src/Scrinia.Server --urls http://localhost:5001
```

Or set in `appsettings.json`:
```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://localhost:5001" }
    }
  }
}
```

### SQLite "database is locked"

The API key database (`scrinia-keys.db`) uses SQLite. Lock errors can occur if multiple server instances access the same data directory.

**Fix:** Ensure only one server instance runs per data directory. For high-availability, use separate data directories or a reverse proxy with sticky sessions.

### Health check returning 503

`GET /health/ready` checks SQLite connectivity, storage backend availability, per-store health, and loaded plugins.

**Diagnostic:** The response body includes per-check results:
```json
{
  "status": "unhealthy",
  "checks": [
    { "name": "sqlite", "status": "healthy" },
    { "name": "store:default", "status": "unhealthy", "error": "..." }
  ]
}
```

**Fix:** Address the specific failing check. Common causes:
- Store path doesn't exist or has wrong permissions
- SQLite file corrupted (delete and restart for fresh bootstrap)
- Plugin failed to load (check `{dataDir}/plugins/` DLLs)

### Web UI shows blank page

The React SPA must be built into `src/Scrinia.Server/wwwroot/`:

```bash
cd web && npm ci && npm run build
```

If `wwwroot/index.html` doesn't exist, the MSBuild target auto-runs `npm ci && npm run build` during `dotnet build`. Check that Node.js is installed.

## Memory Issues

### Version bloat in .scrinia/store/versions/

Every memory overwrite archives the previous version. For frequently updated memories, this can accumulate.

**Fix:** Periodically clean old versions:
```bash
# Remove versions older than 30 days
find .scrinia/store/versions/ -name "*.nmp2" -mtime +30 -delete
find .scrinia/topics/*/versions/ -name "*.nmp2" -mtime +30 -delete
```

Note: `task_complete` and `project:state` updates skip archiving by design to prevent this issue for planning data.

### Search returns unexpected results

Scrinia uses hybrid scoring: `fieldScore + bm25*5 + semanticScore`. If results seem wrong:

1. Check if embeddings are active: `scri setup` (downloads model if missing)
2. Keywords matter: agent-provided keywords get +5 TF boost, auto-extracted get +2
3. Try `list(mode="full")` to see all entries with their keywords

### Cross-process conflicts

Multiple CLI processes or a CLI and server accessing the same workspace use OS-enforced file locks (`.lock` files per scope). If you see stale `.lock` files after a crash:

**Fix:** Delete `.lock` files in `.scrinia/store/` and `.scrinia/topics/*/`. They are recreated automatically.

## Docker Issues

### Container can't write to data volume

**Fix:** Ensure the volume mount has correct permissions:
```bash
docker compose down
docker volume rm scrinium-data
docker compose up -d
```

### Container health check failing

The Docker health check hits `/health/live`. If it fails, the container may not have started correctly.

**Fix:** Check logs:
```bash
docker compose logs scrinium
```

Common causes: missing environment variables, port conflicts within Docker network.
