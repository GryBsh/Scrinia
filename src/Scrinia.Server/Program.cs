using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Scalar.AspNetCore;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Resilience;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Scrinia.Plugin.Abstractions;
using Scrinia.Server.Auth;
using Scrinia.Server.Endpoints;
using Scrinia.Server.Middleware;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Request size limits ─────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB default
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(opt =>
{
    opt.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50 MB for bundle imports
});

// ── Configuration ────────────────────────────────────────────────────────────

var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var defaultPath = Path.Combine(localAppData, "scrinium");
string dataDir = builder.Configuration["Scrinia:DataDir"] ?? string.Empty;

if (string.IsNullOrWhiteSpace(dataDir))
{
    // Migrate from pre-rename data directory if it exists and the new one doesn't
    var legacyPath = Path.Combine(localAppData, "scrinia-server");
    dataDir = !Directory.Exists(defaultPath) && Directory.Exists(legacyPath) ? legacyPath : defaultPath;
}
    

dataDir = Path.GetFullPath(dataDir);
Directory.CreateDirectory(dataDir);

// Read store definitions: name → path. Empty path defaults to {dataDir}/stores/{name}.
var storesSection = builder.Configuration.GetSection("Scrinia:Stores");
var storePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

foreach (var child in storesSection.GetChildren())
{
    string storeName = child.Key;
    string storePath = child.Value ?? "";
    if (string.IsNullOrWhiteSpace(storePath))
        storePath = Path.Combine(dataDir, "stores", storeName);
    storePaths[storeName] = Path.GetFullPath(storePath);
}

// Ensure at least a "default" store
if (storePaths.Count == 0)
    storePaths["default"] = Path.Combine(dataDir, "stores", "default");

// ── Plugins ──────────────────────────────────────────────────────────────────

string pluginsDir = builder.Configuration["Scrinia:PluginsDir"]
    ?? Path.Combine(dataDir, "plugins");
pluginsDir = Path.GetFullPath(pluginsDir);
Directory.CreateDirectory(pluginsDir);

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var bootLogger = loggerFactory.CreateLogger("Scrinia.Server");
Scrinia.Core.FileLock.DefaultLogger = loggerFactory.CreateLogger("Scrinia.FileLock");

// Only load plugins when Vulkan embeddings are explicitly enabled
bool vulkanEnabled = string.Equals(
    builder.Configuration["Scrinia:Embeddings:Vulkan"], "true",
    StringComparison.OrdinalIgnoreCase);
var loadedPlugins = vulkanEnabled
    ? new PluginLoader().LoadPlugins(pluginsDir, bootLogger)
    : (IReadOnlyList<IScriniaPlugin>)[];
if (!vulkanEnabled)
    bootLogger.LogInformation("Vulkan embeddings disabled (set Scrinia__Embeddings__Vulkan=true to enable)");

// ── Services ─────────────────────────────────────────────────────────────────

var keyStore = new ApiKeyStore(Path.Combine(dataDir, "scrinia-keys.db"));

builder.Services.AddSingleton(keyStore);
builder.Services.AddSingleton<IStorageBackend, FilesystemBackend>();
builder.Services.AddScoped<RequestContext>();

// Maintenance takes no dependencies and has nothing to do with embeddings. It used to be
// registered inside the embeddings block, so ref-linking and orphan marking silently vanished
// whenever the model was missing, initialisation threw, or a plugin supplied embeddings — the
// last being a supported configuration, which meant installing a plugin turned off orphan
// marking. Registered here so none of those can reach it.
builder.Services.AddSingleton<IMemoryEventSink>(_ => new MaintenanceEventSink());

// Carries the registered IMemoryEventSink set onto the REST path, so a REST write gets the same
// ref-linking / orphan-marking / entity-typing treatment as the identical write through MCP. See
// EventSinkOperationHook's doc comment for why the exclusion filter inside it is load-bearing.
builder.Services.AddSingleton<IMemoryOperationHook>(sp =>
    new EventSinkOperationHook(sp.GetServices<IMemoryEventSink>()));

