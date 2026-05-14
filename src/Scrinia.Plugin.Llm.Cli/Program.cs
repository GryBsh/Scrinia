using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Scrinia.Plugin.Llm;
using Scrinia.Plugin.Llm.Cli;

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

modelsDir ??= Path.Combine(AppContext.BaseDirectory, "scri-plugin-llm");
Directory.CreateDirectory(modelsDir);

// -- Load model (best effort — fall through to NullLlmProvider so status still works) --
ILogger logger = NullLogger.Instance;

string modelFile = configValues.TryGetValue("Scrinia:Llm:LocalModelFile", out var lf) && !string.IsNullOrWhiteSpace(lf)
    ? lf
    : LlmModelManager.DefaultModelFile;

// Default 8K — covers the largest Tier 2 prompt (fact extraction at MaxInputChars=12_000
// chars ≈ 3K tokens) plus system prompt and 400-token response budget with headroom. LFM2.5
// and Qwen2.5 both support 32K, so users on bigger VRAM can bump this via config.
int contextSize = 8192;
if (configValues.TryGetValue("Scrinia:Llm:LocalContextSize", out var ctxStr)
    && int.TryParse(ctxStr, out var ctxVal) && ctxVal > 0)
    contextSize = ctxVal;

ILocalLlm provider;
if (LlmModelManager.IsModelAvailable(modelsDir, modelFile))
{
    string modelPath = LlmModelManager.GetModelPath(modelsDir, modelFile);
    try
    {
        provider = VulkanLlmProvider.Create(modelPath, contextSize, logger);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[scrinia:warn] LLM model failed to load: {ex.GetType().Name}: {ex.Message}");
        provider = new NullLlmProvider($"{ex.GetType().Name}: {ex.Message}");
    }
}
else
{
    Console.Error.WriteLine(
        $"[scrinia:warn] LLM GGUF not found in {modelsDir}. Run `scri setup` to download it.");
    provider = new NullLlmProvider($"Model file '{modelFile}' missing.");
}

Console.Error.WriteLine(
    $"[scrinia:info] LLM plugin started (provider={provider.GetType().Name}, " +
    $"available={provider.IsAvailable}, arch={provider.ModelArchitecture}, hardware={provider.Hardware})");

// -- MCP server --
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(provider);

string pluginVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion.Split('+')[0] ?? "unknown";
builder.Services
    .AddMcpServer(mcp => mcp.ServerInfo = new() { Name = "scrinia-plugin-llm", Version = pluginVersion })
    .WithStdioServerTransport()
    .WithTools<LlmTools>();

await builder.Build().RunAsync();
