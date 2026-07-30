using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Scrinia.Mcp;
using Scrinia.Server.Auth;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// Unit tests for the pure permission-mapping policy behind the MCP per-operation auth filter.
/// The map is the single source of truth for which MCP request needs which permission; these
/// assertions pin the policy independently of the JSON-RPC pipeline.
/// </summary>
public sealed class McpPermissionFilterTests
{
    [Theory]
    [InlineData("search", "search")]
    [InlineData("list", "search")]
    [InlineData("recall", "read")]
    [InlineData("remember", "store")]
    [InlineData("compact", "store")]
    [InlineData("link", "store")]
    [InlineData("reconcile", "store")]
    [InlineData("append", "append")]
    [InlineData("forget", "forget")]
    public void ToolsCall_MapsEachTool_ToItsPermission(string tool, string expected)
    {
        McpPermissionFilter.RequiredPermission("tools/call", tool).Should().Be(expected);
    }

    [Theory]
    [InlineData("guide")]
    [InlineData("restore")]
    [InlineData("some_unknown_tool")]
    [InlineData(null)]
    public void ToolsCall_BootstrapRecoveryOrUnknown_IsUngated(string? tool)
    {
        McpPermissionFilter.RequiredPermission("tools/call", tool).Should().BeNull();
    }

    [Fact]
    public void ResourcesRead_RequiresRead()
    {
        McpPermissionFilter.RequiredPermission("resources/read", null).Should().Be("read");
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("tools/list")]
    [InlineData("prompts/list")]
    [InlineData("prompts/get")]
    [InlineData("resources/list")]
    [InlineData("resources/templates/list")]
    [InlineData("notifications/initialized")]
    public void HandshakeAndDiscovery_AreUngated(string method)
    {
        McpPermissionFilter.RequiredPermission(method, null).Should().BeNull();
    }

    [Fact]
    public void Facts_RequiresSearchPermission()
    {
        McpPermissionFilter.RequiredPermission("tools/call", "facts").Should().Be("search");
    }

    [Theory]
    [InlineData("store", true)]
    [InlineData("append", true)]
    [InlineData("forget", true)]
    [InlineData("search", false)]
    [InlineData("read", false)]
    [InlineData(null, false)]
    public void IsWritePermission_ClassifiesMutatingPermissions(string? permission, bool expected)
    {
        McpPermissionFilter.IsWritePermission(permission).Should().Be(expected);
    }

    [Theory]
    [InlineData("remember", true)]
    [InlineData("append", true)]
    [InlineData("forget", true)]
    [InlineData("compact", true)]
    [InlineData("link", true)]
    [InlineData("reconcile", true)]
    [InlineData("search", false)]
    [InlineData("list", false)]
    [InlineData("facts", false)]
    [InlineData("recall", false)]
    public void IsWritePermission_ComposedWithRequiredPermission_MatchesToolMutationStatus(string tool, bool expected)
    {
        McpPermissionFilter.IsWritePermission(McpPermissionFilter.RequiredPermission("tools/call", tool))
            .Should().Be(expected);
    }

    /// <summary>
    /// Bare-bones <see cref="McpServer"/> stub so tests can construct a real <see cref="MessageContext"/>
    /// and drive <see cref="McpPermissionFilter.Filter"/> end to end, without a live MCP session, SSO
    /// claims, or HTTP round trip. None of these members are exercised by the filter itself — the
    /// filter only reads <c>ctx.JsonRpcMessage</c> — so every override is a throwaway default.
    /// </summary>
#pragma warning disable MCPEXP002 // McpServer's protected ctor is experimental; this is a test double, not a real server.
    private sealed class NoopMcpServer : McpServer
    {
        public override string? SessionId => null;
        public override string? NegotiatedProtocolVersion => null;
        public override ClientCapabilities? ClientCapabilities => null;
        public override Implementation? ClientInfo => null;
        public override McpServerOptions ServerOptions { get; } = new();
        public override IServiceProvider? Services => null;
        public override LoggingLevel? LoggingLevel => null;

        public override Task RunAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by the permission filter under test.");

        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public override IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler) =>
            throw new NotSupportedException("Not exercised by the permission filter under test.");

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore MCPEXP002

