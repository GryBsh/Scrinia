using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class CompositeEventSinkTests
{
    [Fact]
    public async Task AllSinksReceiveEvents()
    {
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        var composite = new CompositeEventSink([sink1, sink2]);

        await composite.OnStoredAsync("test:topic", ["chunk1"], null!, CancellationToken.None);
        await composite.OnAppendedAsync("test:topic", "extra", null!, CancellationToken.None);
        await composite.OnForgottenAsync("test:topic", true, null!, CancellationToken.None);

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

        await composite.OnStoredAsync("test:topic", ["chunk1"], null!, CancellationToken.None);
        await composite.OnAppendedAsync("test:topic", "extra", null!, CancellationToken.None);
        await composite.OnForgottenAsync("test:topic", false, null!, CancellationToken.None);

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
            await composite.OnStoredAsync("test:topic", ["chunk1"], null!, CancellationToken.None);
            await composite.OnAppendedAsync("test:topic", "extra", null!, CancellationToken.None);
            await composite.OnForgottenAsync("test:topic", true, null!, CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }

    private sealed class RecordingSink : IMemoryEventSink
    {
        public int StoredCount, AppendedCount, ForgottenCount;

        public Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
        { StoredCount++; return Task.CompletedTask; }

        public Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
        { AppendedCount++; return Task.CompletedTask; }

        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
        { ForgottenCount++; return Task.CompletedTask; }
    }

    private sealed class ThrowingSink : IMemoryEventSink
    {
        public Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
            => throw new InvalidOperationException("boom");

        public Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
            => throw new InvalidOperationException("boom");

        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
