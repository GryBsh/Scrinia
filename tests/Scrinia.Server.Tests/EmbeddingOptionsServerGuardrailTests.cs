using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core.Embeddings;
using Scrinia.Server.Auth;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// Residual 2 of the 2026-08-16 embeddings-search-consolidation re-review. Program.cs used to bind
/// <c>EmbeddingOptions</c> straight from <c>Scrinia:Embeddings</c> config and hand it to
/// <see cref="BuiltInEmbeddingsService"/>'s constructor, which builds a <c>MemoryVectorIndexer</c>
/// whose own constructor throws <see cref="ArgumentOutOfRangeException"/> when
/// <c>MaxChunksPerMemory >= ChunkIndexStride</c> (1000). That constructor call happens lazily,
/// inside a DI singleton factory with no surrounding try/catch — so an out-of-range config value
/// would 500 whichever request first resolved the service, not fail loudly at boot where an
/// operator could see it.
///
/// <para>These tests drive the real Program.cs startup path (not a hand-built
/// <see cref="EmbeddingOptions"/>) with the two out-of-range <c>MaxChunksPerMemory</c> values the
/// re-review named, and prove <see cref="IEmbeddingsAdmin"/> resolves cleanly rather than
/// throwing. <c>EmbeddingOptionsGuardrailTests</c> in Scrinia.Tests pins the exact corrected
/// values <c>NormalizeGuardrails</c> produces; this class pins that the server actually calls it
/// before construction, the way <c>ServerSinkRegistrationTests</c> pins the embeddings-available
/// branch itself.</para>
/// </summary>
public sealed class EmbeddingOptionsServerGuardrailTests
{
    [Theory]
    [InlineData("1000")] // >= MemoryVectorIndexer.ChunkIndexStride: the constructor throw itself
    [InlineData("0")]    // non-positive: no throw, but IndexAsync would Take(0) and write nothing
    public void OutOfRangeMaxChunksPerMemory_ResolvesTheEmbeddingsServiceWithoutThrowing(string configuredValue)
    {
        using var factory = new ChunkCapServerFactory(configuredValue);

        // BuiltInEmbeddingsService is registered as a lazy singleton — resolving it is what
        // constructs MemoryVectorIndexer and would throw pre-fix for "1000". The "0" case would
        // not have thrown here even pre-fix (see EmbeddingOptionsGuardrailTests for why it is a
        // silent-data-loss bug rather than a throw), so this also proves the guardrail ran rather
        // than merely "nothing crashed by luck": both configured values must resolve.
        Action resolve = () => factory.Services.GetRequiredService<IEmbeddingsAdmin>();
        resolve.Should().NotThrow(
            because: $"MaxChunksPerMemory={configuredValue} must be normalized by " +
                "EmbeddingOptions.NormalizeGuardrails before it reaches MemoryVectorIndexer's constructor");

        factory.Services.GetRequiredService<IEmbeddingsAdmin>().Should().BeOfType<BuiltInEmbeddingsService>(
            because: "the ollama provider forces IsAvailable == true deterministically (see " +
                "EmbeddingsAvailableServerFactory), so a genuine BuiltInEmbeddingsService here proves " +
                "the whole embeddings-available branch — including the guardrail call — completed");
    }

    /// <summary>
    /// Forces the embeddings-available branch the same way <see cref="EmbeddingsAvailableServerFactory"/>
    /// does (Provider=ollama, whose IsAvailable is hard-coded true — see that class's doc comment),
    /// with a configurable, deliberately out-of-range <c>MaxChunksPerMemory</c>.
    /// </summary>
    private sealed class ChunkCapServerFactory(string maxChunksPerMemory) : WebApplicationFactory<Program>
    {
        private readonly string _tempDir =
            Path.Combine(Path.GetTempPath(), "scrinium-tests-" + Guid.NewGuid().ToString("N")[..8]);

        public string PrimaryStore { get; } = "test-store";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDir);
            builder.UseSetting("Scrinia:DataDir", _tempDir);
            builder.UseSetting($"Scrinia:Stores:{PrimaryStore}", Path.Combine(_tempDir, "stores", PrimaryStore));
            builder.UseSetting("Scrinia:Embeddings:Provider", "ollama");
            builder.UseSetting("Scrinia:Embeddings:MaxChunksPerMemory", maxChunksPerMemory);

            builder.ConfigureServices(services =>
            {
                var sp = services.BuildServiceProvider();
                var keyStore = sp.GetRequiredService<ApiKeyStore>();
                keyStore.CreateKey("chunk-cap-guardrail-test-user", [PrimaryStore], ["read", "store"], "test");
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
}
