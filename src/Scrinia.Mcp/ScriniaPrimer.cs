namespace Scrinia.Mcp;

/// <summary>
/// The canonical short "memories exist — here's when to use them" baseline. Single source of truth
/// shared by everything that needs to state the baseline: the CLI's AGENTS.md/CLAUDE.md writers and
/// the MCP server <c>instructions</c> field. Lives in <c>Scrinia.Mcp</c> because both the CLI and
/// the CLI reference this assembly. Deliberately terse — it points at <c>guide()</c>
/// for the full workflow rather than duplicating the agent guide (<c>prompts/guide.md</c>).
/// </summary>
public static class ScriniaPrimer
{
    /// <summary>
    /// Markdown body for the AGENTS.md / CLAUDE.md managed block.
    /// </summary>
    public const string MarkdownBody =
        """
        ## Scrinia memory

        This workspace uses **Scrinia** for persistent, workspace-scoped memory via MCP tools. The
        full agent guide is one call away — call `guide()` at the start of a session.

        - `search(...)` when prior context could change the work, including earlier decisions,
          preferences, or events; skip it for self-contained requests.
        - `remember(...)` for useful information that should survive the session — a decision,
          preference, event, correction, finding, or open task. Read relevant evidence before relying on it.
        - Reserved paths: `/skill/`, `/agent/`, `/patterns/`, `/findings/`, `/learn/`,
          `/sessions/`, `/checkpoint/`, `/temp/`.
        - Follow the configured tracking policy and the user's sharing intent for `.scrinia/`.
          Writing a memory does not authorize committing or publishing it.
        - Supported agent hooks run `scri hint --client <host>` before commit or push (the
          `UserPromptSubmit` hook) to surface relevant memories; the shared MCP server takes no
          prompt or correlation field — advisories come from the hook only.

        Call `guide()` for the full primer — chunks, skills, attributes, and recovery.
        """;

    /// <summary>
    /// Single-line-dense variant for the MCP server <c>instructions</c> field, where the first
    /// ~512 chars matter most on tools-only CLIs (Codex/Copilot read this as server guidance even
    /// though they ignore MCP prompts/resources). Kept self-contained.
    /// </summary>
    public const string McpInstructions =
        "This server provides Scrinia, persistent workspace-scoped memory. Call guide() at session " +
        "start for the full workflow. Search when prior context could affect the task; recall relevant evidence before relying on it. " +
        "Remember useful preferences, events, decisions, corrections, and open work. Follow the user's retention and sharing intent. " +
        "Reserved paths: /skill/ /agent/ /patterns/ /findings/ /learn/ /sessions/ /checkpoint/ /temp/.";
}
