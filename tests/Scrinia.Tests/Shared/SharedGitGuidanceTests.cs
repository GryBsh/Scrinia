using System.Text;
using FluentAssertions;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Task 8 — operation-based Git guidance. Guidance is emitted only when an authoritative mutation is
/// applied and its manifest is nonempty (§7.2); it is the exact one staging command with every
/// manifest path as an individually shell-quoted argument, followed by the fixed commit and push.
/// Scrinia never claims Git state and never substitutes a directory pathspec or derived path.
/// </summary>
public sealed class SharedGitGuidanceTests
{
    [Fact]
    public void Git_guidance_is_operation_based_and_shell_quotes_each_manifest_path()
    {
        string root = "/srv/team-memory";
        string[] manifest =
        [
            ".scrinia/memories/local-topic-memory-patterns/review.nmp2",
            ".scrinia/memories/local-topic-memory-patterns/review.nmp2.meta.json",
        ];

        string? guidance = SharedGitGuidance.Render(root, manifest);

        string expected = string.Join('\n',
            $"git -C \"{root}\" add -A -- '{manifest[0]}' '{manifest[1]}'",
            $"git -C \"{root}\" commit -m \"docs(memory): update shared Scrinia memory\"",
            $"git -C \"{root}\" push");

        guidance.Should().Be(expected);
    }

    [Fact]
    public void No_op_and_empty_manifest_emit_no_staging_command()
    {
        SharedGitGuidance.Render("/srv/team-memory", []).Should().BeNull();
    }

    [Fact]
    public void Every_manifest_path_is_an_individually_shell_quoted_argument()
    {
        string addLine = SharedGitGuidance.Render("/srv/team-memory",
            [".scrinia/memories/a.nmp2", ".scrinia/memories/b.nmp2"])!.Split('\n')[0];

        addLine.Should().Contain("'.scrinia/memories/a.nmp2'").And.Contain("'.scrinia/memories/b.nmp2'");
        addLine.Should().NotContain("add -A -- '.scrinia'"); // the store directory is never a bare directory pathspec
    }

    [Fact]
    public void Guidance_never_claims_git_state()
    {
        string guidance = SharedGitGuidance.Render("/srv/team-memory", [".scrinia/memories/a.nmp2"]);

        // The guidance prints exact commands only; it never asserts the resulting Git state or that a
        // command already ran (§7.2 / §8). Every mutable verb below is a state claim and must be absent.
        string[] forbiddenWords = ["tracked", "committed", "pushed", "clean", "synchronized", "already", "staged"];
        foreach (string forbidden in forbiddenWords)
            guidance.Should().NotContain(forbidden);
    }

    [Fact]
    public void Embedded_single_quote_in_a_path_round_trips_through_posix_quoting()
    {
        string addLine = SharedGitGuidance.Render("/srv", [".scrinia/memories/a.b.nmp2".Replace("a.b", "a'b")])!.Split('\n')[0];

        // POSIX single-quote escaping wraps the token and represents the embedded quote as '"'"'
        addLine.Should().Contain(".scrinia/memories/a'\"'\"'b.nmp2'");
    }

    [Fact]
    public void Root_escapes_double_quote_backslash_and_command_substitution_characters()
    {
        string root = "C:\\shared root\\quoted\" $HOME `date`";

        string addLine = SharedGitGuidance.Render(root, [".scrinia/memory.nmp2"])!.Split('\n')[0];

        addLine.Should().Be("""git -C "C:\\shared root\\quoted\" \$HOME \`date\`" add -A -- '.scrinia/memory.nmp2'""");
    }
}
