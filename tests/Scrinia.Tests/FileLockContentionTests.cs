using System.Diagnostics;
using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class FileLockContentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrinia-lock-contention-" + Guid.NewGuid().ToString("N"));

    public FileLockContentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(28)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(unchecked((int)0x8007000B))]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    [InlineData(unchecked((int)0x80070023))]
    [InlineData(unchecked((int)0x80131620))]
    public void OpenClassifierAcceptsOnlyPlatformContention(int error)
    {
        bool expected = OperatingSystem.IsWindows()
            ? (error & 0xFFFF) is 32 or 33
            : OperatingSystem.IsLinux() ? error == 11 : OperatingSystem.IsMacOS() && error == 35;
        FileLock.IsContentionOpenError(new IOException("injected", error)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(28)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(unchecked((int)0x8007000B))]
    [InlineData(unchecked((int)0x80070023))]
    public void ExplicitFlockClassifierUsesTheSameUnixErrno(int error)
    {
        bool expected = OperatingSystem.IsLinux() ? error == 11 : OperatingSystem.IsMacOS() && error == 35;
        FileLock.IsUnixContentionError(error).Should().Be(expected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RealContentionWaitsUntilTheRequestedTimeout(bool sharedHolder, bool sharedRequest)
    {
        string path = Path.Combine(_dir, "scope.lock");
        using var holder = sharedHolder ? FileLock.AcquireShared(path) : FileLock.AcquireExclusive(path);
        var elapsed = Stopwatch.StartNew();
        Action acquire = () =>
        {
            using var blocked = sharedRequest
                ? FileLock.AcquireShared(path, TimeSpan.FromMilliseconds(150))
                : FileLock.AcquireExclusive(path, TimeSpan.FromMilliseconds(150));
        };
        acquire.Should().Throw<FileLockTimeoutException>();
        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
    }
}
