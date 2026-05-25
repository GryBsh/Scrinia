using System.Net.Http.Json;

namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

/// <summary>
/// <see cref="IBenchLlmClient"/> implementation against Google Gemini's
/// <c>generateContent</c> API. Auth is via the <c>x-goog-api-key</c> header.
/// <c>LlmOptions.GeminiBaseUrl</c> defaults to
/// <c>https://generativelanguage.googleapis.com</c>; the
/// <c>/v1beta/models/{model}:generateContent</c> path is constructed per call.
///
/// <para><b>JSON mode:</b> Gemini supports wire-level JSON output via
/// <c>generationConfig.responseMimeType: application/json</c>. The
/// <c>jsonMode</c> flag forwards through.</para>
/// </summary>
public sealed class GeminiBenchLlmClient : ResilientLlmProvider
{
    public static GeminiBenchLlmClient Create(LlmOptions options)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
        return new GeminiBenchLlmClient(options, http, ownsHttp: true);
    }

    internal GeminiBenchLlmClient(LlmOptions options, HttpClient http, bool ownsHttp = false)
        : base(options, http, ownsHttp) { }

    public override async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        // GET /v1beta/models lists available models — returns 200 with a valid API key
        // and the API enabled, 401/403 on auth issues. Used as the availability probe.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri("/v1beta/models"));
            ApplyAuth(req);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    public override async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, bool jsonMode, CancellationToken ct)
    {
        var request = new GeminiGenerateContentRequest
        {
            SystemInstruction = new GeminiSystemInstruction { Parts = [new GeminiRequestPart { Text = systemPrompt }] },
            Contents = [new GeminiTurn { Role = "user", Parts = [new GeminiRequestPart { Text = userPrompt }] }],
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = Options.Temperature,
                MaxOutputTokens = maxTokens,
                // Gemini's wire-level JSON mode: responseMimeType=application/json
                // constrains the model to emit valid JSON. Plain-text completions leave
                // this field null so the request payload stays minimal.
                ResponseMimeType = jsonMode ? "application/json" : null,
            },
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                BuildUri($"/v1beta/models/{Options.Model}:generateContent"))
            {
                Content = JsonContent.Create(request, GeminiRequestJsonContext.Default.GeminiGenerateContentRequest),
            };
            ApplyAuth(req);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            GeminiGenerateContentResponse? body = await resp.Content.ReadFromJsonAsync(
                GeminiResponseJsonContext.Default.GeminiGenerateContentResponse, ct);

            // The first candidate's content has a list of parts; concatenate text fields.
            var firstCandidate = body?.Candidates?.FirstOrDefault();
            var parts = firstCandidate?.Content?.Parts;
            if (parts is null || parts.Length == 0) return null;
            string combined = string.Concat(parts.Select(p => p.Text));
            return string.IsNullOrWhiteSpace(combined) ? null : combined.Trim();
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private Uri BuildUri(string path)
    {
        string baseUrl = Options.GeminiBaseUrl.TrimEnd('/');
        string rel = path.TrimStart('/');
        return new Uri($"{baseUrl}/{rel}");
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(Options.GeminiApiKey))
            req.Headers.TryAddWithoutValidation("x-goog-api-key", Options.GeminiApiKey);
    }
}
