using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public sealed class ReservedPathDispatcherTests
{
    public static IEnumerable<object[]> OperationMatrix()
    {
        foreach (ReservedPathOperation operation in Enum.GetValues<ReservedPathOperation>())
        {
            yield return [operation, "/skill/example"];
            yield return [operation, "/agent/example"];
            yield return [operation, "/ordinary/example"];
        }
    }

    [Fact]
    public async Task OrdinaryPaths_ReturnNullForStoreHandling()
    {
        using var scope = new TestHelpers.StoreScope();
        var dispatcher = new ReservedPathDispatcher(new ScriniaMcpTools());

        McpResponse? response = await dispatcher.DispatchAsync(
            ReservedPathOperation.Read,
            MemoryAddress.Parse("/ordinary/example"),
            new ReservedPathRequest(null, null, false, false),
            CancellationToken.None);

        response.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(UnsupportedOperations))]
    public async Task ReservedPaths_RejectUnsupportedOperations(int operationValue, string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var dispatcher = new ReservedPathDispatcher(new ScriniaMcpTools());
        ReservedPathOperation operation = (ReservedPathOperation)operationValue;

        McpResponse? response = await dispatcher.DispatchAsync(
            operation,
            MemoryAddress.Parse(path),
            new ReservedPathRequest(null, null, false, false),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.ErrorCode.Should().Be(ErrorCodes.ReservedPathOperationForbidden);
    }

    public static IEnumerable<object[]> UnsupportedOperations()
    {
        ReservedPathOperation[] unsupported =
        [
            ReservedPathOperation.Copy,
            ReservedPathOperation.Export,
            ReservedPathOperation.Import,
            ReservedPathOperation.Compact,
        ];
        foreach (ReservedPathOperation operation in unsupported)
        {
            yield return [(int)operation, "/skill/example"];
            yield return [(int)operation, "/agent/example"];
        }
    }

    [Theory]
    [InlineData("/skill")]
    [InlineData("/agent")]
    public async Task ListReservedNamespace_ReturnsReservedListing(string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var dispatcher = new ReservedPathDispatcher(new ScriniaMcpTools());

        McpResponse? response = await dispatcher.DispatchAsync(
            ReservedPathOperation.List,
            MemoryAddress.Parse(path),
            new ReservedPathRequest(null, null, false, false),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task CompactAgent_IsRefusedBeforeOrdinaryStoreFallback()
    {
        using var scope = new TestHelpers.StoreScope();
        string result = await new ScriniaMcpTools().MemoryCompact("/agent/example");

        result.Should().Contain(ErrorCodes.ReservedPathOperationForbidden);
        TestHelpers.Store.ListScoped(["local"], null)
            .Should().NotContain(entry => entry.Entry.Name.Equals("example", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AgentRoot_ReadListsAgentsInsteadOfFallingThrough()
    {
        using var scope = new TestHelpers.StoreScope();
        string agentDir = PathRouter.ToFileEntityDir("agent", TestHelpers.Store.WorkspaceRoot);
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "reviewer.md"), "instructions");

        McpResponse? response = await new ReservedPathDispatcher(new ScriniaMcpTools()).DispatchAsync(
            ReservedPathOperation.Read,
            MemoryAddress.Parse("/agent"),
            new ReservedPathRequest(null, null, false, false),
            CancellationToken.None);

        response!.IsError.Should().BeFalse();
        response.Content.Should().Contain("/agent/reviewer");
    }

    [Fact]
    public async Task DeleteAgent_RemovesBodySidecarAndVersions()
    {
        using var scope = new TestHelpers.StoreScope();
        string agentDir = PathRouter.ToFileEntityDir("agent", TestHelpers.Store.WorkspaceRoot);
        Directory.CreateDirectory(Path.Combine(agentDir, "versions"));
        File.WriteAllText(Path.Combine(agentDir, "reviewer.md"), "instructions");
        File.WriteAllText(Path.Combine(agentDir, "reviewer.meta.json"), "{}");
        File.WriteAllText(Path.Combine(agentDir, "versions", "reviewer_20261001.md"), "old");
        File.WriteAllText(Path.Combine(agentDir, "versions", "other_20261001.md"), "keep");

        var dispatcher = new ReservedPathDispatcher(new ScriniaMcpTools());
        McpResponse? response = await dispatcher.DispatchAsync(
            ReservedPathOperation.Delete,
            MemoryAddress.Parse("/agent/reviewer"),
            new ReservedPathRequest(null, null, false, false),
            CancellationToken.None);

        response!.IsError.Should().BeFalse();
        File.Exists(Path.Combine(agentDir, "reviewer.md")).Should().BeFalse();
        File.Exists(Path.Combine(agentDir, "reviewer.meta.json")).Should().BeFalse();
        string versionsDir = Path.Combine(agentDir, "versions");
        File.Exists(Path.Combine(versionsDir, "reviewer_20261001.md")).Should().BeFalse();
        File.Exists(Path.Combine(versionsDir, "other_20261001.md")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAgent_RemovesOrphanedSidecarWithoutBody()
    {
        using var scope = new TestHelpers.StoreScope();
        string agentDir = PathRouter.ToFileEntityDir("agent", TestHelpers.Store.WorkspaceRoot);
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "orphan.meta.json"), "{}");

        McpResponse? response = await new ReservedPathDispatcher(new ScriniaMcpTools()).DispatchAsync(
            ReservedPathOperation.Delete,
            MemoryAddress.Parse("/agent/orphan"),
            new ReservedPathRequest(null, null, false, false),
            CancellationToken.None);

        response!.IsError.Should().BeFalse();
        File.Exists(Path.Combine(agentDir, "orphan.meta.json")).Should().BeFalse();
    }
}
