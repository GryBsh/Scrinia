using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.Server;
using Scalar.AspNetCore;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
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

        var embeddingOptions = new EmbeddingOptions();
        var weightStr = builder.Configuration["Scrinia:Embeddings:SemanticWeight"];
        if (weightStr is not null && double.TryParse(weightStr, out double w))
            embeddingOptions.SemanticWeight = w;

        var embeddingProvider = EmbeddingProviderFactory.Create(
            embeddingOptions, modelsDir,
            loggerFactory.CreateLogger("Scrinia.Embeddings"));

        if (embeddingProvider.IsAvailable)
        {
            var vectorStore = new VectorStore(embeddingsDir);
            builder.Services.AddSingleton(embeddingProvider);
            builder.Services.AddSingleton(vectorStore);
            builder.Services.AddSingleton(sp =>
                new BuiltInEmbeddingsService(
                    sp.GetRequiredService<IEmbeddingProvider>(),
                    sp.GetRequiredService<VectorStore>(),
                    embeddingOptions.SemanticWeight,
                    sp.GetRequiredService<ILogger<BuiltInEmbeddingsService>>()));
            builder.Services.AddSingleton<ISearchScoreContributor>(sp =>
                sp.GetRequiredService<BuiltInEmbeddingsService>());
            builder.Services.AddSingleton<IMemoryEventSink>(sp =>
                sp.GetRequiredService<BuiltInEmbeddingsService>());
            builder.Services.AddSingleton<IMemoryOperationHook>(sp =>
                sp.GetRequiredService<BuiltInEmbeddingsService>());

            bootLogger.LogInformation("Built-in embeddings ready (provider={Provider}, dims={Dims})",
                embeddingProvider.GetType().Name, embeddingProvider.Dimensions);
        }
        else
        {
            bootLogger.LogInformation("Built-in embeddings not available (provider={Provider}). Run 'scri setup' to download the model.",
                embeddingProvider.GetType().Name);
        }
    }
    catch (Exception ex)
    {
        bootLogger.LogWarning(ex, "Failed to initialize built-in embeddings");
    }
}

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
        policy.RequireClaim("permission", "manage_keys"));

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection("Scrinia:CorsOrigins").Get<string[]>();
        if (origins is { Length: > 0 })
            policy.WithOrigins(origins);
        else
            policy.AllowAnyOrigin();
        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddSlidingWindowLimiter("api", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.SegmentsPerWindow = 6;
        opt.QueueLimit = 0;
    });
});

// OpenAPI
builder.Services.AddOpenApi();

// MCP over HTTP
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "scrinia", Version = "0.3.0" };
    })
    .WithHttpTransport(options =>
    {
        options.PerSessionExecutionContext = true;
        options.ConfigureSessionOptions = (httpContext, serverOptions, ct) =>
        {
            // Resolve store from query param: /mcp?store=default
            var storeName = httpContext.Request.Query["store"].FirstOrDefault() ?? "default";
            var sm = httpContext.RequestServices.GetRequiredService<StoreManager>();
            if (!sm.StoreExists(storeName))
                throw new InvalidOperationException($"Store '{storeName}' not found.");
            MemoryStoreContext.Current = sm.GetStore(storeName);
            SearchContributorContext.Current = httpContext.RequestServices.GetService<ISearchScoreContributor>();
            MemoryEventSinkContext.Current = httpContext.RequestServices.GetService<IMemoryEventSink>();
            return Task.CompletedTask;
        };
    })
    .WithTools<ScriniaMcpTools>();

// JSON
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonContext.Default);
});

var app = builder.Build();

// ── Bootstrap key ────────────────────────────────────────────────────────────

if (!keyStore.HasAnyKeys())
{
    var storeManager = app.Services.GetRequiredService<StoreManager>();
    var (rawKey, keyId, _) = keyStore.CreateKey(
        "admin", storeManager.StoreNames.ToArray(),
        ["read", "search", "store", "append", "forget", "copy",
         "export", "import", "manage_keys", "manage_roles"], "bootstrap");

    // Write bootstrap key to a file (read once, then delete)
    string keyFile = Path.Combine(dataDir, "BOOTSTRAP_KEY");
    File.WriteAllText(keyFile, rawKey);
    app.Logger.LogWarning(
        "No API keys found. Bootstrap key (id={KeyId}) written to {KeyFile} — read it, then delete the file.",
        keyId, keyFile);
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
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers.XXSSProtection = "0"; // modern browsers: CSP preferred
    await next();
});

