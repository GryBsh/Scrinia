using Scrinia.Core;

namespace Scrinia.Tests;

public class FileLockLazyTests : IDisposable
{
    private readonly string _tempDir;

    public FileLockLazyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_locklazy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AcquireShared_OnMissingDirectory_CreatesNothing()
    {
        string missingDir = Path.Combine(_tempDir, "absent");
        string lockPath = Path.Combine(missingDir, ".lock");

        using (var _ = FileLock.AcquireShared(lockPath)) { }

        Assert.False(Directory.Exists(missingDir));
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void AcquireShared_OnExistingDirectory_StillLocks()
    {
        string dir = Path.Combine(_tempDir, "present");
        Directory.CreateDirectory(dir);
        string lockPath = Path.Combine(dir, ".lock");

        using (var _ = FileLock.AcquireShared(lockPath))
        {
            Assert.True(File.Exists(lockPath));
        }
    }

    [Fact]
    public void AcquireExclusive_StillCreatesItsParentDirectory()
    {
        // The migration path relies on this; it must not regress.
        string missingDir = Path.Combine(_tempDir, "made-by-exclusive");
        string lockPath = Path.Combine(missingDir, ".lock");

        using (var _ = FileLock.AcquireExclusive(lockPath))
        {
            Assert.True(Directory.Exists(missingDir));
        }
    }
}
