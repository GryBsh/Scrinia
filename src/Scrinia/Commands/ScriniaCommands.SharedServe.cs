using System.Reflection;
using Scrinia.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Mcp.SharedContracts;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Commands;

/// <summary>Shared-memory MCP commands.</summary>
public sealed class SharedServeCommands
{
    /// <summary>Serve enabled shared-memory MCP tools over stdio.</summary>
    /// <param name="workspaceRoot">Workspace root for the shared configuration. Defaults to the current directory.</param>
    /// <param name="cancellationToken">Cancellation for the host.</param>
    public async Task<int> Serve(
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        string root = WorkspaceRoot.Resolve(workspaceRoot, Directory.GetCurrentDirectory());
        string home = UserHome.Resolve();
        var workspaceConfig = CoreWorkspaceConfig.Load(root);
        var homeConfigStore = new HomeConfigStore();
        var registry = new SharedBaseRegistry(home, root, homeConfigStore, workspaceConfig);
        var runtimeFactory = new SharedBaseRuntimeFactory();
        var adapter = new SharedServeHostAdapter(homeConfigStore, workspaceConfig, registry, runtimeFactory);

        // This is deliberately before Host.CreateApplicationBuilder: an absent key is a valid
        // zero-base configuration, but it must not create a protocol transport that can never
        // serve a shared namespace.
        var server = new SharedServer(workspaceConfig, registry, runtimeFactory);
        adapter.ValidateStartup(server);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(server);
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new()
            {
                Name = "scri",
                Version = typeof(SharedServeCommands).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion.Split('+')[0] ?? "unknown",
            })
            .WithStdioServerTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next =>
                (request, ct) => SharedValidation.Call(request, next, ct)))
            .WithTools<SharedTools>(ScriniaMcpTools.CreateToolSerializerOptions());

        using IHost host = builder.Build();
        await host.RunAsync(cancellationToken);
        return 0;
    }
}
