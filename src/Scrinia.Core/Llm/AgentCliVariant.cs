namespace Scrinia.Core.Llm;

/// <summary>
/// Per-CLI configuration captured as data, not code. Each entry describes how to drive
/// one of the supported agent CLIs (<c>claude</c>, <c>codex</c>, <c>copilot</c>) in
/// non-interactive print mode for a single one-shot completion. Captured as data so
/// version drift in CLI flags is a config-change rather than a recompile.
///
/// <para>The prompt is sent via stdin rather than a command-line argument because Tier 2
/// inputs (fact-extraction over a 6K-char memory body plus a system+user template) can
/// exceed Windows' ~8K argument-length limit and contain newlines that are awkward to
/// quote portably. Every supported CLI accepts stdin as a prompt source.</para>
///
/// <para>Combined system+user format: each CLI sees a single prompt string. We prepend
/// the system instructions as a leading paragraph terminated with a blank line, then the
/// user content. Small CLIs (1–2B-param locals) handle this fine; frontier-tier CLIs
/// (Claude, Codex) treat the combined string as the user turn without losing system
/// semantics.</para>
///
/// <para>Model + effort defaults intentionally pin Tier 2 to a cheap tier. The user's
/// daily-driver model (often opus + xhigh effort for Claude users, gpt-5.5 for Codex)
/// is overkill for the four Tier 2 ops (descriptions, summaries, fact extraction,
/// importance scoring): inputs are short, outputs are structured, and the workload
/// wants determinism not deep reasoning. Inheriting the user's default would charge
/// frontier-tier rates for what cleanly fits on Sonnet / gpt-5.4-mini at low effort.
/// Overrides live on <see cref="LlmOptions"/>; per-variant defaults are the cost cap.</para>
/// </summary>
/// <param name="Id">Stable provider value used in <c>Scrinia:Llm:Provider</c> config (e.g. <c>claude-cli</c>).</param>
/// <param name="Executable">Bare exe name probed on PATH (<c>claude</c>, <c>codex</c>, <c>copilot</c>).</param>
/// <param name="BaseArguments">Argv that triggers non-interactive print mode. Model and effort flags are appended at call time by <see cref="BuildArguments"/>.</param>
/// <param name="DisplayName">Human-readable label for logs / setup prompts.</param>
/// <param name="ModelFlag">CLI flag for model selection (e.g. <c>--model</c>, <c>-m</c>). Null when the CLI has no model selector.</param>
/// <param name="DefaultModel">Cheap-tier model identifier appended when no override is given. Null falls back to the CLI's daily-driver default.</param>
/// <param name="EffortFlag">CLI flag for reasoning-effort selection (e.g. <c>--effort</c>). Null when the CLI exposes no effort knob (Codex).</param>
/// <param name="DefaultEffort">Cheap-tier effort level appended when no override is given. Null falls back to the CLI's daily-driver default.</param>
public sealed record AgentCliVariant(
    string Id,
    string Executable,
    IReadOnlyList<string> BaseArguments,
    string DisplayName,
    string? ModelFlag = null,
    string? DefaultModel = null,
    string? EffortFlag = null,
    string? DefaultEffort = null)
{
    public static readonly AgentCliVariant ClaudeCli = new(
        Id: "claude-cli",
        Executable: "claude",
        BaseArguments: ["-p"],
        DisplayName: "Claude Code",
        ModelFlag: "--model",
        DefaultModel: "sonnet",
        EffortFlag: "--effort",
        DefaultEffort: "low");

    public static readonly AgentCliVariant CodexCli = new(
        Id: "codex-cli",
        Executable: "codex",
        BaseArguments: ["exec", "-"],
        DisplayName: "Codex",
        ModelFlag: "-m",
        DefaultModel: "gpt-5.4-mini",
        EffortFlag: null,
        DefaultEffort: null);

    public static readonly AgentCliVariant CopilotCli = new(
        Id: "copilot-cli",
        Executable: "copilot",
        BaseArguments: ["--print"],
        DisplayName: "GitHub Copilot",
        ModelFlag: "--model",
        DefaultModel: "gpt-5.4-mini",
        EffortFlag: "--effort",
        DefaultEffort: "low");

    /// <summary>All known variants in <c>auto</c>-mode preference order.</summary>
    public static readonly IReadOnlyList<AgentCliVariant> AllInAutoOrder = [ClaudeCli, CodexCli, CopilotCli];

    /// <summary>Look up by <see cref="Id"/>; returns null when unrecognised.</summary>
    public static AgentCliVariant? TryFromId(string id)
    {
        foreach (var v in AllInAutoOrder)
            if (v.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    /// <summary>
    /// Builds the final argv for one Tier 2 invocation by appending the resolved model
    /// and effort flags after <see cref="BaseArguments"/>.
    ///
    /// <para>Override semantics for both <paramref name="modelOverride"/> and
    /// <paramref name="effortOverride"/>:
    /// <list type="bullet">
    ///   <item><c>null</c> → use the variant's <see cref="DefaultModel"/> / <see cref="DefaultEffort"/> (the cheap-tier cost cap).</item>
    ///   <item>empty string → explicitly suppress the flag, leaving the host CLI's own default in effect.</item>
    ///   <item>any other value → pass that value verbatim after the corresponding flag.</item>
    /// </list>
    /// A flag is also suppressed when the variant has no corresponding flag declared
    /// (e.g. Codex has no <see cref="EffortFlag"/>), so passing an effort override to
    /// Codex is silently ignored rather than producing an invalid argv.</para>
    /// </summary>
    public IReadOnlyList<string> BuildArguments(string? modelOverride, string? effortOverride)
    {
        var args = new List<string>(BaseArguments);

        string? model = Resolve(modelOverride, DefaultModel);
        if (ModelFlag is { Length: > 0 } && !string.IsNullOrEmpty(model))
        {
            args.Add(ModelFlag);
            args.Add(model);
        }

        string? effort = Resolve(effortOverride, DefaultEffort);
        if (EffortFlag is { Length: > 0 } && !string.IsNullOrEmpty(effort))
        {
            args.Add(EffortFlag);
            args.Add(effort);
        }

        return args;
    }

    private static string? Resolve(string? overrideValue, string? defaultValue) =>
        overrideValue switch
        {
            null => defaultValue,           // not configured → use variant default
            "" => null,                     // explicit empty string → suppress flag entirely
            _ => overrideValue,             // explicit value → pass it through verbatim
        };
}