// Plugin DI — runs before StoreManager is constructed so plugins can replace IStorageBackend
foreach (var plugin in loadedPlugins)
{
    plugin.ConfigureServices(builder.Services, builder.Configuration);

    if (plugin is IMemoryOperationHook hook)
        builder.Services.AddSingleton(hook);
}

// Built-in Model2Vec embeddings — registered unless a plugin already provides ISearchScoreContributor
bool pluginProvidesEmbeddings = loadedPlugins.Any(p => p is ISearchScoreContributor);
if (!pluginProvidesEmbeddings)
{
    try
    {
        string modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
        string embeddingsDir = Path.Combine(dataDir, "embeddings");
        Directory.CreateDirectory(embeddingsDir);

        // EmbeddingOptions is bound via the standard IOptions<T> pipeline so binding errors
        // surface at startup. We resolve the value immediately here because the provider is
        // constructed before the host runs.
        builder.Services.AddOptions<EmbeddingOptions>()
            .Bind(builder.Configuration.GetSection("Scrinia:Embeddings"))
            .ValidateOnStart();
        var embeddingOptions = builder.Configuration.GetSection("Scrinia:Embeddings")
            .Get<EmbeddingOptions>() ?? new EmbeddingOptions();

        // The raw bind above has no guardrails of its own — an out-of-range MaxChunksPerMemory,
        // ChunkOverlap, or ChunkSize would otherwise reach BuiltInEmbeddingsService's
        // MemoryVectorIndexer ctor (a throw here would 500 every request from this DI factory) or
        // silently wipe every memory's vectors on write. NormalizeGuardrails is the one shared
        // rule the CLI already applies via WorkspaceSetup; the server logs what it corrects
        // instead of writing to stderr.
        foreach (string warning in embeddingOptions.NormalizeGuardrails())
            bootLogger.LogWarning("Scrinia:Embeddings config: {Warning}", warning);

        var embeddingProvider = EmbeddingProviderFactory.Create(
            embeddingOptions, modelsDir,
            loggerFactory.CreateLogger("Scrinia.Embeddings"));

        if (embeddingProvider.IsAvailable)
        {
            // Bake chunk params into the signature so changing ChunkSize / ChunkOverlap via
            // config triggers a quarantine + reindex on next launch (same flow as model switch).
            string signature = ChunkedSignature.Compose(
                embeddingProvider.Signature, embeddingOptions.ChunkSize, embeddingOptions.ChunkOverlap);
            var vectorStore = new VectorStore(embeddingsDir, signature);
            builder.Services.AddSingleton(embeddingProvider);
            builder.Services.AddSingleton(vectorStore);
            builder.Services.AddSingleton(sp =>
                new BuiltInEmbeddingsService(
                    sp.GetRequiredService<IEmbeddingProvider>(),
                    sp.GetRequiredService<VectorStore>(),
                    embeddingOptions.SemanticWeight,
                    sp.GetRequiredService<ILogger<BuiltInEmbeddingsService>>(),
                    embeddingOptions));
            // Compose embeddings semantic similarity with path-segment overlap. Path scoring
            // is dependency-free; embeddings remains the dominant signal. See
            // src/Scrinia.Core/Search/PathSegmentScoreContributor.cs for rationale.
            builder.Services.AddSingleton<ISearchScoreContributor>(sp =>
                new CompositeSearchScoreContributor(
                    sp.GetRequiredService<BuiltInEmbeddingsService>(),
                    new PathSegmentScoreContributor()));
            builder.Services.AddSingleton<IMemoryEventSink>(sp =>
                sp.GetRequiredService<BuiltInEmbeddingsService>());
            builder.Services.AddSingleton<IMemoryOperationHook>(sp =>
                sp.GetRequiredService<BuiltInEmbeddingsService>());
            builder.Services.AddSingleton<IEmbeddingsAdmin>(sp =>
                sp.GetRequiredService<BuiltInEmbeddingsService>());

            // Parity with the CLI, which has run this since WorkspaceSetup. Safe only because the
            // store-contract branch fixed remote listing truncation first: PromoteAsync reads the
            // existing typed vocabulary through GetEntityFactStore and PERSISTS its promotions, so
            // against a store that returned only the first 200 entries it wrote durable corruption.
            builder.Services.AddSingleton<IMemoryEventSink>(sp =>
                new EntityTypingEventHandler(sp.GetRequiredService<IEmbeddingProvider>()));

            bootLogger.LogInformation("Built-in embeddings ready (provider={Provider}, dims={Dims})",
                embeddingProvider.GetType().Name, embeddingProvider.Dimensions);

            // Stale-quarantine detection, report-only. The CLI reindexes automatically at this
            // point (WorkspaceSetup -> EmbeddingReindexer.ReindexIfStaleAsync), but a server
            // cannot: a full-corpus re-embed at boot is unbounded work that would hold up
            // startup with no progress channel, and this vector store is shared across every
            // store the server hosts, so there is no single correct thing to rebuild. Probing
            // still runs — it is what performs the quarantine rename, so search degrades to BM25
            // rather than scoring against vectors from a different provider — and the operator
            // gets the exact route to finish the migration.
            var staleScopes = EmbeddingReindexer.ProbeStaleScopes(vectorStore, embeddingsDir, bootLogger);
            if (staleScopes.Count > 0)
            {
                bootLogger.LogWarning(
                    "Embedding vectors for {Count} scope(s) were written under a different provider or "
                    + "chunk configuration and have been quarantined: {Scopes}. Semantic search is "
                    + "degraded to keyword-only for them until they are rebuilt. Rebuild with "
                    + "POST /api/v1/stores/{{store}}/embeddings/reindex (requires the 'reindex' "
                    + "permission), once per store.",
                    staleScopes.Count, string.Join(", ", staleScopes));
            }
        }
        else
        {
            bootLogger.LogInformation("Built-in embeddings not available (provider={Provider}). Run 'scri setup' to download the model.",
                embeddingProvider.GetType().Name);
            // Path-segment scoring still works without embeddings; register it so search
            // does more than raw BM25 when the embedding model isn't loaded.
            builder.Services.TryAddSingleton<ISearchScoreContributor>(_ => new PathSegmentScoreContributor());
        }
    }
    catch (Exception ex)
    {
        bootLogger.LogWarning(ex, "Failed to initialize built-in embeddings");
        builder.Services.TryAddSingleton<ISearchScoreContributor>(_ => new PathSegmentScoreContributor());
    }
}

