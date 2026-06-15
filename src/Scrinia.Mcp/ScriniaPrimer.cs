namespace Scrinia.Mcp;

/// <summary>
/// The canonical short "memories exist — here's when to use them" baseline. Single source of truth
/// shared by everything that needs to state the baseline: the CLI's AGENTS.md/CLAUDE.md writers and
/// the MCP server <c>instructions</c> field. Lives in <c>Scrinia.Mcp</c> because both the CLI and
/// <c>Scrinia.Server</c> reference this assembly. Deliberately terse — it points at <c>guide()</c>
/// for the full primer rather than duplicating the ~164-line agent guide (<c>prompts/guide.md</c>).
/// </summary>
public static class ScriniaPrimer
{
    /// <summary>
    /// Markdown body for the AGENTS.md / CLAUDE.md managed block.
    /// </summary>
    public const string MarkdownBody =
        """
        ## Scrinia memory

        This workspace uses **Scrinia** for persistent, project-scoped memory via MCP tools. The
        full agent guide is one call away — call `guide()` at the start of a session.

        - `memory_search(...)` before non-trivial work (joining the project, recalling prior
          decisions or conventions); skip it for self-contained requests.
        - `memory_remember(...)` as soon as you learn something non-obvious — a decision, a fix, a
          convention the next agent shouldn't have to rediscover.
        - Reserved paths: `/skill/`, `/agent/`, `/patterns/`, `/findings/`, `/learn/`,
          `/sessions/`, `/checkpoint/`, `/temp/`.
        - `.scrinia/` changes are part of your commits.

        Call `guide()` for the full primer — chunks, skills, typed keywords, and recovery.
        """;

    /// <summary>
    /// Single-line-dense variant for the MCP server <c>instructions</c> field, where the first
    /// ~512 chars matter most on tools-only CLIs (Codex/Copilot read this as server guidance even
    /// though they ignore MCP prompts/resources). Kept self-contained.
    /// </summary>
    public const string McpInstructions =
        "This server provides Scrinia, persistent project-scoped memory. Call guide() at session " +
        "start for the full primer. Use memory_search before non-trivial work to recall prior " +
        "decisions and conventions; use memory_remember as soon as you learn something non-obvious. " +
        "Reserved paths: /skill/ /agent/ /patterns/ /findings/ /learn/ /sessions/ /checkpoint/ /temp/.";
}
