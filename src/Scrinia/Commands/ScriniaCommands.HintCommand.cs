using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Commands.Hooks;
using Scrinia.Mcp;
using Spectre.Console;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>Show relevant stored-memory hints for a prompt.</summary>
    /// <param name="prompt">Prompt text; reads stdin when omitted.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="plain">Print a one-line hint instead of the hook payload.</param>
    /// <param name="json">Output raw JSON with count and matches.</param>
    /// <param name="client">Hook client: codex, claude, opencode, or copilot.</param>
    public async Task<int> Hint(
        string? prompt = null,
        string? workspaceRoot = null,
        bool plain = false,
        bool json = false,
        string? client = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        // Disabled → silent zero. Single config flag so a user who finds the hint noisy
        // can shut it off without touching every agent CLI's hook config.
        string? enabled = WorkspaceSetup.GetConfigValue("Scrinia:Hint:Enabled");
        if (enabled is not null && enabled.Equals("false", StringComparison.OrdinalIgnoreCase))
            return 0;

        // Advisory branch: supported hook clients get the fail-silent commit/push reminder rather
        // than the relevance payload. It parses positional input or a bounded stdin read, gates on a
        // verified prompt hook, and returns 0 on every path — never blocking the user's turn.
        HookClient advisoryClient = HintCommand.ParseClient(client);
        if (HintCommand.IsSupportedAdvisoryClient(advisoryClient))
            return EmitCommitPushAdvisory(prompt, new ConsoleStdinReader(), advisoryClient, Console.Out);

        // Resolve thresholds from config with sane defaults.
        double minScore = HintCommand.DefaultMinScore;
        string? cfgScore = WorkspaceSetup.GetConfigValue("Scrinia:Hint:MinScore");
        if (cfgScore is not null && double.TryParse(cfgScore,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out double parsedScore))
            minScore = parsedScore;

        int minPromptChars = HintCommand.DefaultMinPromptChars;
        string? cfgChars = WorkspaceSetup.GetConfigValue("Scrinia:Hint:MinPromptChars");
        if (cfgChars is not null && int.TryParse(cfgChars, out int parsedChars) && parsedChars >= 0)
            minPromptChars = parsedChars;

        // MMR diversity rerank: SearchAll returns InnerLimit candidates, MMR with
        // DiversityLambda picks the final TopK. λ=1 reverts to pre-MMR relevance-only
        // ordering; λ=0 is pure diversity (least similar to already-selected wins).
        int innerLimit = HintCommand.DefaultInnerLimit;
        string? cfgInner = WorkspaceSetup.GetConfigValue("Scrinia:Hint:InnerLimit");
        if (cfgInner is not null && int.TryParse(cfgInner, out int parsedInner) && parsedInner >= 1)
            innerLimit = parsedInner;

        double diversityLambda = HintCommand.DefaultDiversityLambda;
        string? cfgLambda = WorkspaceSetup.GetConfigValue("Scrinia:Hint:DiversityLambda");
        if (cfgLambda is not null && double.TryParse(cfgLambda,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out double parsedLambda))
            diversityLambda = parsedLambda;

        // Stdin fallback when no positional arg given (the common hook-invocation shape).
        // CLIs deliver hook input as either plain prompt text or a JSON envelope like
        // {"prompt": "...", "session_id": "...", ...}. Auto-detect: try JSON first; on
        // failure or absent prompt key, use the raw stdin as the prompt verbatim.
        string actualPrompt = prompt ?? ExtractPromptFromStdin(await ReadAllStdinAsync(cancellationToken));

        var store = MemoryStoreContext.Current
            ?? throw new InvalidOperationException("Workspace store not configured. Call Configure first.");

        var hint = new HintCommand(store);
        var result = hint.Compute(actualPrompt, minScore, minPromptChars,
            HintCommand.DefaultTopK, innerLimit, diversityLambda);

        if (!result.Emitted)
            return 0;

        if (json)
        {
            // Raw structured HintResult shape for programmatic consumers — distinct from the hook
            // envelope (which wraps a model-facing instruction string). Through the generated context,
            // not string interpolation: this used to escape the name with a two-character escaper and
            // interpolate the scope with none at all.
            WriteJson(
                new CliHintOutput(
                    result.Matches.Count,
                    [.. result.Matches.Select(m => new CliHintMatch(m.Scope, m.Name, m.Score))]),
                CliJsonContext.Default.CliHintOutput);
        }
        else if (plain)
        {
            Console.WriteLine(HintCommand.FormatPlain(result));
        }
        else
        {
            // Default: hook payload, tailored to the invoking client. Envelope clients (Claude,
            // Codex, Copilot) get the hookSpecificOutput JSON; OpenCode gets the bare payload its
            // plugin injects directly. No --client → the frozen reference format.
            Console.WriteLine(HintCommand.FormatHook(
                result, "UserPromptSubmit", HintCommand.ParseClient(client)));
        }
        // Deliberate unconditional zero, not a missing failure path. This command runs as a
        // UserPromptSubmit hook: a non-zero exit is how a hook signals "block this prompt", so
        // reporting an internal failure that way would fail the user's turn over a missing hint.
        // Every early return above is zero for the same reason.
        return 0;
    }

    private static async Task<string> ReadAllStdinAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        return string.Empty;
    }

    /// <summary>
    /// Heuristically extract a prompt string from hook stdin. Each CLI delivers
    /// UserPromptSubmit input differently — Claude Code wraps it in JSON
    /// (<c>{prompt, session_id, ...}</c>); some setups pipe plain text. Try JSON first
    /// looking for the canonical <c>prompt</c> key; on any failure fall back to the raw
    /// stdin string.
    /// </summary>
    internal static string ExtractPromptFromStdin(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        string trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return raw;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("prompt", out var promptEl)
                && promptEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return promptEl.GetString() ?? raw;
            }
        }
        catch (System.Text.Json.JsonException) { /* fall through */ }
        return raw;
    }

    /// <summary>
    /// Fail-silent commit/push advisory branch for <c>scri hint</c>. Selects the positional prompt
    /// or reads stdin through the bounded reader, classifies commit/push intent, and — only for a
    /// supported hook client — writes the applicable reminders in commit-then-push order. Any
    /// unsupported client, no intent, malformed input, or read error writes nothing and still returns
    /// exit code 0. It consumes <see cref="PromptHintTransport"/>, <see cref="IBoundedStdinReader"/>,
    /// and <see cref="HintCommand.IsSupportedAdvisoryClient"/> and never touches the relevance payload.
    /// </summary>
    internal static int EmitCommitPushAdvisory(
        string? positionalPrompt, IBoundedStdinReader reader, HookClient client, TextWriter output)
    {
        if (!HintCommand.IsSupportedAdvisoryClient(client))
            return 0;

        PromptTransportResult result = PromptHintTransport.Classify(
            PromptHintTransport.SelectPrompt(positionalPrompt, reader));
        if (!result.Present)
            return 0;

        if (result.CommitIntent)
            output.WriteLine(HintAdvisory.CommitReminder);
        if (result.PushIntent)
            output.WriteLine(HintAdvisory.PushReminder);
        return 0;
    }
}