// Chat providers (cloud LLM for agent chat). Bound via IOptions<T> with PostConfigure
// for the historical clamp/default rules — values are normalised before any consumer reads them.
builder.Services.AddOptions<Scrinia.Server.Chat.ChatOptions>()
    .Bind(builder.Configuration.GetSection("Scrinia:Chat"))
    .PostConfigure(opts =>
    {
        opts.Temperature = Math.Clamp(opts.Temperature, 0.0, 2.0);
        if (opts.MaxTokens <= 0) opts.MaxTokens = 4096;
    })
    .ValidateOnStart();
// Bridge: existing consumers (ChatProviderCache, MapChatEndpoints) take a raw ChatOptions.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<Scrinia.Server.Chat.ChatOptions>>().Value);
builder.Services.AddSingleton<Scrinia.Server.Chat.ChatProviderCache>();

// StoreManager uses factory delegate so IStorageBackend is resolved after plugins register
builder.Services.AddSingleton(sp =>
    new StoreManager(storePaths, sp.GetRequiredService<IStorageBackend>()));

builder.Services.AddSingleton<IReadOnlyList<IScriniaPlugin>>([.. loadedPlugins]);
builder.Services.AddSingleton<PluginPipeline>();

// Auth
builder.Services.AddAuthentication(Scrinia.Server.Auth.ApiKeyOptions.SchemeName)
    .AddScheme<Scrinia.Server.Auth.ApiKeyOptions, ApiKeyAuthHandler>(Scrinia.Server.Auth.ApiKeyOptions.SchemeName, _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("ManageKeys", policy =>
        policy.RequireClaim("permission", "manage_keys"))
    .AddPolicy("Health", policy =>
        policy.RequireClaim("permission", "health"))
    .AddPolicy("Chat", policy =>
        policy.RequireClaim("permission", Permissions.Chat));

// CORS — restrictive by default; only explicitly configured origins are allowed
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection("Scrinia:CorsOrigins").Get<string[]>();
        if (origins is { Length: > 0 })
        {
            if (origins.Length == 1 && origins[0] == "*")
                policy.AllowAnyOrigin();
            else
                policy.WithOrigins(origins);
        }
        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

// Rate Limiting — per-user (authenticated) or per-IP (unauthenticated)
var rlPermitLimit = builder.Configuration.GetValue("Scrinia:RateLimit:PermitLimit", 100);
var rlWindowSeconds = builder.Configuration.GetValue("Scrinia:RateLimit:WindowSeconds", 60);
if (rlPermitLimit <= 0) { bootLogger.LogWarning("Scrinia:RateLimit:PermitLimit must be > 0, using default (100)"); rlPermitLimit = 100; }
if (rlWindowSeconds <= 0) { bootLogger.LogWarning("Scrinia:RateLimit:WindowSeconds must be > 0, using default (60)"); rlWindowSeconds = 60; }
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("api", context =>
    {
        string partitionKey = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new()
        {
            PermitLimit = rlPermitLimit,
            Window = TimeSpan.FromSeconds(rlWindowSeconds),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
        });
    });
});

