using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Commands;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public sealed class CliReservedPathParityTests
{
    [Fact]
    public async Task CliAndMcp_ApplyTheSameReservedPathPolicy()
    {
        using var scope = new TestHelpers.StoreScope();
        var dispatcher = new ReservedPathDispatcher(new ScriniaMcpTools());
        var request = new ReservedPathRequest(null, null, false, false);

        McpResponse? mcp = await dispatcher.DispatchAsync(
            ReservedPathOperation.Copy, MemoryAddress.Parse("/skill/example"), request, CancellationToken.None);
        McpResponse? cli = await dispatcher.DispatchAsync(
            ReservedPathOperation.Copy, MemoryAddress.Parse("/skill/example"), request, CancellationToken.None);

        mcp!.ErrorCode.Should().Be(ErrorCodes.ReservedPathOperationForbidden);
        cli!.ErrorCode.Should().Be(mcp.ErrorCode);
    }

    [Fact]
    public async Task RealMcpAndCliCompactAdapters_RefuseReservedPaths()
    {
        using var scope = new TestHelpers.StoreScope();
        string path = "/agent/example";

        string mcp = await new ScriniaMcpTools().MemoryCompact(path);
        int cli = await new ScriniaCommands().Compact(
            path, workspaceRoot: TestHelpers.Store.WorkspaceRoot, json: true);

        mcp.Should().Contain(ErrorCodes.ReservedPathOperationForbidden);
        cli.Should().Be(1);
        TestHelpers.Store.ListScoped(["local"], null)
            .Should().NotContain(entry => entry.Entry.Name.Equals("example", StringComparison.OrdinalIgnoreCase));
    }
}
