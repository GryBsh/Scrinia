using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// Entry point for a Scrinia server plugin.
/// Implement this interface (or extend <see cref="ScriniaPluginBase"/>) in your plugin assembly.
/// </summary>
public interface IScriniaPlugin
{
    /// <summary>Display name of the plugin.</summary>
    string Name { get; }

    /// <summary>Semantic version string.</summary>
    string Version { get; }

    /// <summary>Load order — lower values run first. Default 0.</summary>
    int Order => 0;

    /// <summary>Register services into the DI container.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Add middleware (runs after auth, before endpoints).</summary>
    void ConfigureMiddleware(IApplicationBuilder app);

    /// <summary>Map additional HTTP endpoints.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
