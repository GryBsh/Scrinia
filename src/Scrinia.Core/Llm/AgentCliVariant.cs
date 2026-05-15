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
/// </summary>
/// <param name="Id">Stable provider value used in <c>Scrinia:Llm:Provider</c> config (e.g. <c>claude-cli</c>).</param>
/// <param name="Executable">Bare exe name probed on PATH (<c>claude</c>, <c>codex</c>, <c>copilot</c>).</param>
/// <param name="Arguments">Argv after the executable. Triggers non-interactive print mode.</param>
/// <param name="DisplayName">Human-readable label for logs / setup prompts.</param>
public sealed record AgentCliVariant(
    string Id,
    string Executable,
    IReadOnlyList<string> Arguments,
    string DisplayName)
{
    public static readonly AgentCliVariant ClaudeCli = new(
        Id: "claude-cli",
        Executable: "claude",
        Arguments: ["-p"],
        DisplayName: "Claude Code");

    public static readonly AgentCliVariant CodexCli = new(
        Id: "codex-cli",
        Executable: "codex",
        Arguments: ["exec", "-"],
        DisplayName: "Codex");

    public static readonly AgentCliVariant CopilotCli = new(
        Id: "copilot-cli",
        Executable: "copilot",
        Arguments: ["--print"],
        DisplayName: "GitHub Copilot");

    /// <summary>All known variants in <c>auto</c>-mode preference order.</summary>
    public static readonly IReadOnlyList<AgentCliVariant> AllInAutoOrder = [ClaudeCli, CodexCli, CopilotCli];

    /// <summary>Look up by <see cref="Id"/>; returns null when unrecognised.</summary>
    public static AgentCliVariant? TryFromId(string id)
    {
        foreach (var v in AllInAutoOrder)
            if (v.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }
}
