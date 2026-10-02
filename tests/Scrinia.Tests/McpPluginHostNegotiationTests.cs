using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Services;

namespace Scrinia.Tests;

/// <summary>
/// Finding H3, seventh copy. The host used to re-express the chunking policy over the wire —
/// one upsert for the joined text plus one per content element, windowing nothing. It now
/// uses a single `index` call so the shared indexer runs inside the plugin process. Legacy
/// upsert is retained by the child as compatibility-only surface, but the host never selects it.
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

    private sealed class FailingCaller(string response) : McpPluginHost.IPluginToolCaller
    {
        public int CallCount { get; private set; }

        public Task<string?> CallToolAsync(
            string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<string?>(response);
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
        await host.OnStoredAsync(MemoryAddress.Parse("/note"), ["one", "two", "three"], NewStore(), default);

        caller.Calls.Should().ContainSingle();
        caller.Calls[0].Tool.Should().Be("index");
        // Equal, not BeEquivalentTo: element order determines chunk index on the plugin side, so
        // a reordered array must fail this assertion, not pass it.
        var content = (System.Text.Json.JsonElement)caller.Calls[0].Arguments["content"]!;
        content.EnumerateArray().Select(item => item.GetString()).Should().Equal("one", "two", "three");
    }

    [Fact]
    public async Task PluginOfferingOnlyUpsert_IsNeverSelectedByTheHost()
    {
        var caller = new RecordingCaller();
        var host = new McpPluginHost(caller, ["upsert", "search", "remove"]);

        await host.OnStoredAsync(MemoryAddress.Parse("/note"), ["one", "two"], NewStore(), default);

        caller.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Finding 3 (review pass). This exact bug — a replace-semantics indexer fed only the
    /// appended fragment instead of the memory's full post-append content — has already shipped
    /// and been fixed four separate times elsewhere on this branch. Guards the `index` branch of
    /// OnAppendedAsync specifically, which no prior test in this file touched.
    /// </summary>
    [Fact]
    public async Task PluginOfferingIndex_OnAppended_SendsFullPostAppendContentNotJustTheFragment()
    {
        var caller = new RecordingCaller();
        var host = new McpPluginHost(caller, ["index", "search", "remove"]);
        var store = NewStore();

        // Seed the artifact as it exists on disk AFTER the append already landed — OnAppendedAsync
        // re-reads current state rather than trusting the fragment it's handed, so what matters
        // here is what's on disk, not how it got there.
        var (scope, subject) = store.ParseQualifiedName("note");
        store.Upsert(new ArtifactEntry(subject, "", 0, 1, DateTimeOffset.UtcNow), scope);
        string path = store.ArtifactPath(subject, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.EncodeChunks(["a", "b", "c"]));

        await host.OnAppendedAsync(MemoryAddress.Parse("/note"), "c", store, default);

        caller.Calls.Should().ContainSingle();
        caller.Calls[0].Tool.Should().Be("index");
        var content = (System.Text.Json.JsonElement)caller.Calls[0].Arguments["content"]!;
        content.EnumerateArray().Select(item => item.GetString()).Should().Equal("a", "b", "c");
    }

    [Theory]
    [InlineData("{\"ok\":false,\"error\":\"write failed\"}")]
    [InlineData("{\"error\":\"write failed\"}")]
    public async Task PluginFailureEnvelope_IsRetriedInsteadOfAcknowledged(string response)
    {
        var caller = new FailingCaller(response);
        var host = new McpPluginHost(caller, ["index"]);

        for (int attempt = 0; attempt < 4; attempt++)
            await host.OnStoredAsync(MemoryAddress.Parse("/note"), ["content"], NewStore(), default);

        caller.CallCount.Should().Be(4,
            because: "a structured plugin failure must not reset the host failure counter");
        host.HasEventSinkCapability.Should().BeFalse(
            because: "repeated persistence failures must degrade rather than be acknowledged");
    }
}
