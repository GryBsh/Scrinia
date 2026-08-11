using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Resilience;
using Scrinia.Server.Auth;

namespace Scrinia.Server.Tests;

/// <summary>
/// Pins code-review Finding 2 on remote-write-correctness Task 7: a genuine unhandled server
/// exception under /api/v1/stores/... must still carry <see cref="RemoteApiVersion.HeaderName"/>.
///
/// <para>Reproduced before the fix, over a real Kestrel host: <c>app.UseExceptionHandler(...)</c>
/// is registered in <c>Program.cs</c> before the API-version-stamping middleware, so it wraps it.
/// <c>ExceptionHandlerMiddleware</c> calls <c>Response.Clear()</c> — which empties the header
/// collection — whenever an exception propagates up from downstream and the response hasn't
/// started, before running its own separate branch to write the 500 body. A middleware that set
/// the header directly (this one's first shape) had it wiped by that <c>Clear()</c>, so the
/// client's <c>RemoteApiVersionGuard</c> saw a headerless 500 and threw "wrong API version" —
/// exactly the misdirection this test guards against: a caller told "unrecognised server" when the
/// truth was "the server crashed". Confirmed empirically: before the <c>Response.OnStarting</c>
/// fix, the request below returned 500 with the header absent.</para>
///
/// <para><see cref="ThrowingBackend"/> makes <c>StoreManager.GetStore</c> throw from inside the
/// store-resolution middleware in <c>Program.cs</c> (unguarded there by any try/catch), which is
/// what reaches the outer <c>UseExceptionHandler</c> — a genuine unhandled exception on the real
/// request path, not a simulated response.</para>
/// </summary>
public sealed class ApiVersionHeaderOn500Tests : IAsyncLifetime
{
    private readonly ThrowingStoreServerFactory _factory = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateAuthenticatedClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GenuineUnhandledException_StillCarriesTheVersionHeader()
    {
        var resp = await _client.GetAsync($"/api/v1/stores/{_factory.ThrowingStore}/memories");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            because: "the configured backend throws unconditionally when the store is resolved, " +
                     "so this must reach the global exception handler and not any endpoint's own " +
                     "error handling");

        resp.Headers.TryGetValues(RemoteApiVersion.HeaderName, out var values).Should().BeTrue(
            because: "a genuine server crash must not present as a headerless response to the " +
                     "client's version guard — that would misdirect the caller toward \"talking " +
                     "to an unrecognised server\" instead of \"the server errored\"");
        values!.Should().ContainSingle().Which.Should().Be(RemoteApiVersion.Current);
    }
}

/// <summary>Provisions a store backed by <see cref="ThrowingBackend"/> so a request against it
/// forces a genuine unhandled exception from inside <c>Program.cs</c>'s store-resolution
/// middleware, over a real Kestrel host.</summary>
public sealed class ThrowingStoreServerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;
    public string ThrowingStore { get; } = "throwing-store";
    private string TestApiKey { get; set; } = "";

    public ThrowingStoreServerFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinium-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Scrinia:DataDir", _tempDir);
        builder.UseSetting($"Scrinia:Stores:{ThrowingStore}", Path.Combine(_tempDir, "stores", ThrowingStore));

        builder.ConfigureServices(services =>
        {
            // Registered after Program.cs's own AddSingleton<IStorageBackend, FilesystemBackend>(),
            // so this wins on resolution — same override mechanism as BundleEndpointTests'
            // NonLocalStoreServerFactory/FakeNonLocalBackend.
            services.AddSingleton<IStorageBackend>(new ThrowingBackend());

            var sp = services.BuildServiceProvider();
            var keyStore = sp.GetRequiredService<ApiKeyStore>();
            var (rawKey, _, _) = keyStore.CreateKey(
                "throwing-test-user", [ThrowingStore], ["read", "search", "store"], "throwing-test");
            TestApiKey = rawKey;
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
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

/// <summary>Throws unconditionally on store construction — stands in for a genuine, unrelated
/// server-side bug, so requests against a store backed by this hit the global exception handler
/// exactly like a real crash would.</summary>
internal sealed class ThrowingBackend : IStorageBackend
{
    public string BackendId => "throwing";
    public IMemoryStore CreateStore(string storeName, string storePath) =>
        throw new InvalidOperationException("Deliberate test failure — simulates a genuine server crash.");
}