// OpenAPI
builder.Services.AddOpenApi();

// MCP over HTTP
string serverVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion.Split('+')[0] ?? "unknown";
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "scrinia", Version = serverVersion };
        // Read as server guidance by tools-only CLIs (Codex/Copilot); same baseline as AGENTS.md.
        options.ServerInstructions = ScriniaPrimer.McpInstructions;
        // Enforce per-operation permissions on the MCP surface (parity with the REST endpoints):
        // an incoming-message filter maps each tool/resource call to its permission and rejects
        // callers whose key lacks it. CLI stdio server registers no such filter.
        options.Filters.Message.IncomingFilters.Add(McpPermissionFilter.Filter);
    })
    .WithHttpTransport(options =>
    {
        options.PerSessionExecutionContext = true;
        options.ConfigureSessionOptions = (httpContext, serverOptions, ct) =>
        {
            // Resolve store from query param: /mcp?store=default
            var storeName = httpContext.Request.Query[StoreSelectorKey.Name].FirstOrDefault() ?? "default";
            var sm = httpContext.RequestServices.GetRequiredService<StoreManager>();
            if (!sm.StoreExists(storeName))
                throw new InvalidOperationException($"Store '{storeName}' not found.");
            var reqCtx = httpContext.RequestServices.GetRequiredService<RequestContext>();
            if (!reqCtx.CanAccessStore(storeName))
                throw new UnauthorizedAccessException($"Access denied to store '{storeName}'.");
            // Capture the caller's permissions for the per-op MCP auth filter. Must happen here:
            // reqCtx is the auth-populated request-scoped context, which the message filter can't
            // resolve (it runs in a fresh scope). Flows to the filter via the session execution context.
            McpPermissionFilter.CurrentPermissions = reqCtx.Permissions;
            // Same reasoning as CurrentPermissions above: capture the store's read-only status here,
            // from the auth-populated reqCtx, so the MCP write tools (remember/append/forget/compact/
            // link/reconcile) are denied when the caller's store_access grant is read-only — parity
            // with the REST write handlers (task B4).
            McpPermissionFilter.CurrentStoreReadOnly = !MemoryEndpoints.IsWritable(reqCtx, storeName);
            MemoryStoreContext.Current = sm.GetStore(storeName);
            SearchContributorContext.Current = httpContext.RequestServices.GetService<ISearchScoreContributor>();
            // Entity-graph signal is embedder-independent and on by default; stateless, so a
            // per-request instance is negligible against the O(N) scan it performs.
            EntityGraphScoreContext.Current = new EntityGraphScoreContributor();
            // GetServices, not GetService: sinks are registered individually (so maintenance can
            // be registered unconditionally), and GetService would return only the last one.
            MemoryEventSinkContext.Current = new CompositeEventSink(
                [.. httpContext.RequestServices.GetServices<IMemoryEventSink>()]);
            return Task.CompletedTask;
        };
    })
    // Same tool serializer options as the CLI `serve` path: source-gen the Dictionary<string,string[]>
    // attribute params so the tool surface builds without reflection (trim-safe if the server is ever
    // published trimmed; today it runs un-trimmed, so this is defense-in-depth / parity).
    .WithTools<ScriniaMcpTools>(ScriniaMcpTools.CreateToolSerializerOptions())
    .WithPrompts<ScriniaMcpPrompts>()
    .WithResources<ScriniaMcpResources>();

