using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public class PluginProcessHostTests
{
    [Fact]
    public void PluginProcessHost_ImplementsISearchScoreContributor()
    {
        var host = new PluginProcessHost();
        host.Should().BeAssignableTo<ISearchScoreContributor>();
    }

    [Fact]
    public void PluginProcessHost_ImplementsIMemoryEventSink()
    {
        var host = new PluginProcessHost();
        host.Should().BeAssignableTo<IMemoryEventSink>();
    }

    [Fact]
    public void PluginProcessHost_ImplementsIAsyncDisposable()
    {
        var host = new PluginProcessHost();
        host.Should().BeAssignableTo<IAsyncDisposable>();
    }

    [Fact]
    public void HostRequest_SerializesCorrectly()
    {
        var request = new HostRequest
        {
            Method = "status"
        };

        var json = JsonSerializer.Serialize(request, HostJsonContext.Default.HostRequest);
        json.Should().Contain("\"method\":\"status\"");
    }

    [Fact]
    public void HostRequest_SearchMethod_SerializesWithScopes()
    {
        var request = new HostRequest
        {
            Method = "search",
            Query = "test query",
            Scopes = ["local", "api"],
        };

        var json = JsonSerializer.Serialize(request, HostJsonContext.Default.HostRequest);
        json.Should().Contain("\"method\":\"search\"");
        json.Should().Contain("\"query\":\"test query\"");
        json.Should().Contain("\"scopes\":");
    }

    [Fact]
    public void HostRequest_UpsertMethod_SerializesAllFields()
    {
        var request = new HostRequest
        {
            Method = "upsert",
            Scope = "local",
            Name = "test-mem",
            ChunkIndex = 2,
            Text = "some content",
        };

        var json = JsonSerializer.Serialize(request, HostJsonContext.Default.HostRequest);
        json.Should().Contain("\"method\":\"upsert\"");
        json.Should().Contain("\"scope\":\"local\"");
        json.Should().Contain("\"name\":\"test-mem\"");
        json.Should().Contain("\"chunkIndex\":2");
        json.Should().Contain("\"text\":\"some content\"");
    }

    [Fact]
    public void HostRequest_NullFields_AreOmitted()
    {
        var request = new HostRequest
        {
            Method = "status"
        };

        var json = JsonSerializer.Serialize(request, HostJsonContext.Default.HostRequest);
        json.Should().NotContain("\"text\"");
        json.Should().NotContain("\"scopes\"");
        json.Should().NotContain("\"scope\"");
    }

    [Fact]
    public void HostResponse_DeserializesOkResponse()
    {
        var json = """{"ok":true}""";
        var response = JsonSerializer.Deserialize(json, HostJsonContext.Default.HostResponse);

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        response.Error.Should().BeNull();
    }

    [Fact]
    public void HostResponse_DeserializesErrorResponse()
    {
        var json = """{"ok":false,"error":"Something went wrong"}""";
        var response = JsonSerializer.Deserialize(json, HostJsonContext.Default.HostResponse);

        response.Should().NotBeNull();
        response!.Ok.Should().BeFalse();
        response.Error.Should().Be("Something went wrong");
    }

    [Fact]
    public void HostResponse_DeserializesScores()
    {
        var json = """{"ok":true,"scores":{"local|mem1":0.95,"local|mem2":0.7}}""";
        var response = JsonSerializer.Deserialize(json, HostJsonContext.Default.HostResponse);

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        response.Scores.Should().NotBeNull();
        response.Scores.Should().HaveCount(2);
        response.Scores!["local|mem1"].Should().BeApproximately(0.95, 0.001);
        response.Scores["local|mem2"].Should().BeApproximately(0.7, 0.001);
    }

    [Fact]
    public void HostResponse_DeserializesStatus()
    {
        var json = """{"ok":true,"status":{"provider":"OnnxEmbeddingProvider","hardware":"auto","available":true,"dimensions":384,"vectorCount":42}}""";
        var response = JsonSerializer.Deserialize(json, HostJsonContext.Default.HostResponse);

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        response.Status.Should().NotBeNull();
        response.Status!.Provider.Should().Be("OnnxEmbeddingProvider");
        response.Status.Available.Should().BeTrue();
        response.Status.Dimensions.Should().Be(384);
        response.Status.VectorCount.Should().Be(42);
    }

    [Fact]
    public async Task StartAsync_NonExistentExe_ThrowsAndDoesNotCrash()
    {
        var host = new PluginProcessHost();

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
        var host = new PluginProcessHost();

        var result = await host.ComputeScoresAsync(
            "test query",
            [],
            null!, // store not needed when candidates empty
            CancellationToken.None);

        result.Should().BeNull();
        await host.DisposeAsync();
    }
}
