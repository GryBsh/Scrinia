using System.Text.RegularExpressions;
using Scrinia.Core.Process;

namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

/// <summary>
/// <see cref="IBenchLlmClient"/> implementation that shells out to a user-installed agent
/// CLI (Claude Code, Codex, GitHub Copilot) running in non-interactive print mode. Reuses
/// the user's existing CLI authentication — no separate API key, no separate model
/// download — and delegates the agent harness ops to whatever model the user already
/// trusts for code tasks.
///
/// <para>Each CLI is invoked with its print-mode flags from <see cref="AgentCliVariant"/>;
/// the combined system+user prompt is piped via stdin; the model's response is read from
/// stdout, stripped of ANSI escapes and any banner lines, and returned.</para>
///
/// <para>Failure modes are tolerant — non-zero exit, timeout, empty stdout, and parse
/// failure all return null so the agent harness can skip the op and continue.
/// Subscription rate limits typically surface as either a non-zero exit with a stderr
/// hint or a long stall ended by the per-call CancellationToken.</para>
///
/// <para><b>JSON mode:</b> agent CLIs don't expose a wire-level JSON-object knob the way
/// OpenAI-compat HTTP does. The <paramref name="jsonMode"/> flag on
/// <see cref="CompleteAsync"/> is therefore advisory — callers must ensure the system
/// prompt asks for JSON when they need it.</para>
/// </summary>
public sealed partial class AgentCliBenchLlmClient : IBenchLlmClient
{
    private readonly AgentCliVariant _variant;
    private readonly IProcessRunner _runner;
    private readonly string? _resolvedExe;
    private readonly LlmOptions _options;

    public AgentCliBenchLlmClient(AgentCliVariant variant, IProcessRunner runner, LlmOptions options)
    {
        _variant = variant;
        _runner = runner;
        _options = options;
        // Resolve once at construction so we fail fast in the probe rather than per-call.
        _resolvedExe = runner.ResolveExecutable(variant.Executable);
    }

    /// <summary>
    /// Available when the CLI binary is on PATH. We deliberately do NOT invoke the CLI
    /// to validate auth — that would be slow (every CLI does a network probe on first
    /// invocation) and disruptive (some CLIs print first-run welcome banners we'd then
    /// have to suppress). Bad auth surfaces as a non-zero exit at first real use.
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(_resolvedExe is not null);

    public async Task<string?> CompleteAsync(string systemPrompt, string userPrompt,
        int maxTokens, bool jsonMode, CancellationToken ct)
    {
        // maxTokens and jsonMode are ignored for agent CLIs — no equivalent flags exist
        // across the three supported CLIs. Callers needing JSON output should write the
        // system prompt to ask for it; callers needing a token budget rely on the
        // per-call CancellationToken plus the request-timeout ceiling.
        _ = maxTokens;
        _ = jsonMode;

        if (_resolvedExe is null) return null;

        // Combined-prompt model: system instructions as leading paragraph, then the user
        // body. Every supported CLI accepts a single combined prompt in print mode.
        string combined = $"{systemPrompt}\n\n{userPrompt}";

        // Apply the configured request timeout as an upper bound on the CLI call.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        var argv = _variant.BuildArguments(modelOverride: ModelOverride(), effortOverride: EffortOverride());

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_resolvedExe, argv, combined, linked.Token);
        }
        catch (InvalidOperationException) { return null; }  // process couldn't start
        catch (OperationCanceledException) { return null; } // outer cancellation

        if (result.TimedOut || result.ExitCode != 0) return null;

        string cleaned = CleanOutput(result.Stdout);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    /// <summary>
    /// Strip ANSI escape sequences and trim. Frontier CLIs typically emit clean text in
    /// print mode but local installs occasionally retain colorization or status banners
    /// — both safe to drop for our purposes (we want the model's content, not the
    /// terminal decoration).
    /// </summary>
    private static string CleanOutput(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return AnsiEscapeRegex().Replace(raw, "").Trim();
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeRegex();

    /// <summary>
    /// Picks the right <see cref="LlmOptions"/> model field for the active variant. Each
    /// CLI has its own override key because the model namespaces don't overlap
    /// (<c>sonnet</c> is meaningless to codex; <c>gpt-5.4-mini</c> to claude).
    /// </summary>
    private string? ModelOverride() => _variant.Id switch
    {
        "claude-cli" => _options.ClaudeCliModel,
        "codex-cli" => _options.CodexCliModel,
        "copilot-cli" => _options.CopilotCliModel,
        _ => null,
    };

    /// <summary>
    /// Picks the right <see cref="LlmOptions"/> effort field for the active variant.
    /// Codex has no reasoning-effort flag, so the codex branch returns null regardless
    /// of any user config.
    /// </summary>
    private string? EffortOverride() => _variant.Id switch
    {
        "claude-cli" => _options.ClaudeCliEffort,
        "copilot-cli" => _options.CopilotCliEffort,
        _ => null,
    };
}
