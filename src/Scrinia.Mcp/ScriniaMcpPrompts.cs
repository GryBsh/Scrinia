using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Scrinia.Mcp;

/// <summary>
/// MCP prompts exposed by the Scrinia server. On clients that support MCP prompts (Claude Code,
/// Gemini, OpenCode) these surface as slash commands (e.g. <c>/mcp__scrinia__guide</c>), giving the
/// user an explicit way to pull the guide into context.
///
/// <para>MCP prompts are <b>user-invoked, not model-invoked</b> — they don't drive autonomous
/// behavior. The autonomous path stays the <c>guide()</c> <em>tool</em> (works on every client) and
/// the SKILL.md skill; this prompt is the slash-command convenience layer on top.</para>
/// </summary>
[McpServerPromptType]
public sealed class ScriniaMcpPrompts
{
    [McpServerPrompt(Name = "guide"), Description(
        "Load the Scrinia agent guide — tools, search-before-work strategy, conventions, reserved " +
        "paths, chunks, skills, and recovery.")]
    public static string Guide() =>
        EmbeddedPrompts.LoadGuide()
        ?? "Scrinia guide is unavailable (embedded resource missing).";
}
