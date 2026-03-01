using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

dataDir ??= Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "scrinia", "plugins");
Directory.CreateDirectory(dataDir);

// Models dir is global (shared across workspaces) to avoid re-downloading per project.
modelsDir ??= dataDir;
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

ILogger logger = NullLogger.Instance;
string embeddingsDir = Path.Combine(dataDir, "embeddings");
Directory.CreateDirectory(embeddingsDir);

using var vectorStore = new VectorStore(embeddingsDir);
var provider = EmbeddingProviderFactory.Create(options, modelsDir, logger);

Console.Error.WriteLine($"[scrinia:info] Embeddings plugin started (provider={provider.GetType().Name})");

// ── Protocol loop ────────────────────────────────────────────────────────
await using var stdout = Console.OpenStandardOutput();
using var writer = new StreamWriter(stdout, leaveOpen: true) { AutoFlush = true };

string? line;
while ((line = Console.ReadLine()) is not null)
{
    PluginResponse response;
    try
    {
        var request = JsonSerializer.Deserialize(line, PluginJsonContext.Default.PluginRequest);
        if (request is null)
        {
            response = new PluginResponse { Ok = false, Error = "Invalid request" };
        }
        else
        {
            response = await HandleRequest(request);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[scrinia:warn] Request error: {ex.GetType().Name}: {ex.Message}");
        response = new PluginResponse { Ok = false, Error = ex.Message };
    }

    var json = JsonSerializer.Serialize(response, PluginJsonContext.Default.PluginResponse);
    await writer.WriteLineAsync(json);
}

Console.Error.WriteLine("[scrinia:info] Embeddings plugin shutting down (stdin closed)");
return;

// ── Request handlers ─────────────────────────────────────────────────────

async Task<PluginResponse> HandleRequest(PluginRequest req)
{
    switch (req.Method.ToLowerInvariant())
    {
        case "embed":
            return await HandleEmbed(req);

        case "embed_batch":
            return await HandleEmbedBatch(req);

        case "search":
            return await HandleSearch(req);

        case "upsert":
            return await HandleUpsert(req);

        case "remove":
            return await HandleRemove(req);

        case "status":
            return HandleStatus();

        case "shutdown":
            Console.Error.WriteLine("[scrinia:info] Embeddings plugin received shutdown");
            Environment.Exit(0);
            return new PluginResponse { Ok = true }; // unreachable

        default:
            return new PluginResponse { Ok = false, Error = $"Unknown method: {req.Method}" };
    }
}

async Task<PluginResponse> HandleEmbed(PluginRequest req)
{
    if (req.Text is null)
        return new PluginResponse { Ok = false, Error = "Missing 'text' field" };

    var vector = await provider.EmbedAsync(req.Text);
    if (vector is null)
        return new PluginResponse { Ok = false, Error = "Embedding failed" };

    return new PluginResponse { Ok = true, Vector = vector };
}

async Task<PluginResponse> HandleEmbedBatch(PluginRequest req)
{
    if (req.Texts is null || req.Texts.Length == 0)
        return new PluginResponse { Ok = false, Error = "Missing 'texts' field" };

    var vectors = await provider.EmbedBatchAsync(req.Texts);
    if (vectors is null)
        return new PluginResponse { Ok = false, Error = "Batch embedding failed" };

    return new PluginResponse { Ok = true, Vectors = vectors };
}

async Task<PluginResponse> HandleSearch(PluginRequest req)
{
    if (req.Query is null)
        return new PluginResponse { Ok = false, Error = "Missing 'query' field" };
    if (req.Scopes is null || req.Scopes.Length == 0)
        return new PluginResponse { Ok = false, Error = "Missing 'scopes' field" };

    var queryVec = await provider.EmbedAsync(req.Query);
    if (queryVec is null)
        return new PluginResponse { Ok = false, Error = "Query embedding failed" };

    var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    foreach (string scope in req.Scopes)
    {
        var vectors = vectorStore.GetVectors(scope);
        if (vectors.Count == 0) continue;

        var topK = VectorIndex.Search(queryVec, vectors, vectors.Count);
        foreach (var (entry, similarity) in topK)
        {
            string key = entry.ChunkIndex is not null
                ? $"{scope}|{entry.Name}|{entry.ChunkIndex}"
                : $"{scope}|{entry.Name}";

            scores[key] = similarity * options.SemanticWeight;
        }
    }

    return new PluginResponse { Ok = true, Scores = scores.Count > 0 ? scores : null };
}

async Task<PluginResponse> HandleUpsert(PluginRequest req)
{
    if (req.Scope is null || req.Name is null || req.Text is null)
        return new PluginResponse { Ok = false, Error = "Missing scope, name, or text" };

    if (string.IsNullOrWhiteSpace(req.Text))
        return new PluginResponse { Ok = true };

    var vector = await provider.EmbedAsync(req.Text);
    if (vector is null)
        return new PluginResponse { Ok = false, Error = "Embedding failed" };

    await vectorStore.UpsertAsync(req.Scope, req.Name, req.ChunkIndex, vector);
    return new PluginResponse { Ok = true };
}

async Task<PluginResponse> HandleRemove(PluginRequest req)
{
    if (req.Scope is null || req.Name is null)
        return new PluginResponse { Ok = false, Error = "Missing scope or name" };

    await vectorStore.RemoveAsync(req.Scope, req.Name);
    return new PluginResponse { Ok = true };
}

PluginResponse HandleStatus()
{
    return new PluginResponse
    {
        Ok = true,
        Status = new PluginStatus
        {
            Provider = provider.GetType().Name,
            Hardware = options.Hardware,
            Available = provider.IsAvailable,
            Dimensions = provider.Dimensions,
            VectorCount = vectorStore.TotalVectorCount(),
        }
    };
}
