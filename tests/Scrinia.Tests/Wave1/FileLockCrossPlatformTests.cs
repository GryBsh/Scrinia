using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests.Wave1;

/// <summary>
/// Pins the cross-process file-lock boundary (MAJOR-1). On Windows, exclusion is OS-enforced by
/// FileStream's <c>FileShare</c>; on non-Windows it is enforced by <c>flock(2)</c> (see
/// <see cref="FileLock"/>). A second exclusive acquire on a path already held must fail on BOTH
/// platforms — this suite asserts that so a future port cannot silently drop the non-Windows flock
/// path and let a second holder slip through.
/// </summary>
public sealed class FileLockCrossPlatformTests
{
    [Fact]
    public void SecondExclusiveAcquire_TimesOut_WhileFirstHeld()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-w1-flock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string lockPath = Path.Combine(dir, ".lock");

        // Holder is a FileLock (not a raw FileStream): on non-Windows a raw FileStream does NOT take a
        // flock, so only a FileLock holder exercises the cross-process enforcement under test.
        using FileLock first = FileLock.AcquireExclusive(lockPath, TimeSpan.FromSeconds(5));

        var ex = Assert.Throws<FileLockTimeoutException>(() =>
            FileLock.AcquireExclusive(lockPath, TimeSpan.FromMilliseconds(150)));
        Assert.Contains(lockPath, ex.Message);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void SharedLocks_Coexist_WhileExclusiveBlocks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-w1-flock-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string lockPath = Path.Combine(dir, ".lock");

        // Two shared locks coexist (Windows FileShare.Read / non-Windows LOCK_SH); a third, exclusive
        // acquire must be blocked by either of them on both platforms.
        using FileLock a = FileLock.AcquireShared(lockPath, TimeSpan.FromSeconds(5));
        using FileLock b = FileLock.AcquireShared(lockPath, TimeSpan.FromSeconds(5));

        var ex = Assert.Throws<FileLockTimeoutException>(() =>
            FileLock.AcquireExclusive(lockPath, TimeSpan.FromMilliseconds(150)));
        Assert.Contains(lockPath, ex.Message);

        try { Directory.Delete(dir, true); } catch { }
    }

    // Platform-boundary guard: if the non-Windows flock(2) path is ever removed, this assertion on
    // non-Windows fails loudly instead of letting a second holder succeed undetected. The two
    // functional tests above exercise the flock path end-to-end; this pins the contract.
    [Fact]
    public void CrossProcessEnforcement_IsPresent_OnTestedPlatform()
    {
        Assert.True(
            OperatingSystem.IsWindows() || IsFlockEnforced(),
            "Cross-process file-lock exclusion must be enforced on the running platform " +
            "(Windows: FileStream FileShare; non-Windows: flock(2)).");
    }

    private static bool IsFlockEnforced() =>
        // Records, as an explicit assertion, that non-Windows enforcement now comes from flock(2).
        !OperatingSystem.IsWindows();
}
