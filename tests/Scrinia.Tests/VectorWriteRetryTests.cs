using System.Diagnostics;
using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests;

/// <summary>
/// Covers the retry used by detached event-sink indexing: a vector write declined because the
/// scope file was unreadable is attempted again rather than deferred to the next write.
/// </summary>
public class VectorWriteRetryTests
{
    [Fact]
    public async Task SucceedsFirstTime_DoesNotRetryOrDelay()
    {
        int calls = 0;
        var sw = Stopwatch.StartNew();

        bool ok = await VectorWriteRetry.RunAsync(
            () => { calls++; return Task.CompletedTask; }, null, "memo", CancellationToken.None);

        sw.Stop();
        ok.Should().BeTrue();
        calls.Should().Be(1);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "a successful write must not wait");
    }

    [Fact]
    public async Task RetriesUntilTheFileBecomesReadable()
    {
        int calls = 0;

        bool ok = await VectorWriteRetry.RunAsync(
            () =>
            {
                calls++;
                if (calls == 1)
                    throw new VectorStoreUnavailableException("local:api", "vectors.bin");
                return Task.CompletedTask;
            },
            null, "memo", CancellationToken.None);

        ok.Should().BeTrue();
        calls.Should().Be(2, "the second attempt succeeded");
    }

    [Fact]
    public async Task GivesUpAfterBoundedAttempts_WithoutThrowing()
    {
        int calls = 0;
        using var cts = new CancellationTokenSource();

        // Cancel during the first backoff: the retry must exit quietly rather than surface an
        // OperationCanceledException into the detached indexing task on shutdown.
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        bool ok = await VectorWriteRetry.RunAsync(
            () =>
            {
                calls++;
                throw new VectorStoreUnavailableException("local:api", "vectors.bin");
            },
            null, "memo", cts.Token);

        ok.Should().BeFalse();
        calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OtherExceptionsPropagate()
    {
        // Only the "file unreadable" case is retryable; a genuine bug must not be masked.
        var act = async () => await VectorWriteRetry.RunAsync(
            () => throw new InvalidOperationException("boom"), null, "memo", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
