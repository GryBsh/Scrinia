using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Scrinia.Plugin.Llm.Cli;

/// <summary>
/// MCP tools exposed by the bundled LLM plugin. Intentionally minimal: a generic
/// <c>complete</c> for inference and a <c>status</c> for probing. The plugin is a
/// <em>capability</em> (text generation) — task-specific prompts and parsing live
/// in <c>Scrinia.Core.Llm.LlmPrompts</c> so new Tier 2 features ship as core changes,
/// not plugin rebuilds.
/// </summary>
[McpServerToolType]
public sealed class LlmTools(ILocalLlm provider)
{
    [McpServerTool(Name = "status")]
    [Description("Returns plugin status as JSON: { provider, available, hardware, modelArch, modelPath, lastError }.")]
    public string Status()
    {
        return JsonSerializer.Serialize(new
        {
            provider = provider.GetType().Name,
            available = provider.IsAvailable,
            hardware = provider.Hardware,
            modelArch = provider.ModelArchitecture,
            modelPath = provider.ModelPath,
            lastError = provider.LastError,
        });
    }

    /// <summary>
    /// Runs a single completion. Returns <c>{ text }</c> on success or <c>{ error }</c>
    /// on any failure the host should treat as skip-and-continue. The host serialises
    /// calls so the underlying executor needs no concurrency control.
    /// </summary>
    [McpServerTool(Name = "complete")]
    [Description("Runs a single chat completion using the embedded GGUF chat template. Returns JSON { text } or { error }.")]
    public async Task<string> Complete(
        [Description("System role text.")] string system,
        [Description("User role text.")] string user,
        [Description("Max tokens to generate.")] int maxTokens = 256,
        [Description("Sampling temperature; 0.3 is a sensible Tier 2 default.")] double temperature = 0.3,
        [Description("Optional anti-prompt stop strings.")] string[]? stopSequences = null,
        CancellationToken ct = default)
    {
        if (!provider.IsAvailable)
        {
            return JsonSerializer.Serialize(new
            {
                error = provider.LastError ?? "LLM provider is not available.",
            });
        }

        try
        {
            string? text = await provider.CompleteAsync(system, user, maxTokens, temperature, stopSequences, ct);
            if (string.IsNullOrWhiteSpace(text))
                return JsonSerializer.Serialize(new { error = "Empty completion." });
            return JsonSerializer.Serialize(new { text });
        }
        catch (OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { error = "Cancelled." });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"{ex.GetType().Name}: {ex.Message}",
            });
        }
    }
}
