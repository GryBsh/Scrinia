using Scrinia.Core.Llm;
using Scrinia.Core.Process;

namespace Scrinia.Retrieval.Benchmarks.Llm;

/// <summary>
/// Builds an <see cref="IBackgroundLlm"/> from bench CLI args. Focused subset of
/// <c>WorkspaceSetup.TryLoadBackgroundLlmAsync</c> — that loader is tied to the
/// generic-host pipeline (config providers, DI), neither of which the bench has.
///
/// <para><b>Supported providers (explicit, no <c>auto</c>):</b>
/// <list type="bullet">
///   <item><c>claude-cli</c> / <c>codex-cli</c> / <c>copilot-cli</c> — agent CLIs.
///     Reuses the user's subscription auth; no API key needed.</item>
///   <item><c>ollama</c> — Ollama-compatible HTTP endpoint (defaults to
///     <c>http://localhost:11434/v1</c>). Best for local iteration: no quota
///     pressure, calls visible in <c>ollama serve</c>'s log output, fast small
///     models (<c>qwen2.5:0.5b</c>, <c>llama3.2:1b</c>, <c>phi3:mini</c>).</item>
///   <item><c>openai</c> — generic OpenAI-compatible HTTP endpoint (LM Studio,
///     llama.cpp server, vLLM, OpenAI itself). Set <c>--enrich-base-url</c>
///     and <c>--enrich-api-key</c> to target a specific server.</item>
/// </list></para>
///
/// <para>The bench deliberately does NOT consult <c>Scrinia:Llm:*</c> config. A
/// measurement tool needs reproducible runs; an env-var or workspace-config drift
/// would silently change what's being measured. Everything comes from explicit
/// <c>--enrich-*</c> args.</para>
///
/// <para>An <c>IsAvailableAsync</c> probe runs once at build time. The bench surfaces
/// the failure rather than degrading to no-op — a Tier 2 run that silently dropped
/// to Tier 1 would produce a misleading result file under a <c>__tier2</c> tag.</para>
/// </summary>
internal static class BenchLlmBackendBuilder
{
    /// <summary>
    /// Result of a successful build. <see cref="ModelSlug"/> is the
    /// caller-supplied label used for cache filename naming — does NOT necessarily
    /// match the resolved model the CLI ends up calling (e.g. claude-cli's "sonnet"
    /// alias resolves at the CLI layer).
    /// </summary>
    public sealed record Backend(IBackgroundLlm Llm, string ProviderId, string ModelSlug);

    /// <param name="provider">claude-cli, codex-cli, copilot-cli, ollama, or openai.</param>
    /// <param name="model">Required for ollama/openai (no sensible default — the user's
    ///   pulled-model list is server-specific). Optional for agent CLIs (variant default
    ///   applies if null).</param>
    /// <param name="baseUrl">Optional HTTP endpoint override for ollama/openai. Defaults
    ///   to ollama's <c>http://localhost:11434/v1</c>. Ignored for agent CLIs.</param>
    /// <param name="apiKey">Optional bearer token for openai/ollama. Ollama doesn't
    ///   require one; real OpenAI / hosted endpoints do. Ignored for agent CLIs.</param>
    public static async Task<Backend> BuildAsync(
        string provider, string? model, string? baseUrl, string? apiKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        string providerId = provider.Trim().ToLowerInvariant();
        string modelSlug = Slugify(string.IsNullOrWhiteSpace(model) ? "default" : model);

        IBackgroundLlm llm;
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
                        // Per-CLI model overrides — leave others null. Null means
                        // "use variant default" (the cost-cap model, see AgentCliVariant
                        // for rationale).
                        ClaudeCliModel = providerId == "claude-cli" ? model : null,
                        CodexCliModel = providerId == "codex-cli" ? model : null,
                        CopilotCliModel = providerId == "copilot-cli" ? model : null,
                    };
                    llm = new AgentCliBackgroundLlm(variant, new ProcessRunner(), options);
                    break;
                }

            case "ollama":
            case "openai":
                {
                    if (string.IsNullOrWhiteSpace(model))
                        throw new ArgumentException(
                            $"--enrich-provider {providerId} requires --enrich-model. " +
                            (providerId == "ollama"
                                ? "Try a small fast model: qwen2.5:0.5b, llama3.2:1b, phi3:mini. " +
                                  "Pull it first via `ollama pull <model>`."
                                : "Specify the model name your OpenAI-compatible endpoint expects."));

                    var options = new LlmOptions
                    {
                        Provider = providerId,
                        Model = model,
                        // Ollama and openai both default to Ollama's endpoint when no
                        // base-url override is provided — that's the LlmOptions default
                        // (http://localhost:11434/v1). Setting BaseUrl unconditionally
                        // when supplied is fine; LlmOptions just stores it verbatim.
                        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? new LlmOptions().BaseUrl : baseUrl,
                        ApiKey = apiKey,
                    };
                    llm = OpenAiCompatibleBackgroundLlm.Create(options);
                    break;
                }

            default:
                throw new ArgumentException(
                    $"unknown --enrich-provider '{provider}'. " +
                    "Expected: claude-cli | codex-cli | copilot-cli | ollama | openai");
        }

        bool available = await llm.IsAvailableAsync(ct);
        if (!available)
            throw new InvalidOperationException(
                $"Tier 2 LLM provider '{providerId}' is not available. " +
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
                        "is not reachable. Start your server (LM Studio / llama.cpp / vLLM / etc.) " +
                        "or point --enrich-base-url at a reachable endpoint.",
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
