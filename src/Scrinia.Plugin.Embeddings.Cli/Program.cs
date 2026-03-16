using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Scrinia.Core.Embeddings;
using Scrinia.Plugin.Embeddings;
using Scrinia.Plugin.Embeddings.Cli;

// -- Parse args --
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

modelsDir ??= Path.Combine(AppContext.BaseDirectory, "scri-plugin-embeddings");
Directory.CreateDirectory(modelsDir);

// -- Initialize Vulkan embeddings --
ILogger logger = NullLogger.Instance;
string embeddingsDir = Path.Combine(dataDir, "embeddings");
Directory.CreateDirectory(embeddingsDir);

double semanticWeight = 50.0;
if (configValues.TryGetValue("Scrinia:Embeddings:SemanticWeight", out var sw) && double.TryParse(sw, out var swVal))
    semanticWeight = swVal;

var vectorStore = new VectorStore(embeddingsDir);
IEmbeddingProvider provider;

if (VulkanModelManager.IsModelAvailable(modelsDir))
{
    string modelPath = VulkanModelManager.GetModelPath(modelsDir);
    provider = VulkanEmbeddingProvider.Create(modelPath, 384, logger);
}
else
{
    Console.Error.WriteLine("[scrinia:warn] GGUF model not found. Run setup to download it.");
    provider = new NullEmbeddingProvider();
}

var options = new EmbeddingOptions { SemanticWeight = semanticWeight };

Console.Error.WriteLine($"[scrinia:info] Embeddings plugin started (provider={provider.GetType().Name})");

// -- MCP server --
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(vectorStore);
builder.Services.AddSingleton(provider);
builder.Services.AddSingleton(options);

builder.Services
    .AddMcpServer(mcp => mcp.ServerInfo = new() { Name = "scrinia-plugin-embeddings", Version = "0.3.1" })
    .WithStdioServerTransport()
    .WithTools<EmbeddingsTools>();

await builder.Build().RunAsync();
