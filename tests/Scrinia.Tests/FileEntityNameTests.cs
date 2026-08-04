using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class FileEntityNameTests
{
    [Theory]
    [InlineData("api-reviewer")]
    [InlineData("auth_researcher")]
    [InlineData("v1.helper")]
    public void AcceptsSafeSlugs(string name) => FileEntityName.IsValid(name).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows")]
    [InlineData("a..b")]
    public void RejectsTraversalAndSeparators(string? name) => FileEntityName.IsValid(name).Should().BeFalse();

    // ── Regression: a traversal /agent/ write is rejected end-to-end ────────

    [Fact]
    public async Task Remember_AgentPathTraversal_ReturnsErrorAndWritesNoFileOutsideAgentDir()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await new ScriniaMcpTools().Remember(["malicious content"], "/agent/../../evil");
        var parsed = ResponseParser.Parse(result);

        parsed.Status.Should().Be("error",
            because: "a traversal agent name must be rejected before any file is touched");

        File.Exists(Path.Combine(scope.WorkspaceDir, "evil.md")).Should().BeFalse(
            because: "the traversal must not escape the .scrinia/agent directory");
        File.Exists(Path.Combine(Path.GetTempPath(), "evil.md")).Should().BeFalse(
            because: "the traversal must not escape further up the directory tree either");
    }
}
