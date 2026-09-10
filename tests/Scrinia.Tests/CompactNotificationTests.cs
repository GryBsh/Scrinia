using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
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

        await tools.Remember(["first", "second"], MemoryAddress.Parse("/compact-notify"));
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        sink.Stored.Clear();

        await tools.Compact(MemoryAddress.Parse("/compact-notify"));
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));

        sink.Stored.Should().ContainSingle();
        sink.Stored[0].Name.Should().Be("/compact-notify");
        sink.Stored[0].Content.Should().Equal("firstsecond");
    }

    [Fact]
    public async Task Compact_EphemeralMemory_DoesNotClaimFileChanges()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();

        await tools.Remember(["first", "second"], MemoryAddress.Parse("~compact-ephemeral"));
        var response = await tools.Compact(MemoryAddress.Parse("~compact-ephemeral"));

        response.Content.Should().NotContain("Files in .scrinia/ were updated");
    }

    [Fact]
    public async Task Compact_AlreadySingleChunk_DoesNotPublishAnEvent()
    {
        using var scope = new TestHelpers.StoreScope();
        var sink = new RecordingSink();
        MemoryEventSinkContext.Current = sink;
        var tools = new ScriniaMcpTools();

        await tools.Remember(["single"], MemoryAddress.Parse("/compact-noop"));
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        sink.Stored.Clear();
        string artifactPath = TestHelpers.Store.FindArtifactPath("compact-noop", "local");
        byte[] before = await File.ReadAllBytesAsync(artifactPath);

        var response = await tools.Compact(MemoryAddress.Parse("/compact-noop"));
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        byte[] after = await File.ReadAllBytesAsync(artifactPath);

        response.Status.Should().Be("warning");
        response.Action.Should().Be("compacted");
        response.Content.Should().Be("Already a single chunk, nothing to compact.");
        response.Info.Should().Equal("mutation: none");
        response.ErrorCode.Should().BeNull();
        after.Should().Equal(before);
        sink.Stored.Should().BeEmpty();
    }

    private sealed class RecordingSink : IMemoryEventSink
    {
        public List<(string Name, string[] Content)> Stored { get; } = [];

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        {
            Stored.Add((address.ToString(), content));
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
