using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public sealed class McpPluginHostProtocolTests
{
    [Fact]
    public async Task InvalidHandshake_DisablesExecutableForTheSession()
    {
        var caller = new RecordingCaller(_ => "{\"protocolVersion\":2,\"capabilities\":[\"status\",\"search\",\"index\",\"remove\"],\"providerAvailable\":true,\"dimensions\":384,\"vectorStoreSignature\":\"sig\"}");
        await using var host = new McpPluginHost(caller, ["status", "search", "index", "remove"]);

        await host.NegotiateStatusAsync();

        host.HasSearchCapability.Should().BeFalse();
        host.HasEventSinkCapability.Should().BeFalse();
        host.ProtocolFailureCode.Should().Be("EMBEDDINGS_PROTOCOL_UNSUPPORTED");
    }

    [Fact]
    public async Task Search_SendsOnlyCandidateNamesByScope()
    {
        var caller = new RecordingCaller(_ => "{\"scores\":{\"local|allowed\":2.0,\"local|other\":1.0}}");
        await using var host = new McpPluginHost(caller, ["search"]);
        var candidates = new[]
        {
            new ScopedArtifact("local", new ArtifactEntry(
                MemoryAddress.Parse("/allowed"), "artifact", 0, 0, DateTimeOffset.UtcNow)),
        };

        var scores = await host.ComputeScoresAsync("query", candidates, null!, default);

        caller.LastTool.Should().Be("search");
        caller.LastArguments.Should().ContainKey("candidatesByScope");
        scores.Should().ContainKey("local|allowed");
        scores.Should().NotContainKey("local|other");
    }

    private sealed class RecordingCaller(Func<string, string?> response)
        : McpPluginHost.IPluginToolCaller
    {
        public string? LastTool { get; private set; }
        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }

        public Task<string?> CallToolAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            LastTool = tool;
            LastArguments = arguments;
            return Task.FromResult(response(tool));
        }
    }
}
