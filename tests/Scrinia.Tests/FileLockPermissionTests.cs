using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class FileLockPermissionTests
{
    [Fact]
    public void ReadOnlyLockCannotAdmitAnExclusiveWriter()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(scope.TempDir, "review.lock");
        File.WriteAllText(path, "");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Action acquire = () =>
            {
                using var held = FileLock.AcquireExclusive(path, TimeSpan.FromMilliseconds(100));
            };
            acquire.Should().Throw<UnauthorizedAccessException>();
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
}
