using Scrinia.Core.Process;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Builds an <see cref="IBenchLlmClient"/> backend for the agent harness from CLI args.
///
/// <para><b>Supported providers</b> (explicit, no <c>auto</c>):
/// <list type="bullet">
///   <item><c>claude-cli</c> / <c>codex-cli</c> / <c>copilot-cli</c> — agent CLIs
///     reusing the user's CLI auth. No API key needed.</item>
///   <item><c>ollama</c> — OpenAI-compatible Ollama endpoint (default
///     <c>http://localhost:11434/v1</c>).</item>
///   <item><c>openai</c> — generic OpenAI-compatible HTTP endpoint (real OpenAI,
///     LM Studio, llama.cpp server, vLLM, etc.).</item>
///   <item><c>anthropic</c> — Anthropic Messages API.</item>
///   <item><c>gemini</c> — Google AI Studio / Gemini API.</item>
/// </list>
/// </para>
///
/// <para>An <c>IsAvailableAsync</c> probe runs once at build time so the bench
/// surfaces configuration failure up-front instead of silently degrading to no-op
/// (which would produce a misleading <c>__agent</c> result file).</para>
/// </summary>
internal static class AgentClientBuilder
{
    public sealed record Backend(IBenchLlmClient Llm, string ProviderId, string ModelSlug);

    /// <param name="provider">claude-cli, codex-cli, copilot-cli, ollama, openai, anthropic, or gemini.</param>
    /// <param name="model">Required for ollama/openai/anthropic/gemini; optional for agent CLIs.</param>
    /// <param name="baseUrl">Optional HTTP endpoint override for ollama/openai.</param>
    /// <param name="apiKey">Bearer / API key for openai/anthropic/gemini. Ollama doesn't need one.</param>
    public static async Task<Backend> BuildAsync(
        string provider, string? model, string? baseUrl, string? apiKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        string providerId = provider.Trim().ToLowerInvariant();
        string modelSlug = Slugify(string.IsNullOrWhiteSpace(model) ? "default" : model);

        IBenchLlmClient llm;
        switch (providerId)
        {
            case "claude-cli":
            case "codex-cli":
            case "copilot-cli":
                {
                    var variant = AgentCliVariant.TryFromId(providerId)
                        ?? throw new ArgumentException(
                            $"unknown agent-CLI provider '{provider}'. " +
                            "Expected: claude-cli | codex-cli | copilot-cli");
                    var options = new LlmOptions
                    {
                        Provider = providerId,
                        ClaudeCliModel = providerId == "claude-cli" ? model : null,
                        CodexCliModel = providerId == "codex-cli" ? model : null,
                        CopilotCliModel = providerId == "copilot-cli" ? model : null,
                    };
                    llm = new AgentCliBenchLlmClient(variant, new ProcessRunner(), options);
                    break;
                }

            case "ollama":
            case "openai":
                {
                    if (string.IsNullOrWhiteSpace(model))
                        throw new ArgumentException(
                            $"--agent-provider {providerId} requires --agent-model. " +
                            (providerId == "ollama"
                                ? "Try a small fast model: qwen2.5:0.5b, llama3.2:1b, phi3:mini. " +
                                  "Pull it first via `ollama pull <model>`."
                                : "Specify the model name your OpenAI-compatible endpoint expects."));

                    var options = new LlmOptions
                    {
                        Provider = providerId,
                        Model = model,
                        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? new LlmOptions().BaseUrl : baseUrl,
                        ApiKey = apiKey,
                    };
                    llm = OpenAiCompatibleBenchLlmClient.Create(options);
                    break;
                }

            case "anthropic":
                {
                    if (string.IsNullOrWhiteSpace(model))
                        throw new ArgumentException(
                            "--agent-provider anthropic requires --agent-model (e.g. claude-sonnet-4-5).");
                    if (string.IsNullOrWhiteSpace(apiKey))
                        throw new ArgumentException(
                            "--agent-provider anthropic requires --agent-api-key.");
                    var options = new LlmOptions
                    {
                        Provider = providerId,
                        Model = model,
                        AnthropicApiKey = apiKey,
                    };
                    llm = AnthropicBenchLlmClient.Create(options);
                    break;
                }

            case "gemini":
                {
                    if (string.IsNullOrWhiteSpace(model))
                        throw new ArgumentException(
                            "--agent-provider gemini requires --agent-model (e.g. gemini-2.5-flash).");
                    if (string.IsNullOrWhiteSpace(apiKey))
                        throw new ArgumentException(
                            "--agent-provider gemini requires --agent-api-key.");
                    var options = new LlmOptions
                    {
                        Provider = providerId,
                        Model = model,
                        GeminiApiKey = apiKey,
                    };
                    llm = GeminiBenchLlmClient.Create(options);
                    break;
                }

            default:
                throw new ArgumentException(
                    $"unknown --agent-provider '{provider}'. " +
                    "Expected: claude-cli | codex-cli | copilot-cli | ollama | openai | anthropic | gemini");
        }

        bool available = await llm.IsAvailableAsync(ct);
        if (!available)
            throw new InvalidOperationException(
                $"Agent LLM provider '{providerId}' is not available. " +
                providerId switch
                {
                    "claude-cli" or "codex-cli" or "copilot-cli" =>
                        $"The '{ResolveExe(providerId)}' binary was not found on PATH. " +
                        "Install the agent CLI and ensure it can authenticate non-interactively.",
                    "ollama" =>
                        $"The Ollama endpoint at '{(string.IsNullOrWhiteSpace(baseUrl) ? new LlmOptions().BaseUrl : baseUrl)}' " +
                        "is not reachable, or the model is not pulled. " +
                        $"Verify with `ollama serve` and `ollama pull {model}`.",
                    "openai" =>
                        $"The OpenAI-compatible HTTP endpoint at '{(string.IsNullOrWhiteSpace(baseUrl) ? new LlmOptions().BaseUrl : baseUrl)}' " +
                        "is not reachable. Start your server or point --agent-base-url at a reachable endpoint.",
                    "anthropic" =>
                        "The Anthropic API probe failed. Check --agent-api-key and network reachability of api.anthropic.com.",
                    "gemini" =>
                        "The Gemini API probe failed. Check --agent-api-key and network reachability of generativelanguage.googleapis.com.",
                    _ => "Check provider configuration.",
                });

        return new Backend(llm, providerId, modelSlug);
    }

    private static string ResolveExe(string providerId) => providerId switch
    {
        "claude-cli" => "claude",
        "codex-cli" => "codex",
        "copilot-cli" => "copilot",
        _ => providerId,
    };

    private static string Slugify(string raw)
    {
        Span<char> buf = stackalloc char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            buf[i] = char.IsLetterOrDigit(raw[i]) || raw[i] is '-' or '.' ? raw[i] : '-';
        return new string(buf).Trim('-');
    }
}
