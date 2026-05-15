using FluentAssertions;
using Scrinia.Core.Resilience;

namespace Scrinia.Tests.Resilience;

/// <summary>
/// Tests for <see cref="FileRetry"/>. Covers the success-on-retry path, exhaustion behavior,
/// and the permanent-failure short-circuits (FileNotFound / DirectoryNotFound / PathTooLong)
/// that bypass the retry loop. The simulator here uses an in-memory counter rather than
/// touching real files — fast, deterministic, no AV race.
/// </summary>
public sealed class FileRetryTests
{
    [Fact]
    public void Run_Succeeds_OnFirstAttempt_NoRetry()
    {
        int calls = 0;
        FileRetry.Run(() => { calls++; });
        calls.Should().Be(1);
    }

    [Fact]
    public void Run_RetriesOnIOException_ThenSucceeds()
    {
        int calls = 0;
        FileRetry.Run(() =>
        {
            calls++;
            if (calls < 3) throw new IOException("file in use");
        });
        calls.Should().Be(3);
    }

    [Fact]
    public void Run_ExhaustsRetries_ThenRethrows()
    {
        int calls = 0;
        var act = () => FileRetry.Run(() =>
        {
            calls++;
            throw new IOException("perpetually in use");
        });
        act.Should().Throw<IOException>().WithMessage("perpetually in use");
        // 5 delays defined → 6 attempts total before final rethrow.
        calls.Should().Be(6);
    }

    [Fact]
    public void Run_FileNotFound_BypassesRetry()
    {
        int calls = 0;
        var act = () => FileRetry.Run(() =>
        {
            calls++;
            throw new FileNotFoundException("missing");
        });
        act.Should().Throw<FileNotFoundException>();
        calls.Should().Be(1);
    }

    [Fact]
    public void Run_DirectoryNotFound_BypassesRetry()
    {
        int calls = 0;
        var act = () => FileRetry.Run(() =>
        {
            calls++;
            throw new DirectoryNotFoundException("no such dir");
        });
        act.Should().Throw<DirectoryNotFoundException>();
        calls.Should().Be(1);
    }

    [Fact]
    public void Run_UnauthorizedAccess_BypassesRetry()
    {
        // Permission errors are surfaced immediately — retrying them obscures the real fix
        // (run as different user, change ACLs) and the retry won't help anyway.
        int calls = 0;
        var act = () => FileRetry.Run(() =>
        {
            calls++;
            throw new UnauthorizedAccessException("perm denied");
        });
        act.Should().Throw<UnauthorizedAccessException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_RetriesThenSucceeds()
    {
        int calls = 0;
        await FileRetry.RunAsync(() =>
        {
            calls++;
            if (calls < 2) throw new IOException("transient");
            return Task.CompletedTask;
        });
        calls.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var act = async () => await FileRetry.RunAsync(() =>
        {
            calls++;
            cts.Cancel();
            throw new IOException("force retry");
        }, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
