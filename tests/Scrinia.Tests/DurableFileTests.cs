using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class DurableFileTests
{
    [Fact]
    public void AtomicWriteAllText_WhenContentIsNull_WrapsEncodingFailure()
    {
        using var scope = new TestHelpers.StoreScope();
        string destination = Path.Combine(scope.TempDir, "null-content.meta.json");

        Action write = () => DurableFile.AtomicWriteAllText(destination, null!);

        var failure = write.Should().Throw<DurableWriteException>().Which;

        failure.Operation.Should().Be("write text");
        failure.Destination.Should().Be(destination);
        failure.InnerException.Should().BeOfType<ArgumentNullException>();
    }

    [Fact]
    public void AtomicWriteAllText_WhenMoveFails_PreservesDestinationAndRemovesTemporaryFile()
    {
        using var scope = new TestHelpers.StoreScope();
        string destination = Path.Combine(scope.TempDir, "text.meta.json");
        File.WriteAllText(destination, "old-text");

        DurableFile.FailureInjector = (phase, _) => phase == DurableWritePhase.BeforeMove
            ? new UnauthorizedAccessException("injected before move")
            : null;

        try
        {
            Action write = () => DurableFile.AtomicWriteAllText(destination, "new-text");

            var failure = write.Should().Throw<DurableWriteException>().Which;

            failure.Operation.Should().Be("write text");
            failure.InnerException.Should().BeOfType<UnauthorizedAccessException>()
                .Which.Message.Should().Be("injected before move");
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        File.ReadAllBytes(destination).Should().Equal(TestHelpers.Utf8("old-text"));
        TemporarySiblings(destination).Should().BeEmpty();
    }

    [Fact]
    public void AtomicWriteAllBytes_WhenMoveFails_PreservesDestinationAndRemovesTemporaryFile()
    {
        using var scope = new TestHelpers.StoreScope();
        string destination = Path.Combine(scope.TempDir, "bytes.meta.json");
        byte[] original = [0, 255, 7];
        File.WriteAllBytes(destination, original);

        DurableFile.FailureInjector = (phase, _) => phase == DurableWritePhase.BeforeMove
            ? new UnauthorizedAccessException("injected before move")
            : null;

        try
        {
            Action write = () => DurableFile.AtomicWriteAllBytes(destination, [5, 4, 3]);

            var failure = write.Should().Throw<DurableWriteException>().Which;

            failure.Operation.Should().Be("write bytes");
            failure.InnerException.Should().BeOfType<UnauthorizedAccessException>()
                .Which.Message.Should().Be("injected before move");
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        File.ReadAllBytes(destination).Should().Equal(original);
        TemporarySiblings(destination).Should().BeEmpty();
    }

    [Fact]
    public void AtomicWriteAllBytes_PreservesArbitraryBytes()
    {
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(scope.TempDir, "bytes.meta.json");
        byte[] expected = [0, 255, 1, 0, 128];

        DurableFile.AtomicWriteAllBytes(path, expected);

        File.ReadAllBytes(path).Should().Equal(expected);
    }

    private static IEnumerable<string> TemporarySiblings(string destination)
    {
        string directory = Path.GetDirectoryName(destination)!;
        string prefix = Path.GetFileName(destination) + ".";

        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)
                && path.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
