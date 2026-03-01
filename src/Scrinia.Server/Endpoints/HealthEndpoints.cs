using Scrinia.Plugin.Abstractions;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new HealthResponse("ok")));

        app.MapGet("/health/ready", (ApiKeyStore keyStore, StoreManager storeManager, IReadOnlyList<IScriniaPlugin> plugins) =>
        {
            var checks = RunReadinessChecks(keyStore, storeManager, plugins);
            bool allOk = checks.All(c => c.Status == "ok");

            var response = new HealthResponse(allOk ? "ok" : "degraded", checks);
            return allOk ? Results.Ok(response) : Results.Json(response, statusCode: 503);
        });

        // Backward-compat alias
        app.MapGet("/health", (ApiKeyStore keyStore, StoreManager storeManager, IReadOnlyList<IScriniaPlugin> plugins) =>
        {
            var checks = RunReadinessChecks(keyStore, storeManager, plugins);
            bool allOk = checks.All(c => c.Status == "ok");

            var response = new HealthResponse(allOk ? "ok" : "degraded", checks);
            return allOk ? Results.Ok(response) : Results.Json(response, statusCode: 503);
        });
    }

    private static HealthCheck[] RunReadinessChecks(ApiKeyStore keyStore, StoreManager storeManager, IReadOnlyList<IScriniaPlugin> plugins)
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
            checks.Add(new HealthCheck("sqlite", "fail", ex.Message));
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
                checks.Add(new HealthCheck($"store:{name}", "fail", ex.Message));
            }
        }

        // Loaded plugins
        foreach (var plugin in plugins)
            checks.Add(new HealthCheck($"plugin:{plugin.Name}", "ok"));

        return checks.ToArray();
    }
}
