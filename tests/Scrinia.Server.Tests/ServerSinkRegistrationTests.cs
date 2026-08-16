using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Server.Auth;
using Scrinia.Server.Services;

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
        // EmbeddingsAvailableServerFactory forces EmbeddingProviderFactory.Create down its
        // "ollama" case, whose provider inherits ResilientEmbeddingProvider.IsAvailable => true
        // unconditionally (no model file, no network call, no valid endpoint required — see that
        // class's doc comment). Program.cs then takes its `if (embeddingProvider.IsAvailable)`
        // branch, which is what this test needs to genuinely exercise: the default
        // ScriniaServerFactory does NOT do this, because this environment has no Model2Vec model
        // downloaded under the test binary's output dir, so it falls into the same `else` branch
        // as the unavailable case below.
        using var factory = new EmbeddingsAvailableServerFactory();
        var sinks = factory.Services.GetServices<IMemoryEventSink>().ToList();

        sinks.Should().ContainSingle(s => s is MaintenanceEventSink,
            because: "ref-linking and orphan marking are the server's baseline maintenance");
        sinks.Should().ContainSingle(s => s is BuiltInEmbeddingsService,
            because: "the embeddings-available branch must actually have been taken, not just "
                   + "leave maintenance behind — proves this test exercises IsAvailable == true "
                   + "and not the same else branch as the unavailable-model test");
        sinks.Should().ContainSingle(s => s is EntityTypingEventHandler,
            because: "spec success criterion #4 — EntityTypingEventHandler must be registered "
                   + "server-side when an embedding provider is available — had zero test "
                   + "coverage before this assertion");
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
/// Forces the embeddings-available path deterministically, without needing a downloaded
/// Model2Vec model or a running network endpoint. <c>ResilientEmbeddingProvider.IsAvailable</c>
/// (the base class for every HTTP-backed provider, including Ollama) is hard-coded <c>true</c> —
/// availability is a static property, not a live reachability check, so construction is inert:
/// no request is sent until <c>EmbedAsync</c> is actually called, which this test never does.
/// </summary>
public sealed class EmbeddingsAvailableServerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;

    public string PrimaryStore { get; } = "test-store";

    public EmbeddingsAvailableServerFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinium-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Scrinia:DataDir", _tempDir);
        builder.UseSetting($"Scrinia:Stores:{PrimaryStore}", Path.Combine(_tempDir, "stores", PrimaryStore));
        builder.UseSetting("Scrinia:Embeddings:Provider", "ollama");

        builder.ConfigureServices(services =>
        {
            var sp = services.BuildServiceProvider();
            var keyStore = sp.GetRequiredService<ApiKeyStore>();
            keyStore.CreateKey("embeddings-available-test-user", [PrimaryStore], ["read", "store"], "test");
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
