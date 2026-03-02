using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Scrinia.Plugin.Embeddings;
using Scrinia.Plugin.Embeddings.Cli;
using Scrinia.Plugin.Embeddings.Onnx;

// ── Parse args ───────────────────────────────────────────────────────────
string? dataDir = null;
string? modelsDir = null;
var configValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--data-dir" && i + 1 < args.Length)
        dataDir = args[++i];
    else if (args[i] == "--models-dir" && i + 1 < args.Length)
        modelsDir = args[++i];
    else if (args[i] == "--config" && i + 1 < args.Length)
    {
        var kv = args[++i];
        int eq = kv.IndexOf('=');
        if (eq > 0)
            configValues[kv[..eq]] = kv[(eq + 1)..];
    }
}

dataDir ??= Path.Combine(AppContext.BaseDirectory, "..", ".scrinia");
dataDir = Path.GetFullPath(dataDir);
Directory.CreateDirectory(dataDir);

// Models dir defaults to a folder named after the plugin, alongside the plugin executable.
modelsDir ??= Path.Combine(AppContext.BaseDirectory, "scri-plugin-embeddings");
Directory.CreateDirectory(modelsDir);

// ── Initialize embeddings ────────────────────────────────────────────────
var options = new EmbeddingOptions();
if (configValues.TryGetValue("Scrinia:Embeddings:Provider", out var p)) options.Provider = p;
if (configValues.TryGetValue("Scrinia:Embeddings:Hardware", out var h)) options.Hardware = h;
if (configValues.TryGetValue("Scrinia:Embeddings:SemanticWeight", out var sw) && double.TryParse(sw, out var swVal)) options.SemanticWeight = swVal;
if (configValues.TryGetValue("Scrinia:Embeddings:OllamaBaseUrl", out var obu)) options.OllamaBaseUrl = obu;
if (configValues.TryGetValue("Scrinia:Embeddings:OllamaModel", out var om)) options.OllamaModel = om;
if (configValues.TryGetValue("Scrinia:Embeddings:OpenAiApiKey", out var oak)) options.OpenAiApiKey = oak;
if (configValues.TryGetValue("Scrinia:Embeddings:OpenAiModel", out var oam)) options.OpenAiModel = oam;
if (configValues.TryGetValue("Scrinia:Embeddings:OpenAiBaseUrl", out var oab)) options.OpenAiBaseUrl = oab;
if (configValues.TryGetValue("Scrinia:Embeddings:VoyageAiApiKey", out var vak)) options.VoyageAiApiKey = vak;
if (configValues.TryGetValue("Scrinia:Embeddings:VoyageAiModel", out var vam)) options.VoyageAiModel = vam;
if (configValues.TryGetValue("Scrinia:Embeddings:VoyageAiBaseUrl", out var vab)) options.VoyageAiBaseUrl = vab;
if (configValues.TryGetValue("Scrinia:Embeddings:AzureEndpoint", out var ae)) options.AzureEndpoint = ae;
if (configValues.TryGetValue("Scrinia:Embeddings:AzureApiKey", out var aak)) options.AzureApiKey = aak;
if (configValues.TryGetValue("Scrinia:Embeddings:AzureDeployment", out var ad)) options.AzureDeployment = ad;
if (configValues.TryGetValue("Scrinia:Embeddings:AzureModel", out var am)) options.AzureModel = am;
if (configValues.TryGetValue("Scrinia:Embeddings:AzureApiVersion", out var aav)) options.AzureApiVersion = aav;
if (configValues.TryGetValue("Scrinia:Embeddings:AzureUseV1", out var auv) && bool.TryParse(auv, out var auvVal)) options.AzureUseV1 = auvVal;
if (configValues.TryGetValue("Scrinia:Embeddings:GoogleApiKey", out var gak)) options.GoogleApiKey = gak;
if (configValues.TryGetValue("Scrinia:Embeddings:GoogleModel", out var gm)) options.GoogleModel = gm;
if (configValues.TryGetValue("Scrinia:Embeddings:GoogleBaseUrl", out var gbu)) options.GoogleBaseUrl = gbu;
if (configValues.TryGetValue("Scrinia:Embeddings:GoogleDimensions", out var gd) && int.TryParse(gd, out var gdVal)) options.GoogleDimensions = gdVal;

ILogger logger = NullLogger.Instance;
string embeddingsDir = Path.Combine(dataDir, "embeddings");
Directory.CreateDirectory(embeddingsDir);

var vectorStore = new VectorStore(embeddingsDir);
var provider = EmbeddingProviderFactory.Create(options, modelsDir, logger);

Console.Error.WriteLine($"[scrinia:info] Embeddings plugin started (provider={provider.GetType().Name})");

// ── MCP server ───────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(vectorStore);
builder.Services.AddSingleton<IEmbeddingProvider>(provider);
builder.Services.AddSingleton(options);

builder.Services
    .AddMcpServer(mcp => mcp.ServerInfo = new() { Name = "scrinia-plugin-embeddings", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools<EmbeddingsTools>();

await builder.Build().RunAsync();
