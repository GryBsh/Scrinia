using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class CompactNotificationTests
{
    [Fact]
    public async Task Compact_PublishesOneCompleteContentNotification()
    {
        using var scope = new TestHelpers.StoreScope();
        var sink = new RecordingSink();
        MemoryEventSinkContext.Current = sink;
        var tools = new ScriniaMcpTools();

        await tools.Remember(["first", "second"], "compact-notify");
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        sink.Stored.Clear();

        await tools.Compact("compact-notify");
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));

        sink.Stored.Should().ContainSingle();
        sink.Stored[0].Name.Should().Be("compact-notify");
        sink.Stored[0].Content.Should().Equal("firstsecond");
    }

    [Fact]
    public async Task Compact_EphemeralMemory_DoesNotClaimFileChanges()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();

        await tools.Remember(["first", "second"], "~compact-ephemeral");
        var response = await tools.Compact("~compact-ephemeral");

        response.Content.Should().NotContain("Files in .scrinia/ were updated");
    }

    [Fact]
    public async Task Compact_AlreadySingleChunk_DoesNotPublishAnEvent()
    {
        using var scope = new TestHelpers.StoreScope();
        var sink = new RecordingSink();
        MemoryEventSinkContext.Current = sink;
        var tools = new ScriniaMcpTools();

        await tools.Remember(["single"], "compact-noop");
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        sink.Stored.Clear();

        var response = await tools.Compact("compact-noop");
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));

        response.Content.Should().Contain("Already a single chunk");
        sink.Stored.Should().BeEmpty();
    }

    private sealed class RecordingSink : IMemoryEventSink
    {
        public List<(string Name, string[] Content)> Stored { get; } = [];

        public Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct)
        {
            Stored.Add((qualifiedName, content));
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(string qualifiedName, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
