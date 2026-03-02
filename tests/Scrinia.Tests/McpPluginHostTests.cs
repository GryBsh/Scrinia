using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public class McpPluginHostTests
{
    [Fact]
    public void McpPluginHost_ImplementsISearchScoreContributor()
    {
        var host = new McpPluginHost();
        host.Should().BeAssignableTo<ISearchScoreContributor>();
    }

    [Fact]
    public void McpPluginHost_ImplementsIMemoryEventSink()
    {
        var host = new McpPluginHost();
        host.Should().BeAssignableTo<IMemoryEventSink>();
    }

    [Fact]
    public void McpPluginHost_ImplementsIAsyncDisposable()
    {
        var host = new McpPluginHost();
        host.Should().BeAssignableTo<IAsyncDisposable>();
    }

    [Fact]
    public async Task StartAsync_NonExistentExe_ThrowsAndDoesNotCrash()
    {
        var host = new McpPluginHost();

        var act = () => host.StartAsync(
            "/nonexistent/scri-plugin-embeddings",
            Path.GetTempPath(),
            Path.GetTempPath(),
            _ => null,
            CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task ComputeScoresAsync_WhenNotStarted_ReturnsNull()
    {
        // A host that was never started (or is degraded) should return null
        // so the caller falls back to BM25-only
        var host = new McpPluginHost();

        var result = await host.ComputeScoresAsync(
            "test query",
            [],
            null!, // store not needed when candidates empty
            CancellationToken.None);

        result.Should().BeNull();
        await host.DisposeAsync();
    }

    [Fact]
    public void PluginClientJsonContext_DeserializesScores()
    {
        var json = """{"local|mem1":0.95,"local|mem2":0.7}""";
        var scores = JsonSerializer.Deserialize(json, PluginClientJsonContext.Default.DictionaryStringDouble);

        scores.Should().NotBeNull();
        scores.Should().HaveCount(2);
        scores!["local|mem1"].Should().BeApproximately(0.95, 0.001);
        scores["local|mem2"].Should().BeApproximately(0.7, 0.001);
    }
}
