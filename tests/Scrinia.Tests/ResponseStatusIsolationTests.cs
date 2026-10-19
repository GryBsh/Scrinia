using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// A memory body is data. It must never be able to impersonate the response envelope.
///
/// ToYaml emits content as `content: |` with each line indented two spaces and no escaping,
/// so a body line reading "status: error" is a substring of the rendered document. Every
/// consumer that greps the rendered string therefore misreads a successful response as a
/// failure. These tests pin the consumers, not the format.
/// </summary>
public class ResponseStatusIsolationTests
{
    private const string Poison = "When the tool fails you will see status: error in the envelope.";

    [Fact]
    public async Task Recall_OfBodyContainingStatusError_IsStillSuccess()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();

        await tools.MemoryRemember("/findings/response-format", [Poison], description: "Test memory.");
        string yaml = await tools.MemoryRecall("/findings/response-format");

        yaml.Should().StartWith("status: success");
    }

    /// <summary>
    /// Green both before and after this branch, not a regression guard. <c>Restore</c> never folds
    /// a memory body into <c>ContentSections</c> (see <see cref="CliLink_WithPoisonedReason_ExitsZero"/>
    /// below for the reachable case) — <c>ContentSections</c> only ever carries the "Skills available"
    /// line or the empty-state fallback, so no poisoned memory body can reach this envelope. Kept as
    /// documentation that the substring-coupling defect does not apply to <c>scri restore</c>.
    /// </summary>
    [Fact]
    public async Task CliRestore_WithPoisonedMemory_ExitsZero()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/patterns/response-format", [Poison], description: "Test memory.");

        int exitCode = await new ScriniaCommands().Restore(workspaceRoot: scope.WorkspaceDir);

        exitCode.Should().Be(0, "the memory body is data, not the response envelope");
    }

    /// <summary>
    /// The reachable form of the <c>EmitMcpResult</c> defect. <c>scri restore</c> turned out not to
    /// be able to express it — <c>BuildRestoreContextAsync</c> only ever puts the skills line into
    /// <c>ContentSections</c>, never a memory body, so no memory path can poison that envelope.
    /// <c>scri link --reason</c> can: the reason is echoed verbatim into the success content, and
    /// <c>EmitMcpResult</c> greps the rendered document for "status: error", so a fully successful
    /// link exits 1.
    /// </summary>
    [Fact]
    public async Task CliLink_WithPoisonedReason_ExitsZero()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/patterns/link-source", ["source"], description: "source");
        await tools.MemoryRemember("/patterns/link-target", ["target"], description: "target");

        int exitCode = await new ScriniaCommands().Link("/patterns/link-source", "/patterns/link-target",
            reason: Poison, workspaceRoot: scope.WorkspaceDir);

        exitCode.Should().Be(0, "the link succeeded; the reason text is data, not the response envelope");
    }

    /// <summary>
    /// The mirror defect, found while planning this branch. ScriniaCommands.Memory.cs:406 and
    /// :449 guard with `result.StartsWith("Error:")` — a shape ToYaml has never produced, since
    /// every envelope starts with `status: `. Both branches are dead, so `scri show` and
    /// `scri forget` report success on a genuine not-found.
    /// </summary>
    [Fact]
    public async Task CliShow_OfMissingMemory_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();

        int exitCode = await new ScriniaCommands().Show("/findings/does-not-exist",
            workspaceRoot: scope.WorkspaceDir);

        exitCode.Should().Be(1, "a not-found recall is a failure, not a success");
    }

    /// <summary>
    /// Reporting the failure must not cost the user the recovery hints. `scri show` and
    /// `scri forget` print the error message followed by one indented bullet per actionNeeded
    /// entry; this pins the formatter rather than the console, so it does not depend on
    /// redirecting process-wide output from a parallel test host.
    /// </summary>
    [Fact]
    public void CliErrorFormat_KeepsTheRecoveryHintsBeneathTheMessage()
    {
        McpResponse notFound = ResponseBuilder.Error(
            "Memory '/findings/does-not-exist' not found.",
            ErrorCodes.NotFound,
            "list() to see available memories",
            "search({ query: '...' }) to find by keyword");

        ScriniaCommands.FormatCliError(notFound).Should().Be(
            "Memory '/findings/does-not-exist' not found.\n"
            + "  - list() to see available memories\n"
            + "  - search({ query: '...' }) to find by keyword");
    }

    [Fact]
    public void CliErrorFormat_WithNoHints_IsJustTheMessage()
    {
        ScriniaCommands.FormatCliError(ResponseBuilder.Error("boom"))
            .Should().Be("boom");
    }

    [Fact]
    public void SuccessWithWarning_IsRecognizedAsWarning()
    {
        ResponseBuilder.SuccessWithWarning("committed with a warning").IsWarning.Should().BeTrue();
    }
}