// Static files (Web UI) — served before auth so no token is required
app.UseDefaultFiles();
app.UseStaticFiles();

// Request timing — outermost diagnostic layer
app.UseMiddleware<RequestTimingMiddleware>();

app.UseCors();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is not null)
        {
            var reqCtx = context.RequestServices.GetRequiredService<RequestContext>();
            reqCtx.UserId = userId;
            reqCtx.Stores = context.User.FindAll("store").Select(c => c.Value).ToArray();
            reqCtx.Permissions = context.User.FindAll("permission").Select(c => c.Value).ToArray();
            reqCtx.StoreAccessLevels = context.User.FindAll("store_access").Select(c => c.Value).ToArray();

            // Resolve {store} route parameter if present
            if (context.Request.RouteValues.TryGetValue("store", out var storeValue) && storeValue is string storeName)
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

// Set search contributor context for REST requests (NOT event sink — hooks handle that via PluginPipeline)
app.Use(async (context, next) =>
{
    SearchContributorContext.Current = context.RequestServices.GetService<ISearchScoreContributor>();
    await next();
});

// Plugin middleware (after auth, before endpoints)
foreach (var plugin in loadedPlugins)
    plugin.ConfigureMiddleware(app);

// ── Endpoints ────────────────────────────────────────────────────────────────

app.MapMemoryEndpoints();
app.MapKeyEndpoints();
app.MapHealthEndpoints();

// Plugin endpoints
var pluginGroup = app.MapGroup("/api/v1/plugins");
foreach (var plugin in loadedPlugins)
    plugin.MapEndpoints(pluginGroup);

// Built-in embeddings endpoints (if no plugin provides them)
if (!pluginProvidesEmbeddings)
{
    var embGroup = pluginGroup.MapGroup("/embeddings");

    embGroup.MapGet("/status", (HttpContext ctx) =>
    {
        var svc = ctx.RequestServices.GetService<BuiltInEmbeddingsService>();
        if (svc is null)
            return Results.Ok(new { provider = "none", available = false, dimensions = 0, semanticWeight = 0.0, vectorCount = 0, hardware = "" });
        return Results.Ok(new { provider = svc.ProviderName, available = svc.IsAvailable, dimensions = svc.Dimensions, semanticWeight = svc.SemanticWeight, vectorCount = svc.TotalVectorCount, hardware = "CPU" });
    });

    embGroup.MapGet("/settings", (HttpContext ctx) =>
    {
        var svc = ctx.RequestServices.GetService<BuiltInEmbeddingsService>();
        if (svc is null)
            return Results.Ok(new { provider = "none", semanticWeight = 0.0, maxBatchSize = 1 });
        return Results.Ok(new { provider = svc.ProviderName, semanticWeight = svc.SemanticWeight, maxBatchSize = 1 });
    });

    embGroup.MapPost("/reindex", async (HttpContext ctx, CancellationToken ct) =>
    {
        var svc = ctx.RequestServices.GetService<BuiltInEmbeddingsService>();
        if (svc is null || !svc.IsAvailable)
            return Results.BadRequest(new { error = "Embedding provider is not available." });

        var store = MemoryStoreContext.Current;
        if (store is null)
        {
            // Resolve from request context
            var reqCtx = ctx.RequestServices.GetRequiredService<RequestContext>();
            store = reqCtx.Store;
        }
        if (store is null)
            return Results.BadRequest(new { error = "No store context available." });

        int count = await svc.ReindexStoreAsync(store, ct);
        return Results.Ok(new { message = $"Reindexed {count} memories." });
    });
}

app.MapOpenApi();
app.MapScalarApiReference();

app.MapMcp("/mcp").RequireAuthorization();

// SPA fallback — must be last so API routes take priority
app.MapFallbackToFile("index.html");

// ── Graceful shutdown ────────────────────────────────────────────────────────

app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Logger.LogInformation("Shutting down — disposing resources…");
    keyStore.Dispose();
});

await app.RunAsync();

// Required for WebApplicationFactory<Program> in tests
public partial class Program { }
