using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Auth;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// Test factory that provisions temp data dir, two stores, and test API keys.
/// </summary>
public sealed class ScriniaServerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;

    public string TestApiKey { get; private set; } = "";
    public string TestKeyId { get; private set; } = "";
    public string TestUserId { get; } = "test-user";
    public string PrimaryStore { get; } = "test-store";
    public string SecondaryStore { get; } = "store-2";

    public ScriniaServerFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-server-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Scrinia:DataDir", _tempDir);
        builder.UseSetting("Scrinia:Stores:test-store", Path.Combine(_tempDir, "stores", "test-store"));
        builder.UseSetting("Scrinia:Stores:store-2", Path.Combine(_tempDir, "stores", "store-2"));

        builder.ConfigureServices(services =>
        {
            // After all services are registered, provision a test key
            var sp = services.BuildServiceProvider();
            var keyStore = sp.GetRequiredService<ApiKeyStore>();
            var (rawKey, keyId, _) = keyStore.CreateKey(
                TestUserId,
                [PrimaryStore, SecondaryStore],
                ["read", "search", "store", "append", "forget", "copy",
                 "export", "import", "manage_keys", "manage_roles"],
                "test");
            TestApiKey = rawKey;
            TestKeyId = keyId;
        });
    }

    /// <summary>
    /// Creates an HttpClient with the test API key in the Authorization header.
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    /// <summary>
    /// Creates an API key for a specific store (for isolation tests).
    /// </summary>
    public (string RawKey, string StoreName) CreateRestrictedStoreKey(string storeName, string? userId = null)
    {
        var keyStore = Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, _, _) = keyStore.CreateKey(userId ?? "restricted-user", [storeName], label: "restricted");
        return (rawKey, storeName);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