// JSON
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonContext.Default);
});

var app = builder.Build();

var corsOrigins = builder.Configuration.GetSection("Scrinia:CorsOrigins").Get<string[]>();
if (corsOrigins is ["*"])
{
    app.Logger.LogWarning("CORS is configured with wildcard origin '*'. This allows any origin to access the API. Restrict CorsOrigins in production.");
}

// ── Bootstrap key ────────────────────────────────────────────────────────────

if (!keyStore.HasAnyKeys())
{
    var storeManager = app.Services.GetRequiredService<StoreManager>();
    var (rawKey, keyId, _) = keyStore.CreateKey(
        "admin", storeManager.StoreNames.ToArray(),
        // Every permission the server recognises. This key is the ONLY key on a fresh server, so
        // any omission here is a permission an operator cannot reach at all — including 'reindex',
        // whose route (POST /api/v1/stores/{store}/embeddings/reindex) is the documented migration
        // step for a deployment that does not auto-reindex on startup.
        [Permissions.Read, Permissions.Search, Permissions.Store, Permissions.Append, Permissions.Forget,
         Permissions.Copy, Permissions.Export, Permissions.Import, "manage_keys", "manage_roles", "health",
         Permissions.Chat, Permissions.Reindex], "bootstrap");

    // Write bootstrap key to a file (read once, then delete)
    string keyFile = Path.Combine(dataDir, "BOOTSTRAP_KEY");
    File.WriteAllText(keyFile, rawKey);
    app.Logger.LogWarning(
        "No API keys found. Bootstrap key (id={KeyId}) written to {KeyFile} — read it, then delete the file.",
        keyId, keyFile);
}

// Warn if bootstrap key file still exists after operator has created additional keys
{
    string bootstrapKeyPath = Path.Combine(dataDir, "BOOTSTRAP_KEY");
    if (File.Exists(bootstrapKeyPath) && keyStore.ListKeys().Count > 1)
    {
        app.Logger.LogCritical("SECURITY: Bootstrap key file '{Path}' still exists. Delete it after creating a permanent API key.", bootstrapKeyPath);
    }
}

// ── Middleware pipeline ──────────────────────────────────────────────────────

// Global exception handler — sanitize unhandled exceptions
app.UseExceptionHandler(error =>
{
    error.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new ErrorResponse("An internal error occurred."),
            ServerJsonContext.Default.ErrorResponse);
    });
});

