using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class RecoveryHandleIdentityTests
{
    [Theory]
    [InlineData("evidence.json")]
    [InlineData("evidence-\u00e9-\u65e5.json")]
    public void OpenRegularFile_ReportsItsActualPath(string name)
    {
        using var scope = new TestHelpers.StoreScope();
        string root = PathContainment.ResolveCanonical(scope.WorkspaceDir);
        string path = Path.Combine(root, name);
        File.WriteAllText(path, "evidence");
        using var stream = Open(path);

        RecoveryHandleIdentityVerifier.TryCapture(stream, path, root, out var identity).Should().BeTrue();
        identity.OpenedPath.Should().Be(path);
        identity.RegularFile.Should().BeTrue();
    }

    [Fact]
    public void RenamedHandle_CannotAuthorizeAReplacementAtTheCheckedPath()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = PathContainment.ResolveCanonical(scope.WorkspaceDir);
        string path = Path.Combine(root, "evidence.json");
        string moved = Path.Combine(root, "moved.json");
        File.WriteAllText(path, "original evidence");
        using var stream = Open(path);
        File.Move(path, moved);
        File.WriteAllText(path, "replacement");

        RecoveryHandleIdentityVerifier.TryCapture(stream, path, root, out _).Should().BeFalse();
        RecoveryHandleIdentityVerifier.TryCapture(stream, moved, root, out var identity).Should().BeTrue();
        identity.OpenedPath.Should().Be(moved);
    }

    [Fact]
    public void OpenedFileOutsideTheBoundary_IsRejected()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = PathContainment.ResolveCanonical(scope.WorkspaceDir);
        string boundary = Path.Combine(root, "boundary");
        Directory.CreateDirectory(boundary);
        string path = Path.Combine(root, "outside.json");
        File.WriteAllText(path, "outside evidence");
        using var stream = Open(path);

        RecoveryHandleIdentityVerifier.TryCapture(stream, path, boundary, out _).Should().BeFalse();
        RecoveryHandleIdentityVerifier.TryCapture(stream, Path.Combine(boundary, "claimed.json"), boundary, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void DeletedOpenFile_IsRejected()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = PathContainment.ResolveCanonical(scope.WorkspaceDir);
        string path = Path.Combine(root, "evidence.json");
        File.WriteAllText(path, "evidence");
        using var stream = Open(path);
        File.Delete(path);

        RecoveryHandleIdentityVerifier.TryCapture(stream, path, root, out _).Should().BeFalse();
    }

    [Fact]
    public void ClosedHandle_IsRejected()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = PathContainment.ResolveCanonical(scope.WorkspaceDir);
        string path = Path.Combine(root, "evidence.json");
        File.WriteAllText(path, "evidence");
        using var stream = Open(path);
        stream.SafeFileHandle.Dispose();

        RecoveryHandleIdentityVerifier.TryCapture(stream, path, root, out _).Should().BeFalse();
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
