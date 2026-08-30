using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class CompositeEventSinkTests
{
    [Fact]
    public async Task AllSinksReceiveEvents()
    {
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        var composite = new CompositeEventSink([sink1, sink2]);

        var address = MemoryAddress.Parse("/test/topic");
        await composite.OnStoredAsync(address, ["chunk1"], null!, CancellationToken.None);
        await composite.OnAppendedAsync(address, "extra", null!, CancellationToken.None);
        await composite.OnForgottenAsync(address, true, null!, CancellationToken.None);

        sink1.StoredCount.Should().Be(1);
        sink1.AppendedCount.Should().Be(1);
        sink1.ForgottenCount.Should().Be(1);

        sink2.StoredCount.Should().Be(1);
        sink2.AppendedCount.Should().Be(1);
        sink2.ForgottenCount.Should().Be(1);
    }

    [Fact]
    public async Task FailingSinkDoesNotBlockOthers()
    {
        var thrower = new ThrowingSink();
        var recorder = new RecordingSink();
        var composite = new CompositeEventSink([thrower, recorder]);

        var address = MemoryAddress.Parse("/test/topic");
        await composite.OnStoredAsync(address, ["chunk1"], null!, CancellationToken.None);
        await composite.OnAppendedAsync(address, "extra", null!, CancellationToken.None);
        await composite.OnForgottenAsync(address, false, null!, CancellationToken.None);

        recorder.StoredCount.Should().Be(1);
        recorder.AppendedCount.Should().Be(1);
        recorder.ForgottenCount.Should().Be(1);
    }

    [Fact]
    public async Task EmptyArrayWorks()
    {
        var composite = new CompositeEventSink([]);

        var act = async () =>
        {
            var address = MemoryAddress.Parse("/test/topic");
            await composite.OnStoredAsync(address, ["chunk1"], null!, CancellationToken.None);
            await composite.OnAppendedAsync(address, "extra", null!, CancellationToken.None);
            await composite.OnForgottenAsync(address, true, null!, CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }

    private sealed class RecordingSink : IMemoryEventSink
    {
        public int StoredCount, AppendedCount, ForgottenCount;

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        { StoredCount++; return Task.CompletedTask; }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
        { AppendedCount++; return Task.CompletedTask; }

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
        { ForgottenCount++; return Task.CompletedTask; }
    }

    private sealed class ThrowingSink : IMemoryEventSink
    {
        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
            => throw new InvalidOperationException("boom");

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
            => throw new InvalidOperationException("boom");

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
