using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Server.Auth;

namespace Scrinia.Server.Tests;

/// <summary>
/// MaintenanceEventSink has no dependency on embeddings — it is constructed with
/// `new MaintenanceEventSink()`. It was nevertheless registered inside the embeddings block, so
/// ref-linking and orphan marking disappeared from the server whenever embeddings did: model not
/// downloaded, initialisation threw, or a plugin supplied embeddings instead. The last is a
/// supported configuration, which made installing a plugin silently disable orphan marking.
/// </summary>
public sealed class ServerSinkRegistrationTests
{
    [Fact]
    public void MaintenanceSink_IsRegistered_WhenEmbeddingsAreAvailable()
    {
        using var factory = new ScriniaServerFactory();
        var sinks = factory.Services.GetServices<IMemoryEventSink>().ToList();

        sinks.Should().ContainSingle(s => s is MaintenanceEventSink,
            because: "ref-linking and orphan marking are the server's baseline maintenance");
    }

    [Fact]
    public void MaintenanceSink_IsStillRegistered_WhenTheEmbeddingModelIsUnavailable()
    {
        // NoEmbeddingsServerFactory forces EmbeddingProviderFactory.Create down its `default:`
        // branch (Scrinia:Embeddings:Provider = "none" matches no recognized case), which returns
        // NullEmbeddingProvider (IsAvailable => false) deterministically — independent of whether a
        // Model2Vec model happens to be present on disk. Program.cs then takes its `else` branch.
        using var factory = new NoEmbeddingsServerFactory();
        var sinks = factory.Services.GetServices<IMemoryEventSink>().ToList();

        sinks.Should().ContainSingle(s => s is MaintenanceEventSink,
            because: "maintenance takes no constructor arguments and has nothing to do with "
                   + "embeddings — an absent model must not disable orphan marking");
    }
}

/// <summary>
/// Forces the embeddings-unavailable path deterministically. <c>ScriniaServerFactory</c> is
/// sealed and exposes no per-instance settings hook (see PatchEndpointTests.ReadOnlyClaimsServerFactory
/// and RemoteLoadIndexTests.PagingRateLimitServerFactory for the established pattern of a small
/// sealed <see cref="WebApplicationFactory{TEntryPoint}"/> subclass instead of extending the shared one).
/// </summary>
public sealed class NoEmbeddingsServerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;

    public string PrimaryStore { get; } = "test-store";

    public NoEmbeddingsServerFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinium-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Scrinia:DataDir", _tempDir);
        builder.UseSetting($"Scrinia:Stores:{PrimaryStore}", Path.Combine(_tempDir, "stores", PrimaryStore));
        // "none" is a documented EmbeddingOptions.Provider value (see EmbeddingOptions.Provider's
        // XML doc) but matches no case in EmbeddingProviderFactory.Create's switch, so it falls to
        // `default:` and returns NullEmbeddingProvider — IsAvailable is hard-coded false, regardless
        // of whether a Model2Vec model is present on this machine.
        builder.UseSetting("Scrinia:Embeddings:Provider", "none");

        builder.ConfigureServices(services =>
        {
            var sp = services.BuildServiceProvider();
            var keyStore = sp.GetRequiredService<ApiKeyStore>();
            keyStore.CreateKey("no-embeddings-test-user", [PrimaryStore], ["read", "store"], "test");
        });
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
