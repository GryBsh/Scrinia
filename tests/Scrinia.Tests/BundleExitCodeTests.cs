using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// <c>scri bundle export</c> and <c>scri bundle import</c> guarded their exit code with
/// <c>result.StartsWith("Error:", ...)</c> against <c>response.ToYaml()</c> — a shape the
/// renderer has never produced, since every envelope opens with <c>status: </c>. Both commands
/// therefore exited 0 on a genuine failure. These tests pin the fix (typed <see cref="McpResponse.IsError"/>)
/// on both sides: failure now exits 1. The pre-existing success path still exits 0, but the
/// warning path does not — an empty or fully-filtered import is also treated as a failure now
/// (see <see cref="Import_OnEmptyBundle_ExitsNonZero"/> below and <see cref="McpResponse.IsWarning"/>).
/// </summary>
public class BundleExitCodeTests
{
    [Fact]
    public async Task Export_OnGenuineFailure_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();

        // Nonexistent topic -> ResponseBuilder.Error(..., ErrorCodes.NotFound, ...), reached
        // because no entries exist for the topic (MemoryTools.Bundling.cs:66-69).
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
    /// RULED 2026-08-10: an import that matched no topics is a failure, not a success.
    ///
    /// <para>This test previously asserted exit 0 and said, in as many words, that if someone
    /// later decided an empty import was a failure, this was the place to record it as a
    /// decision. That decision has now been taken: a caller that asked for topics and received
    /// none has had its request silently not-performed, and reporting exit 0 makes a CI step
    /// gating on it pass. The same ruling applies to every <c>warning</c> envelope — see
    /// <see cref="McpResponse.IsWarning"/>.</para>
    ///
    /// <para>Breaking for anything gating on exit 0; recorded in CHANGELOG.md.</para>
    /// </summary>
    [Fact]
    public async Task Import_OnEmptyBundle_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["auth flow doc"], "/api/auth");
        string exportContent = ResponseParser.Parse(
            (await new ScriniaMcpTools().Export(["api"])).ToYaml()).Content!;
        string bundlePath = exportContent[(exportContent.LastIndexOf(") to ", StringComparison.Ordinal) + 5)..];

        // Filter on a topic the bundle does not contain -> topicCount == 0, skippedCategories
        // empty -> ResponseBuilder.Warning("No topics were imported ...").
        int exit = await new ScriniaCommands().Import(
            bundlePath, workspaceRoot: scope.WorkspaceDir, topics: "does-not-match-anything");

        exit.Should().Be(1, "an import that matched nothing must not report success to a CI step");
    }

    [Fact]
    public async Task Export_OnSuccess_ExitsZero()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["auth flow doc"], "/api/auth");

        int exit = await new ScriniaCommands().Export("api", workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(0);
    }

    [Fact]
    public void IsWarning_IsTrueOnlyForTheWarningStatus()
    {
        ResponseBuilder.Warning("x").IsWarning.Should().BeTrue();
        ResponseBuilder.Success("x").IsWarning.Should().BeFalse();
        ResponseBuilder.Error("x").IsWarning.Should().BeFalse();
        ResponseBuilder.Warning("x").IsError.Should().BeFalse(
            because: "a warning is not an error — the two predicates must not overlap");
    }

    /// <summary>
    /// `scri store` had no IsError guard at all and returned 0 unconditionally — the same dead-guard
    /// defect already fixed at Show, Forget, Export and Import.
    /// </summary>
    [Fact]
    public async Task Store_OnGenuineFailure_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();
        string contentFile = Path.Combine(scope.TempDir, "content.txt");
        await File.WriteAllTextAsync(contentFile, "some content");

        // A space is not a valid FileEntityName character, so the /agent/ route rejects this with
        // ResponseBuilder.Error(..., INVALID_PARAMETER) at MemoryTools.Core.cs:273-274 — before the
        // IMemoryStore check below it, so the verdict does not depend on the store mode.
        int exit = await new ScriniaCommands().Store(
            "/agent/bad name", contentFile, workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(1);
    }
}
