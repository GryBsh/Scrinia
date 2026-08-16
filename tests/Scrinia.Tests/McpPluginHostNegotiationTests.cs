using FluentAssertions;
using Scrinia.Core;
using Scrinia.Services;

namespace Scrinia.Tests;

/// <summary>
/// Finding H3, seventh copy. The host used to re-express the chunking policy over the wire —
/// one upsert for the joined text plus one per content element, windowing nothing. It now
/// prefers a single `index` call so the shared indexer runs inside the plugin process, and
/// falls back to the old two-call shape only for plugins that do not offer `index`.
/// </summary>
public sealed class McpPluginHostNegotiationTests
{
    private sealed class RecordingCaller : McpPluginHost.IPluginToolCaller
    {
        public List<(string Tool, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = [];

        public Task<string?> CallToolAsync(
            string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            Calls.Add((tool, arguments));
            return Task.FromResult<string?>("{\"ok\":true}");
        }
    }

    private static FileMemoryStore NewStore()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia_pluginhost_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new FileMemoryStore(root);
    }

    [Fact]
    public async Task PluginOfferingIndex_ReceivesOneCallWithTheWholeContentArray()
    {
        var caller = new RecordingCaller();
        var host = new McpPluginHost(caller, ["index", "search", "remove"]);

        // "local:topic:subject" (v1 colon syntax) was removed by Task 13 — ParseQualifiedName now
        // rejects it. A bare name (no leading '/') still parses to ("local", name), so it exercises
        // the same ParseQualifiedName call the brief's literal "local:note" no longer can.
        await host.OnStoredAsync("note", ["one", "two", "three"], NewStore(), default);

        caller.Calls.Should().ContainSingle();
        caller.Calls[0].Tool.Should().Be("index");
        caller.Calls[0].Arguments["content"].Should().BeEquivalentTo(new[] { "one", "two", "three" });
    }

    [Fact]
    public async Task PluginOfferingOnlyUpsert_StillReceivesTheLegacySequence()
    {
        var caller = new RecordingCaller();
        var host = new McpPluginHost(caller, ["upsert", "search", "remove"]);

        await host.OnStoredAsync("note", ["one", "two"], NewStore(), default);

        caller.Calls.Should().OnlyContain(c => c.Tool == "upsert");
        caller.Calls.Should().HaveCount(3, because: "one joined document vector plus one per element");
    }
}
