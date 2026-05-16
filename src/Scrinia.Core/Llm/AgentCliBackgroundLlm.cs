using System.Text.RegularExpressions;
using Scrinia.Core.Process;

namespace Scrinia.Core.Llm;

/// <summary>
/// <see cref="IBackgroundLlm"/> implementation that shells out to a user-installed agent
/// CLI (Claude Code, Codex, GitHub Copilot) running in non-interactive print mode. Reuses
/// the user's existing CLI authentication — no separate API key, no separate model
/// download — and delegates Tier 2 work to whatever model the user already trusts for
/// code tasks.
///
/// <para>Each CLI is invoked with its print-mode flags from <see cref="AgentCliVariant"/>;
/// the combined system+user prompt is piped via stdin; the model's response is read from
/// stdout, stripped of ANSI escapes and any banner lines, and returned.</para>
///
/// <para>Failure modes are tolerant — non-zero exit, timeout, empty stdout, and parse
/// failure all return null so <c>LlmConsolidator</c> can skip the memory and continue
/// the batch. Subscription rate limits typically surface as either a non-zero exit with
/// a stderr hint or a long stall ended by the per-call CancellationToken.</para>
/// </summary>
public sealed partial class AgentCliBackgroundLlm : IBackgroundLlm
{
    private readonly AgentCliVariant _variant;
    private readonly IProcessRunner _runner;
    private readonly string? _resolvedExe;
    private readonly LlmOptions _options;

    public AgentCliBackgroundLlm(AgentCliVariant variant, IProcessRunner runner, LlmOptions options)
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
    /// have to suppress). Bad auth surfaces as a non-zero exit at first real use, where
    /// <c>LlmConsolidator</c> already handles it cleanly.
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(_resolvedExe is not null);

    public Task<string?> GenerateDescriptionAsync(string content, CancellationToken ct) =>
        CompleteAsync(LlmPrompts.DescriptionSystem, LlmPrompts.DescriptionUser(content), ct);

    public Task<string?> SummarizeAsync(string text, CancellationToken ct) =>
        CompleteAsync(LlmPrompts.SummarySystem, LlmPrompts.SummaryUser(text), ct);

    public async Task<string[]?> ExtractFactsAsync(string content, CancellationToken ct)
    {
        string? raw = await CompleteAsync(LlmPrompts.FactsSystem, LlmPrompts.FactsUser(content), ct);
        if (raw is null) return null;
        string[] parsed = LlmPrompts.ParseFacts(raw);
        return parsed.Length == 0 ? null : parsed;
    }

    public async Task<int?> ScoreImportanceAsync(string content, CancellationToken ct)
    {
        string? raw = await CompleteAsync(LlmPrompts.ImportanceSystem, LlmPrompts.ImportanceUser(content), ct);
        return LlmPrompts.ParseImportance(raw);
    }

    private async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (_resolvedExe is null) return null;

        // Combined-prompt model: system instructions as leading paragraph, then the user
        // body. Every supported CLI accepts a single combined prompt in print mode.
        string combined = $"{systemPrompt}\n\n{userPrompt}";

        // Apply the configured request timeout as an upper bound on the CLI call.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_resolvedExe, _variant.Arguments, combined, linked.Token);
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
}
