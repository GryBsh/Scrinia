using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// Convenience base class for plugins. All methods are virtual with empty defaults.
/// </summary>
public abstract class ScriniaPluginBase : IScriniaPlugin
{
    public abstract string Name { get; }
    public abstract string Version { get; }
    public virtual int Order => 0;

    public virtual void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
    public virtual void ConfigureMiddleware(IApplicationBuilder app) { }
    public virtual void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
