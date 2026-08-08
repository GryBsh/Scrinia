using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// <c>scri bundle export</c> and <c>scri bundle import</c> guarded their exit code with
/// <c>result.StartsWith("Error:", ...)</c> against <c>response.ToYaml()</c> — a shape the
/// renderer has never produced, since every envelope opens with <c>status: </c>. Both commands
/// therefore exited 0 on a genuine failure. These tests pin the fix (typed <see cref="McpResponse.IsError"/>)
/// on both sides: failure now exits 1, and the pre-existing success/warning paths still exit 0.
/// </summary>
public class BundleExitCodeTests
{
    [Fact]
    public async Task Export_OnGenuineFailure_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();

        // Nonexistent topic -> ResponseBuilder.Error(..., ErrorCodes.NotFound, ...) per F5
        // (MemoryTools.Bundling.cs:66-69, reached because no entries exist for the topic).
        int exit = await new ScriniaCommands().Export("nonexistent-topic", workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(1, "a failed export must not report success to a shell or CI step");
    }

    [Fact]
    public async Task Import_OnGenuineFailure_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();

        // A bundle path that does not exist -> "Bundle file not found" error envelope
        // (MemoryTools.Bundling.cs:109-113).
        int exit = await new ScriniaCommands().Import(
            "does-not-exist.scrinia-bundle", workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(1);
    }

    /// <summary>
    /// Status is "warning", not "error" (F4). Pinning today's behaviour deliberately: see "The
    /// decision" in the task brief. If someone later decides an empty import is a failure, this
    /// test is the place that records it was a decision and not an accident.
    /// </summary>
    [Fact]
    public async Task Import_OnEmptyBundle_ExitsZero()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["auth flow doc"], "/api/auth");
        string exportContent = ResponseParser.Parse(
            (await new ScriniaMcpTools().Export(["api"])).ToYaml()).Content!;
        string bundlePath = exportContent[(exportContent.LastIndexOf(") to ", StringComparison.Ordinal) + 5)..];

        // Filter on a topic the bundle does not contain -> topicCount == 0, skippedCategories
        // empty -> ResponseBuilder.Warning("No topics were imported ...") per F4.
        int exit = await new ScriniaCommands().Import(
            bundlePath, workspaceRoot: scope.WorkspaceDir, topics: "does-not-match-anything");

        exit.Should().Be(0);
    }

    [Fact]
    public async Task Export_OnSuccess_ExitsZero()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["auth flow doc"], "/api/auth");

        int exit = await new ScriniaCommands().Export("api", workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(0);
    }
}
