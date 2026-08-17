using System.Reflection;
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

// Secret config (API keys) arrives via the environment, never argv (Stream 5 S1).
PluginConfigProtocol.ApplySecretEnvironment(configValues, Environment.GetEnvironmentVariable);

dataDir ??= Path.Combine(AppContext.BaseDirectory, "..", ".scrinia");
dataDir = Path.GetFullPath(dataDir);
Directory.CreateDirectory(dataDir);

modelsDir ??= Path.Combine(AppContext.BaseDirectory, "scri-plugin-embeddings");
Directory.CreateDirectory(modelsDir);

// -- Initialize Vulkan embeddings --
ILogger logger = NullLogger.Instance;
string embeddingsDir = Path.Combine(dataDir, "embeddings");
Directory.CreateDirectory(embeddingsDir);

var options = new EmbeddingOptions();

if (configValues.TryGetValue("Scrinia:Embeddings:SemanticWeight", out var sw) && double.TryParse(sw, out var swVal))
    options.SemanticWeight = swVal;

// The chunking keys reach this process the same way every other forwarded key does
// (PluginConfigProtocol.Keys). Without them the plugin embedded at built-in defaults while the
// host's signature was composed from the configured values, so `Scrinia:Embeddings:ChunkSize`
// was silently ignored end to end whenever this plugin owned embeddings.
if (configValues.TryGetValue("Scrinia:Embeddings:ChunkSize", out var csRaw)
    && int.TryParse(csRaw, out int cs) && cs > 0)
    options.ChunkSize = cs;

if (configValues.TryGetValue("Scrinia:Embeddings:ChunkOverlap", out var coRaw)
    && int.TryParse(coRaw, out int co) && co >= 0)
    options.ChunkOverlap = co;

if (configValues.TryGetValue("Scrinia:Embeddings:MaxChunksPerMemory", out var mcRaw)
    && int.TryParse(mcRaw, out int mc) && mc > 0)
    options.MaxChunksPerMemory = mc;

// Same two guardrails the CLI host applies (WorkspaceSetup.BuildEmbeddingOptions): overlap at or
// above the window size makes SliceWindows throw, and a cap at or above the stride makes
// MemoryVectorIndexer's constructor throw. Both would kill this process at startup.
if (options.ChunkOverlap >= options.ChunkSize)
{
    Console.Error.WriteLine(
        $"[scrinia:warn] ChunkOverlap ({options.ChunkOverlap}) >= ChunkSize ({options.ChunkSize}); " +
        $"reverting to defaults {TextChunker.DefaultWindowSize}/{TextChunker.DefaultOverlap}.");
    options.ChunkSize = TextChunker.DefaultWindowSize;
    options.ChunkOverlap = TextChunker.DefaultOverlap;
}

int requestedCap = options.MaxChunksPerMemory;
if (options.ClampChunkCapBelowStride())
{
    Console.Error.WriteLine(
        $"[scrinia:warn] MaxChunksPerMemory ({requestedCap}) must be below " +
        $"{MemoryVectorIndexer.ChunkIndexStride}; clamping to {options.MaxChunksPerMemory}.");
}

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

// Sign the store with the active provider's signature (null when unavailable — non-destructive).
var vectorStore = new VectorStore(embeddingsDir, ChunkedSignature.ForProvider(provider, options));

Console.Error.WriteLine($"[scrinia:info] Embeddings plugin started (provider={provider.GetType().Name})");

// -- MCP server --
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(vectorStore);
builder.Services.AddSingleton(provider);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new MemoryVectorIndexer(
    provider, vectorStore, NullLogger.Instance, options));

string pluginVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion.Split('+')[0] ?? "unknown";
builder.Services
    .AddMcpServer(mcp => mcp.ServerInfo = new() { Name = "scrinia-plugin-embeddings", Version = pluginVersion })
    .WithStdioServerTransport()
    .WithTools<EmbeddingsTools>();

await builder.Build().RunAsync();
