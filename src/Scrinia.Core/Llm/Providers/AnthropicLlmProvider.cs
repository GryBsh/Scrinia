using System.Net.Http.Json;

namespace Scrinia.Core.Llm.Providers;

/// <summary>
/// <see cref="IBackgroundLlm"/> implementation against Anthropic's native Messages API
/// (POST <c>/v1/messages</c>). Used when the user has an Anthropic API key and prefers
/// raw API access over shelling out to <c>claude</c> CLI. The Messages API has better
/// feature parity than Anthropic's OpenAI-compat shim (tool use, thinking, prompt
/// caching) and is on a longer support timeline.
///
/// <para>Auth is via the <c>x-api-key</c> header plus the required
/// <c>anthropic-version</c> header. <c>LlmOptions.AnthropicBaseUrl</c> defaults to
/// <c>https://api.anthropic.com</c>; the <c>/v1/messages</c> path is appended by the
/// provider.</para>
/// </summary>
public sealed class AnthropicLlmProvider : ResilientLlmProvider
{
    private const string ApiVersion = "2023-06-01";

    public static AnthropicLlmProvider Create(LlmOptions options)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
        return new AnthropicLlmProvider(options, http, ownsHttp: true);
    }

    internal AnthropicLlmProvider(LlmOptions options, HttpClient http, bool ownsHttp = false)
        : base(options, http, ownsHttp) { }

    public override async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        // Cheap probe: GET /v1/messages without a body returns 405 (Method Not Allowed)
        // when reachable with a valid API key, or 401/403 with an invalid one. Anything
        // other than 401/403 indicates the endpoint is up and credentials are accepted.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri("/v1/messages"));
            ApplyAuth(req);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)resp.StatusCode;
            return code != 401 && code != 403;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    protected override async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        var request = new AnthropicMessagesRequest(
            Model: Options.Model,
            Messages: [new AnthropicMessage("user", userPrompt)],
            MaxTokens: maxTokens,
            System: systemPrompt,
            Temperature: Options.Temperature);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("/v1/messages"))
            {
                Content = JsonContent.Create(request, AnthropicJsonContext.Default.AnthropicMessagesRequest),
            };
            ApplyAuth(req);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            AnthropicMessagesResponse? body = await resp.Content.ReadFromJsonAsync(
                AnthropicJsonContext.Default.AnthropicMessagesResponse, ct);

            // Messages API returns an array of content blocks; concatenate text-typed ones.
            // Tool-use blocks (which we don't request) would have type=tool_use with no text.
            if (body?.Content is null || body.Content.Length == 0) return null;
            string combined = string.Concat(body.Content
                .Where(b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase))
                .Select(b => b.Text));
            return string.IsNullOrWhiteSpace(combined) ? null : combined.Trim();
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private Uri BuildUri(string path)
    {
        string baseUrl = Options.AnthropicBaseUrl.TrimEnd('/');
        string rel = path.TrimStart('/');
        return new Uri($"{baseUrl}/{rel}");
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(Options.AnthropicApiKey))
            req.Headers.TryAddWithoutValidation("x-api-key", Options.AnthropicApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
    }
}
