using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
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
        // Equal, not BeEquivalentTo: element order determines chunk index on the plugin side, so
        // a reordered array must fail this assertion, not pass it.
        ((string[])caller.Calls[0].Arguments["content"]!).Should().Equal("one", "two", "three");
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

        await host.OnAppendedAsync("note", "c", store, default);

        caller.Calls.Should().ContainSingle();
        caller.Calls[0].Tool.Should().Be("index");
        ((string[])caller.Calls[0].Arguments["content"]!).Should().Equal("a", "b", "c");
    }
}
