using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Services;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Finding H-4. The chunking settings are baked into the active embedding signature
/// (<see cref="ChunkedSignature.Compose"/>), so a host and an out-of-process plugin that
/// disagree about them disagree about which stored vectors are still valid. They were not on
/// <see cref="PluginConfigProtocol.Keys"/> and the plugin CLI did not parse them, so
/// <c>Scrinia:Embeddings:ChunkSize</c> was silently ignored end to end whenever the Vulkan
/// plugin owned embeddings — the same defect this branch fixed for the in-process path.
///
/// <para>These tests walk the real round trip: the host's argument build, the plugin's argument
/// parse, and the plugin's option build.</para>
/// </summary>
public sealed class PluginChunkConfigTransportTests
{
    private static readonly Dictionary<string, string> Configured = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Scrinia:Embeddings:ChunkSize"] = "600",
        ["Scrinia:Embeddings:ChunkOverlap"] = "60",
        ["Scrinia:Embeddings:MaxChunksPerMemory"] = "250",
        ["Scrinia:Embeddings:SemanticWeight"] = "42",
    };

    private static Func<string, string?> Config() => k => Configured.GetValueOrDefault(k);

    /// <summary>Mirrors the plugin CLI's `--config key=value` parse.</summary>
    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--config" || i + 1 >= args.Length) continue;
            string kv = args[++i];
            int eq = kv.IndexOf('=');
            if (eq > 0) values[kv[..eq]] = kv[(eq + 1)..];
        }
        return values;
    }

    [Fact]
    public void TheChunkingKeys_AreForwardedToThePlugin()
    {
        PluginConfigProtocol.Keys.Should().Contain(
            ["Scrinia:Embeddings:ChunkSize",
             "Scrinia:Embeddings:ChunkOverlap",
             "Scrinia:Embeddings:MaxChunksPerMemory"],
            because: "they are baked into the embedding signature, so a plugin that does not "
                   + "receive them writes vectors the host will consider stale");
    }

    [Fact]
    public void ConfiguredChunkSettings_SurviveTheHostToPluginRoundTrip()
    {
        var args = McpPluginHost.BuildArguments("/data", "/models", Config());
        var received = ParseArgs(args);

        var options = PluginConfigProtocol.BuildOptions(k => received.GetValueOrDefault(k));

        using var scope = new FluentAssertions.Execution.AssertionScope();
        options.ChunkSize.Should().Be(600);
        options.ChunkOverlap.Should().Be(60);
        options.MaxChunksPerMemory.Should().Be(250);
        options.SemanticWeight.Should().Be(42);
    }

    [Fact]
    public void TheSignatureThePluginComposes_MatchesTheHostsForTheSameConfig()
    {
        var args = McpPluginHost.BuildArguments("/data", "/models", Config());
        var pluginOptions = PluginConfigProtocol.BuildOptions(k => ParseArgs(args).GetValueOrDefault(k));

        // What the host bakes into the signature for the same configuration.
        string hostSignature = ChunkedSignature.Compose("vulkan:test", 600, 60);
        string pluginSignature = ChunkedSignature.Compose(
            "vulkan:test", pluginOptions.ChunkSize, pluginOptions.ChunkOverlap);

        pluginSignature.Should().Be(hostSignature,
            because: "a mismatch here quarantines every vector the plugin writes on the next load");
    }

    [Fact]
    public void AnOverlapAtOrAboveTheWindowSize_ClampsRatherThanCrashingThePlugin()
    {
        var warnings = new List<string>();
        var options = PluginConfigProtocol.BuildOptions(
            k => k switch
            {
                "Scrinia:Embeddings:ChunkSize" => "100",
                "Scrinia:Embeddings:ChunkOverlap" => "100",
                _ => null,
            },
            warnings.Add);

        options.ChunkOverlap.Should().BeLessThan(options.ChunkSize);
        warnings.Should().ContainSingle().Which.Should().Contain("ChunkOverlap");
    }

    [Fact]
    public void AChunkCapAboveTheStride_ClampsRatherThanThrowingFromTheIndexerCtor()
    {
        var warnings = new List<string>();
        var options = PluginConfigProtocol.BuildOptions(
            k => k == "Scrinia:Embeddings:MaxChunksPerMemory" ? "5000" : null,
            warnings.Add);

        options.MaxChunksPerMemory.Should().Be(MemoryVectorIndexer.ChunkIndexStride - 1);
        warnings.Should().ContainSingle().Which.Should().Contain("MaxChunksPerMemory");

        // The whole point of the clamp: this construction must not throw.
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Action act = () => _ = new MemoryVectorIndexer(
            new NullEmbeddingProvider(), new VectorStore(dir),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, options);

        act.Should().NotThrow();
    }
}
