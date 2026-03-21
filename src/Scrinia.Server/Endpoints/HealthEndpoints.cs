using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;
using Scrinia.Plugin.Abstractions;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Unauthenticated — status only (no store/plugin/backend names)
        app.MapGet("/health/live", () => Results.Ok(new HealthResponse("ok")));

        app.MapGet("/health/ready", StatusOnly);
        app.MapGet("/health", StatusOnly); // backward-compat alias

        // Authenticated — full detail with store names, plugin names, backend info
        app.MapGet("/health/details", (ApiKeyStore keyStore, StoreManager storeManager,
            IReadOnlyList<IScriniaPlugin> plugins, ILoggerFactory loggerFactory) =>
        {
            var checks = RunReadinessChecks(keyStore, storeManager, plugins, loggerFactory.CreateLogger("Health"));
            bool allOk = checks.Where(c => !c.Name.StartsWith("circuit-breaker:")).All(c => c.Status == "ok");

            var response = new HealthResponse(allOk ? "ok" : "degraded", checks);
            return allOk ? Results.Ok(response) : Results.Json(response, statusCode: 503);
        }).RequireAuthorization("Health");
    }

    private static IResult StatusOnly(ApiKeyStore keyStore, StoreManager storeManager,
        IReadOnlyList<IScriniaPlugin> plugins, ILoggerFactory loggerFactory)
    {
        var checks = RunReadinessChecks(keyStore, storeManager, plugins, loggerFactory.CreateLogger("Health"));
        bool allOk = checks.All(c => c.Status == "ok");
        string status = allOk ? "ok" : "degraded";
        return allOk ? Results.Ok(new HealthResponse(status)) : Results.Json(new HealthResponse(status), statusCode: 503);
    }

    private static HealthCheck[] RunReadinessChecks(
        ApiKeyStore keyStore, StoreManager storeManager, IReadOnlyList<IScriniaPlugin> plugins, ILogger logger)
    {
        var checks = new List<HealthCheck>();

        // SQLite connectivity
        try
        {
            keyStore.HasAnyKeys();
            checks.Add(new HealthCheck("sqlite", "ok"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed: SQLite connectivity");
            checks.Add(new HealthCheck("sqlite", "fail", "unavailable"));
        }

        // Storage backend
        checks.Add(new HealthCheck($"backend:{storeManager.Backend.BackendId}", "ok"));

        // Per-store availability
        foreach (var name in storeManager.StoreNames)
        {
            try
            {
                storeManager.GetStore(name);
                checks.Add(new HealthCheck($"store:{name}", "ok"));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health check failed: store {StoreName}", name);
                checks.Add(new HealthCheck($"store:{name}", "fail", "unavailable"));
            }
        }

        // Loaded plugins
        foreach (var plugin in plugins)
            checks.Add(new HealthCheck($"plugin:{plugin.Name}", "ok"));

        // Circuit breaker state
        foreach (var (name, cb) in CircuitBreakerRegistry.GetAll())
            checks.Add(new HealthCheck($"circuit-breaker:{name}", cb.State.ToString().ToLowerInvariant()));

        return checks.ToArray();
    }
}
