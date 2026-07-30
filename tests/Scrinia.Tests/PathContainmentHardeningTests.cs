using Scrinia.Core;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class PathContainmentHardeningTests
{
    [Fact]
    public void NestedPath_IsContained()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        PathContainment.IsWithin(baseDir, Path.Combine(baseDir, "sub", "file.txt")).Should().BeTrue();
    }

    [Fact]
    public void SiblingAndTraversal_AreRejected()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        PathContainment.IsWithin(baseDir, baseDir + "-sibling").Should().BeFalse();
        PathContainment.IsWithin(baseDir, Path.Combine(baseDir, "..", "escape")).Should().BeFalse();
    }

    [Fact]
    public void SymlinkedBase_LegitPath_StillContained()
    {
        string real = Path.Combine(Path.GetTempPath(), "pc-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        string link = Path.Combine(Path.GetTempPath(), "pc-link-" + Guid.NewGuid().ToString("N"));
        if (!TryCreateDirSymlink(link, real)) return; // environment can't create symlinks; skip
        try
        {
            // A file addressed through the symlinked base must still count as contained,
            // whether the base is given as the link or the real dir.
            string fileViaLink = Path.Combine(link, "note.txt");
            PathContainment.IsWithin(link, fileViaLink).Should().BeTrue();
            PathContainment.IsWithin(real, fileViaLink).Should().BeTrue();
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void SymlinkUnderBase_PointingOutward_IsRejected()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-base-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "pc-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(outside);
        string evil = Path.Combine(baseDir, "evil");
        if (!TryCreateDirSymlink(evil, outside)) return; // skip if unsupported
        try
        {
            // base/evil resolves outside the base → escape must be rejected.
            PathContainment.IsWithin(baseDir, Path.Combine(evil, "secret.txt")).Should().BeFalse();
        }
        finally { Directory.Delete(evil); }
    }

    [Fact]
    public void CaseDifferingPath_RejectedOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return; // case-sensitivity contract only holds on Linux
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-CASE-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        PathContainment.IsWithin(baseDir, baseDir.ToLowerInvariant() + "/x").Should().BeFalse();
    }

    [Fact]
    public void SymlinkLoop_FailsClosed_ReturnsFalse()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        string loopA = Path.Combine(baseDir, "a");
        string loopB = Path.Combine(baseDir, "b");
        try
        {
            // a -> b, b -> a : resolving either throws IOException (too many links / loop).
            Directory.CreateSymbolicLink(loopA, loopB);
            Directory.CreateSymbolicLink(loopB, loopA);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { return; } // environment can't create symlinks; skip
        // Must fail closed (return false), never throw.
        PathContainment.IsWithin(baseDir, Path.Combine(loopA, "x")).Should().BeFalse();
    }

    private static bool TryCreateDirSymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { return false; }
    }
}