    /// <summary>Builds a <see cref="MessageContext"/> wrapping a <c>tools/call</c> request for <paramref name="toolName"/>.</summary>
    private static MessageContext CreateToolCallContext(string toolName)
    {
        var request = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = "tools/call",
            Params = new JsonObject { ["name"] = toolName },
        };
        return new MessageContext(new NoopMcpServer(), request);
    }

    /// <summary>
    /// Behavioral tests driving the actual <see cref="McpPermissionFilter.Filter"/> delegate — the glue
    /// the pure <see cref="McpPermissionFilter.IsWritePermission"/>/<see cref="McpPermissionFilter.RequiredPermission"/>
    /// unit tests above can't verify: that <see cref="McpPermissionFilter.CurrentStoreReadOnly"/> is read
    /// via the correct (non-inverted) AsyncLocal and actually short-circuits the pipeline.
    /// </summary>
    public sealed class FilterBehaviorTests : IDisposable
    {
        public void Dispose()
        {
            // AsyncLocals are static on McpPermissionFilter; reset after every test so state can't leak
            // into a sibling test.
            McpPermissionFilter.CurrentPermissions = null;
            McpPermissionFilter.CurrentStoreReadOnly = false;
        }

        [Fact]
        public async Task WriteTool_DeniedWhenStoreReadOnly()
        {
            McpPermissionFilter.CurrentPermissions = ["store", "append", "forget", "search", "read"];
            McpPermissionFilter.CurrentStoreReadOnly = true;
            bool reached = false;
            McpMessageHandler next = (_, _) => { reached = true; return Task.CompletedTask; };
            McpMessageHandler handler = McpPermissionFilter.Filter(next);
            MessageContext ctx = CreateToolCallContext("remember");

            Func<Task> act = () => handler(ctx, CancellationToken.None);

            await act.Should().ThrowAsync<McpException>();
            reached.Should().BeFalse("a read-only store must block the write tool before it reaches the next handler");
        }

        [Fact]
        public async Task WriteTool_AllowedWhenStoreWritable()
        {
            McpPermissionFilter.CurrentPermissions = ["store", "append", "forget", "search", "read"];
            McpPermissionFilter.CurrentStoreReadOnly = false;
            bool reached = false;
            McpMessageHandler next = (_, _) => { reached = true; return Task.CompletedTask; };
            McpMessageHandler handler = McpPermissionFilter.Filter(next);
            MessageContext ctx = CreateToolCallContext("remember");

            await handler(ctx, CancellationToken.None);

            reached.Should().BeTrue("a writable store must let a permitted write tool reach the next handler");
        }

        [Fact]
        public async Task ReadTool_AllowedEvenWhenStoreReadOnly()
        {
            McpPermissionFilter.CurrentPermissions = ["store", "append", "forget", "search", "read"];
            McpPermissionFilter.CurrentStoreReadOnly = true;
            bool reached = false;
            McpMessageHandler next = (_, _) => { reached = true; return Task.CompletedTask; };
            McpMessageHandler handler = McpPermissionFilter.Filter(next);
            MessageContext ctx = CreateToolCallContext("search");

            await handler(ctx, CancellationToken.None);

            reached.Should().BeTrue("read-only must gate writes only; reads must proceed regardless of the flag");
        }

        [Fact]
        public async Task WriteTool_MissingPermission_DeniedRegardlessOfReadOnlyFlag()
        {
            // Ordering check: a caller lacking the permission outright must see the permission error,
            // not the read-only error, even when the store also happens to be read-only.
            McpPermissionFilter.CurrentPermissions = ["search", "read"];
            McpPermissionFilter.CurrentStoreReadOnly = true;
            bool reached = false;
            McpMessageHandler next = (_, _) => { reached = true; return Task.CompletedTask; };
            McpMessageHandler handler = McpPermissionFilter.Filter(next);
            MessageContext ctx = CreateToolCallContext("remember");

            Func<Task> act = () => handler(ctx, CancellationToken.None);

            (await act.Should().ThrowAsync<McpException>()).WithMessage("*lacks the 'store' permission*");
            reached.Should().BeFalse();
        }
    }

    [Fact]
    public void EveryRegisteredTool_HasAPermissionMapping_OrIsIntentionallyUngated()
    {
        // Guards the hand-maintained parallel copy: a tool advertised to agents that the
        // permission map doesn't know about would silently fall through to the ungated
        // default. Bootstrap/recovery tools are ungated on purpose (parity with REST).
        var intentionallyUngated = new HashSet<string>(StringComparer.Ordinal) { "guide", "restore" };

        var toolNames = typeof(ScriniaMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        toolNames.Should().NotBeEmpty("the tool type must expose [McpServerTool] methods");

        using var scope = new AssertionScope();
        foreach (var name in toolNames)
        {
            if (intentionallyUngated.Contains(name)) continue;
            McpPermissionFilter.RequiredPermission("tools/call", name)
                .Should().NotBeNull($"tool '{name}' must have an explicit permission mapping");
        }
    }
}
