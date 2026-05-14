using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Scrinia.Commands;

/// <summary>
/// Interactive Ollama discovery + model configuration used by <c>scri setup</c>. Probes for
/// a running Ollama instance, lists its installed models, prompts the user for embedding +
/// chat selections, pulls missing models via Ollama's streaming <c>/api/pull</c> with
/// progress display, and writes <c>Scrinia:Embeddings:*</c> + <c>Scrinia:Llm:*</c> config so
/// subsequent <c>scri serve</c>/<c>scri consolidate --with-llm</c> just work.
///
/// Defaults match the project's documented direction: <c>nomic-embed-text</c> for embeddings
/// (768-dim, widely available on Ollama) and <c>lfm2:1.2b</c> for chat (LFM2.5-Instruct family
/// per <c>LlmModelManager.DefaultModelFile</c>). When LFM2 isn't available the user can pick
/// any pulled model or type a known-available fallback like <c>llama3.2:1b</c>.
/// </summary>
internal static class OllamaSetup
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultEmbeddingModel = "nomic-embed-text";
    public const string DefaultCompletionModel = "lfm2:1.2b";
    public const string FallbackCompletionModel = "llama3.2:1b";

    public sealed record OllamaModelInfo(string Name, long Size);

    public sealed record ProbeResult(bool Reachable, List<OllamaModelInfo> Models, string? Error);

    /// <summary>
    /// Probes Ollama via <c>GET /api/tags</c> — the native endpoint that always returns 200
    /// with the (possibly empty) list of pulled models when Ollama is up. Avoids depending on
    /// <c>GET /</c>, which has varied across Ollama versions, or <c>/v1/models</c>, which can
    /// 404 on installs without pulled models.
    ///
    /// <para>Returns <c>Reachable=true</c> with the model list on success. On failure,
    /// returns <c>Reachable=false</c> and a human-readable <c>Error</c> string so the caller
    /// can surface the actual reason to the user (timeout, connection refused, DNS, etc.).</para>
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(string baseUrl, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            using var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags", ct);
            if (!resp.IsSuccessStatusCode)
                return new ProbeResult(false, [], $"HTTP {(int)resp.StatusCode} from /api/tags");

            var body = await resp.Content.ReadFromJsonAsync(OllamaSetupJsonContext.Default.OllamaTagsResponse, ct);
            var models = body?.Models?.Select(m => new OllamaModelInfo(m.Name ?? "", m.Size)).ToList() ?? [];
            return new ProbeResult(true, models, null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, [], $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Pulls a model via Ollama's streaming <c>POST /api/pull</c>. The response is a sequence
    /// of newline-delimited JSON status objects; we extract download progress and forward it
    /// to a <see cref="ProgressContext"/>. Returns true on success.
    /// </summary>
    public static async Task<bool> PullModelAsync(string baseUrl, string model, CancellationToken ct)
    {
        // The pull can be slow (multi-GB downloads). No timeout — let cancellation handle it.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var requestBody = new OllamaPullRequest(model, Stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/pull")
        {
            Content = JsonContent.Create(requestBody, OllamaSetupJsonContext.Default.OllamaPullRequest),
        };

        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync(ct);
                AnsiConsole.MarkupLine($"[red]  Pull failed (HTTP {(int)resp.StatusCode}): {Markup.Escape(err)}[/]");
                return false;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            bool succeeded = false;
            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn())
                .StartAsync(async ctx =>
                {
                    ProgressTask? task = null;
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) is not null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        OllamaPullStatus? status;
                        try
                        {
                            status = JsonSerializer.Deserialize(line, OllamaSetupJsonContext.Default.OllamaPullStatus);
                        }
                        catch (JsonException) { continue; }
                        if (status is null) continue;

                        if (status.Total > 0)
                        {
                            // Re-create the task whenever the digest changes (Ollama can pull multiple
                            // layers in sequence). We key on Total since that's most stable.
                            string desc = status.Status ?? "downloading";
                            if (task is null || task.MaxValue != status.Total)
                            {
                                task = ctx.AddTask(desc, maxValue: status.Total);
                            }
                            else
                            {
                                task.Description = desc;
                            }
                            task.Value = status.Completed;
                        }

                        if (string.Equals(status.Status, "success", StringComparison.OrdinalIgnoreCase))
                        {
                            succeeded = true;
                        }
                    }
                });

            return succeeded;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            AnsiConsole.MarkupLine($"[red]  Pull error: {Markup.Escape(ex.Message)}[/]");
            return false;
        }
    }

    /// <summary>
    /// Best-effort classifier: returns true when <paramref name="modelName"/> looks like an
    /// embedding model (name contains "embed", or one of the well-known embedding tags).
    /// Used to split the installed-model list into two prompts.
    /// </summary>
    public static bool LooksLikeEmbeddingModel(string modelName)
    {
        string n = modelName.ToLowerInvariant();
        return n.Contains("embed", StringComparison.Ordinal)
            || n.Contains("bge-", StringComparison.Ordinal)
            || n.Contains("e5-", StringComparison.Ordinal)
            || n.StartsWith("nomic-", StringComparison.Ordinal)
            || n.StartsWith("mxbai-", StringComparison.Ordinal);
    }
}

internal sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaTagsModel>? Models { get; set; }
}

internal sealed class OllamaTagsModel
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

internal sealed record OllamaPullRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("stream")] bool Stream);

internal sealed class OllamaPullStatus
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("completed")]
    public long Completed { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaPullRequest))]
[JsonSerializable(typeof(OllamaPullStatus))]
internal partial class OllamaSetupJsonContext : JsonSerializerContext;
