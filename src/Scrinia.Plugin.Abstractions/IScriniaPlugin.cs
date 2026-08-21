namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// Identity for a Scrinia plugin. Implement this interface (or extend
/// <see cref="ScriniaPluginBase"/>) in your plugin assembly.
///
/// <para>This used to carry three more members — <c>ConfigureServices(IServiceCollection,
/// IConfiguration)</c>, <c>ConfigureMiddleware(IApplicationBuilder)</c> and
/// <c>MapEndpoints(IEndpointRouteBuilder)</c> — for the HTTP server that hosted plugins in
/// process. That server is gone, and with it the only caller any of the three ever had; they were
/// also the entire reason this assembly carried a <c>FrameworkReference</c> on
/// <c>Microsoft.AspNetCore.App</c>, which a program with no HTTP surface has no business
/// requiring of its plugin authors.</para>
///
/// <para><b>What a plugin actually reaches Scrinia through</b> is unchanged and lives elsewhere:
/// <c>ISearchScoreContributor</c>, <c>IMemoryEventSink</c> and <c>IEmbeddingsAdmin</c> in
/// <c>Scrinia.Core</c>, and <see cref="IMemoryOperationHook"/> here. A plugin implements those on
/// its own class; nothing needs a DI container to hand it over.</para>
/// </summary>
public interface IScriniaPlugin
{
    /// <summary>Display name of the plugin.</summary>
    string Name { get; }

    /// <summary>Semantic version string.</summary>
    string Version { get; }

    /// <summary>Load order — lower values run first. Default 0.</summary>
    int Order => 0;
}
