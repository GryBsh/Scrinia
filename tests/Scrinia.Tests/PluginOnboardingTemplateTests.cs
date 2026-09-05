using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Phase 4, Task 4 — OpenCode <c>save</c>→<c>hint</c> transform. The OpenCode plugin has no
/// shell-hook channel, so its only path to the fail-silent advisory is the prompt-aware
/// <c>experimental.chat.messages.transform</c> hook. This guards the whole embedded template:
/// the transform must shell to <c>scri hint --client opencode</c>, pipe the prompt on stdin, and
/// carry neither the retired <c>scri save</c> invocation nor any <c>child_process</c> spawn.
/// </summary>
public sealed class PluginOnboardingTemplateTests
{
    private static string Template() =>
        EmbeddedPrompts.Load("plugins/opencode/scrinia.ts")
        ?? throw new InvalidOperationException(
            "OpenCode plugin template is not embedded in Scrinia.Mcp");

    [Fact]
    public void Template_IsEmbedded() => Template().Should().NotBeEmpty();

    [Fact]
    public void Transform_InvokesHint_OnOpencode_Client()
    {
        // The template shells through the resolved scri path (no literal "scri" token); the
        // client-tailored advisory argument is the exact --client opencode form.
        Template().Should().Contain("hint --client opencode",
            "the per-turn transform routes through the fail-silent scri hint advisory, not save");
    }

    [Fact]
    public void Transform_SendsPrompt_ThroughStdin()
    {
        Template().Should().Contain("< ${prompt}",
            "the prompt is piped to scri hint on stdin — the bounded fail-silent transport, never a spawn arg");
    }

    [Fact]
    public void Transform_ContainsNoSaveOrSpawn()
    {
        string ts = Template();
        ts.Should().NotContain("save",
            "the retired scri save invocation is replaced by scri hint");
        ts.Should().NotContain("spawn",
            "the alternate child_process spawn block is removed");
        ts.Should().NotContain("child_process",
            "the transport is stdin-only — no node:child_process spawn of scri");
    }
}