// HTTPS enforcement in production
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Security headers
//
// Registered via Response.OnStarting, not direct assignment, for exactly the reason documented on
// the API-version middleware below: UseExceptionHandler (registered above, so it wraps this) calls
// Response.Clear() on an unhandled exception, emptying the header collection before it writes the
// 500 body. Headers assigned directly here were wiped by that Clear(), so a genuine server crash —
// the response most likely to carry an unexpected content type — arrived with no nosniff and no
// framing protection at all. OnStarting callbacks live outside the header collection and fire
// immediately before the response starts, i.e. after any Clear(), so they survive whatever happens
// downstream (reproduced: see SecurityHeadersOn500Tests).
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        context.Response.Headers.XXSSProtection = "0"; // modern browsers: CSP preferred
        return Task.CompletedTask;
    });
    await next();
});

// API version — stamped on every response under /api/v1/stores (MemoryEndpoints' group and its
// nested ChatEndpoints group) so HttpMemoryStore can fail closed against a server it doesn't
// recognise: an old/unversioned server ignores an unknown filter param (excludeTopics,
// pathPrefix, …) instead of rejecting it, silently returning an unfiltered result set that looks
// filtered. Deliberately middleware, not an endpoint filter: an endpoint filter only runs once
// UseAuthorization has already let a request through to the matched endpoint, so it would miss
// the header on a 401/403 — and the client must recognise a real server on those responses too.
//
// Registered via Response.OnStarting, not a direct header assignment, and deliberately NOT via a
// second app.Use(...) that just sets the header before next() (that was this middleware's first
// shape, and it was wrong): UseExceptionHandler (registered earlier, above, so it wraps this
// middleware) calls Response.Clear() — which empties the header collection — whenever an
// unhandled exception propagates up from anything after this point and the response hasn't
// started, then runs its own separate error-writing branch to produce the 500 body. A header set
// directly here would be wiped by that Clear() before the client ever sees it, so a genuine server
// crash would arrive with no version header at all — indistinguishable, to the client's guard,
// from an old/unversioned server, misdirecting the caller toward "wrong API version" instead of
// "the server crashed" (reproduced: see ApiVersionHeaderOn500Tests). OnStarting callbacks are
// stored outside the header collection and fire immediately before the response actually starts —
// i.e. after any Clear() — so registering the stamp here, rather than writing it immediately,
// guarantees it survives regardless of what happens downstream, unhandled exception or not.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/stores"))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RemoteApiVersion.HeaderName] = RemoteApiVersion.Current;
            return Task.CompletedTask;
        });
    }
    await next();
});

// Static files (Web UI) — served before auth so no token is required
app.UseDefaultFiles();
app.UseStaticFiles();

// Request timing — outermost diagnostic layer
app.UseMiddleware<RequestTimingMiddleware>();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Runs after auth (per-user partitioning needs the authenticated identity): unauthenticated
// requests are rejected by auth before ever reaching the limiter. Acceptable trade-off — API
// keys are high-entropy, so pre-auth request volume isn't a meaningful abuse vector here.
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is not null)
        {
            var reqCtx = context.RequestServices.GetRequiredService<RequestContext>();
            reqCtx.UserId = userId;
            reqCtx.Stores = context.User.FindAll(StoreSelectorKey.Name).Select(c => c.Value).ToArray();
            reqCtx.Permissions = context.User.FindAll("permission").Select(c => c.Value).ToArray();
            reqCtx.StoreAccessLevels = context.User.FindAll("store_access").Select(c => c.Value).ToArray();

            // Resolve {store} route parameter if present
            if (context.Request.RouteValues.TryGetValue(StoreSelectorKey.Name, out var storeValue) && storeValue is string storeName)
            {
                var sm = context.RequestServices.GetRequiredService<StoreManager>();
                if (!sm.StoreExists(storeName))
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsJsonAsync(
                        new ErrorResponse($"Store '{storeName}' not found."),
                        ServerJsonContext.Default.ErrorResponse);
                    return;
                }

                if (!reqCtx.CanAccessStore(storeName))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(
                        new ErrorResponse($"Access denied to store '{storeName}'."),
                        ServerJsonContext.Default.ErrorResponse);
                    return;
                }

                reqCtx.ActiveStore = storeName;
                reqCtx.Store = sm.GetStore(storeName);
            }
        }
    }

    await next();
});

