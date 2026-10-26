using System.Text;
using FluentAssertions;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// Phase 4, Task 5 — the generated agent primer points at the hook-only
/// <c>scri hint --client &lt;host&gt;</c> advisory, MCP instructions introduce the memory workflow,
/// and the retired multi-user merge workflow is
/// replaced with shared-base collaboration across the owned living docs.
/// </summary>
public sealed class HookAdvisoryOnboardingTests
{
    private const string HintHookWording = "scri hint --client <host>";

    [Fact]
    public void Primer_Onboarding_PointsAtHookOnlyHintAdvisory()
    {
        // The generated agent primer must route the commit/push advisory through the hook-only
        // scri hint surface, never through a shared MCP prompt field (spec §7.3).
        ScriniaPrimer.MarkdownBody.Should().Contain(HintHookWording,
            "the agent primer points at the fail-silent scri hint hook advisory");
        ScriniaPrimer.MarkdownBody.Should().Contain("UserPromptSubmit",
            "the primer names the hook event that drives the advisory");
    }

    [Fact]
    public void Primer_McpInstructions_DirectsAgentsToTheMemoryWorkflow()
    {
        string instructions = ScriniaPrimer.McpInstructions;
        instructions.Should().Contain("guide()", "the full guide is the operational entry point");
        instructions.Should().Contain("workspace-scoped", "persistence does not require a shared repository");
        instructions.Should().Contain("retention and sharing", "storage is subject to user intent");
        foreach (string path in new[] { "/skill/", "/agent/", "/patterns/", "/findings/", "/learn/", "/sessions/", "/checkpoint/", "/temp/" })
            instructions.Should().Contain(path);
    }

    [Fact]
    public void MultiUserSetup_IsSharedBaseCollaborationNotMergeWorkflow()
    {
        string text = Read("docs", "multi-user-setup.md");

        // Shared-base collaboration is the subject of the replacement.
        text.Should().Contain("shared base", "collaboration is described through shared bases");
        text.Should().Contain("project-git", "shared bases are fixed to project-git");
        text.Should().Contain("add -A", "Phase 3's exact authoritative staging command is preserved");

        // The retired merge/reconcile/multi-user-workflow claims are gone.
        text.Should().NotContain("reconcile", "reconcile workflow is retired");
        text.Should().NotContain("scri-merge", "scri-merge binary claim is retired");
        text.Should().NotContain("--multi-user", "setup --multi-user is retired");
        text.Should().NotContain("merge driver", "merge-driver setup is retired");
    }

    [Fact]
    public void AddressFacingOnboarding_UsesCurrentCommandsAndCanonicalForms()
    {
        string readme = Read("README.md");
        string gettingStarted = Read("docs", "getting-started.md");
        string security = Read("docs", "security.md");
        string guide = EmbeddedPrompts.LoadGuide()
            ?? throw new InvalidOperationException("guide.md is not embedded in Scrinia.Mcp");

        foreach (string text in new[] { readme, gettingStarted, security, guide })
        {
            text.Should().Contain("scri shared base add", "current shared-base registration must be discoverable");
            text.Should().Contain("project-git", "shared bases use the fixed tracking policy");
            text.Should().MatchRegex("/topic/(?:name|subject)", "persistent guidance uses canonical topic paths");
            text.Should().MatchRegex("~(?:name|subject)", "ephemeral guidance uses canonical tilde names");
            text.Should().NotContain("scri reconcile", "the retired reconcile command must not be advertised");
            text.Should().NotContain("scri-merge", "the retired merge binary must not be advertised");
            text.Should().NotContain("--multi-user", "the retired setup mode must not be advertised");
        }
    }

    [Fact]
    public void OwnedDocs_ExerciseDisjointPhase4Phase5Ownership()
    {
        string readme = Read("README.md");
        string gettingStarted = Read("docs", "getting-started.md");
        string security = Read("docs", "security.md");
        string guide = EmbeddedPrompts.LoadGuide()
            ?? throw new InvalidOperationException("guide.md is not embedded in Scrinia.Mcp");

        // README: Phase 4 shared-onboarding heading (## MCP setup) collaborates on shared bases;
        // Phase 5 installation heading (## Install) is untouched and owns build/publish.
        Section(readme, "## MCP setup").Should().Contain("shared base", "README shared onboarding points at shared-base collaboration");
        Section(readme, "## MCP setup").Should().NotContain("merge driver", "README onboarding no longer claims a merge driver");
        Section(readme, "## Install").Should().Contain("dotnet build", "README Phase 5 installation heading is intact");

        // getting-started: Phase 4 "## How it runs" collaborates on shared bases; Phase 5
        // "## Installation" owns build-from-source.
        Section(gettingStarted, "## How it runs").Should().Contain("shared base", "getting-started shared onboarding points at shared-base collaboration");
        Section(gettingStarted, "## How it runs").Should().NotContain("resolve file conflicts", "getting-started no longer describes manual conflict resolution");
        Section(gettingStarted, "## Installation").Should().Contain("Build from Source", "getting-started Phase 5 installation heading is intact");

        // security: Phase 4 shared-threat heading covers hook-only advisory + shared MCP prompt
        // isolation; Phase 5 plugin/model headings remain owned and untouched.
        Section(security, "### Configuration and shared-base boundaries").Should().Contain("project-git", "security shared-base threat keeps the fixed policy");
        Section(security, "### Plugin trust").Should().Contain("child process", "security Phase 5 plugin-truth heading is intact");

        // guide: Phase 4 shared-base collaboration present; no merge/reconcile workflow.
        guide.Should().Contain("shared base", "guide.md shared-base collaboration is present");
        guide.Should().NotContain("reconcile", "guide.md no longer teaches a reconcile workflow");
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(RepoPaths.Combine(segments), Encoding.UTF8);

    private static string Section(string markdown, string startHeading)
    {
        int start = markdown.IndexOf(startHeading, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        int next = markdown.IndexOf("\n## ", start + startHeading.Length, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }
}