// Set search contributor context for REST requests (NOT event sink — hooks handle that via PluginPipeline).
// Skip /mcp: those requests set their contexts per-session in ConfigureSessionOptions, so doing it
// here too is redundant (and allocates an unused EntityGraphScoreContributor per MCP request).
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/mcp"))
    {
        SearchContributorContext.Current = context.RequestServices.GetService<ISearchScoreContributor>();
        EntityGraphScoreContext.Current = new EntityGraphScoreContributor();
    }
    await next();
});

// Plugin middleware (after auth, before endpoints)
foreach (var plugin in loadedPlugins)
    plugin.ConfigureMiddleware(app);

// ── Endpoints ────────────────────────────────────────────────────────────────

app.MapMemoryEndpoints();
app.MapKeyEndpoints();
app.MapHealthEndpoints();
app.MapEmbeddingsEndpoints();
app.MapChatEndpoints(app.Services.GetRequiredService<Scrinia.Server.Chat.ChatOptions>());

// Plugin endpoints
// Plugins map their own routes into this group, so the permissions are not knowable here.
// Empty in the default configuration — plugins load only when Vulkan is explicitly enabled
// (see the loadedPlugins block above) — which is why no test exercises the populated case.
// "the plugin declares it" is a promise nothing enforces; making plugin-mapped routes
// declare their permissions is a plugin-contract change, tracked as follow-up.
var pluginGroup = app.MapGroup("/api/v1/plugins")
    .RequireAuthorization()
    .RequireRateLimiting("api")
    .PermissionCheckedElsewhere(PermissionCheckedElsewhereReasons.PluginOwnDeclarations);
foreach (var plugin in loadedPlugins)
    plugin.MapEndpoints(pluginGroup);

if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Scrinia:ExposeOpenApi", false))
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// One HTTP route carrying every JSON-RPC operation. The permission varies per call and is
// resolved by McpPermissionFilter, which EndpointDataSource cannot see; naming a single
// permission here would name one that isn't the real one.
app.MapMcp("/mcp")
   .RequireAuthorization()
   .RequireRateLimiting("api")
   .PermissionCheckedElsewhere(PermissionCheckedElsewhereReasons.McpPerJsonRpcCall);

// SPA fallback — must be last so API routes take priority
app.MapFallbackToFile("index.html");

// ── Permission coverage ──────────────────────────────────────────────────────

// Runs after every Map* call, because it reads the fully populated route table, and before
// the server accepts a request, because a route that authenticates without authorizing must
// never be reachable. A failure here names the offending endpoint; fix the declaration.
// Which route table it reads is the extension's decision, not this call site's — see its
// docs, one of the two plausible sources is empty here and checks nothing.
app.ValidatePermissionCoverage();

// ── Graceful shutdown ────────────────────────────────────────────────────────

app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Logger.LogInformation("Shutting down — disposing resources…");
    app.Services.GetRequiredService<Scrinia.Server.Chat.ChatProviderCache>().Dispose();
});

await app.RunAsync();

// The MCP endpoint writes through the same MemoryTools path as the CLI, so sinks dispatched by
// a request near shutdown can still be indexing. Finish them before the process ends rather
// than leaving a vector file half-written.
if (!await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10)))
{
    app.Logger.LogWarning(
        "Background indexing did not finish within 10s and was cancelled. Some memories may not " +
        "appear in semantic search until the next write or a reindex.");
}

// Required for WebApplicationFactory<Program> in tests
public partial class Program { }
