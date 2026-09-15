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

    [Fact]
    public void AtomicWriteAllBytes_WithLongDestination_UsesShortSameDirectoryTemporarySibling()
    {
        using var scope = new TestHelpers.StoreScope();
        string destination = Path.Combine(scope.TempDir, new string('x', 225) + ".meta.json");
        string? temporaryPath = null;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase != DurableWritePhase.BeforeMove)
                return null;

            temporaryPath = Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
                .Single(candidate => candidate.EndsWith(".tmp", StringComparison.Ordinal));
            return new UnauthorizedAccessException("injected before move");
        };

        try
        {
            Action write = () => DurableFile.AtomicWriteAllBytes(destination, [1, 2, 3]);

            write.Should().Throw<DurableWriteException>();
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        temporaryPath.Should().NotBeNull();
        Path.GetDirectoryName(temporaryPath!).Should().Be(Path.GetDirectoryName(destination));
        Path.GetFileName(temporaryPath!).Length.Should().BeLessThanOrEqualTo(255);
        TemporarySiblings(destination).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AtomicWriteAllBytes_WhenTransientWriteOrFlushFails_CleansEveryAttemptTemporaryFile(
        int failurePhaseValue)
    {
        using var scope = new TestHelpers.StoreScope();
        string destination = Path.Combine(scope.TempDir, "retry.meta.json");
        DurableWritePhase failurePhase = (DurableWritePhase)failurePhaseValue;
        int writeFailures = 0;
        DurableFile.FailureInjector = (phase, _) =>
        {
            if (phase == failurePhase && writeFailures++ == 0)
                return new IOException("injected transient write failure");

            return null;
        };

        try
        {
            DurableFile.AtomicWriteAllBytes(destination, [8, 9, 10]);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        File.ReadAllBytes(destination).Should().Equal([8, 9, 10]);
        TemporarySiblings(destination).Should().BeEmpty();
    }

    [Fact]
    public void AtomicWriteAllBytes_GeneratesUniqueTemporarySiblings()
    {
        using var scope = new TestHelpers.StoreScope();
        string destination = Path.Combine(scope.TempDir, "unique.meta.json");
        var temporaryPaths = new List<string>();
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeMove)
            {
                temporaryPaths.Add(Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
                    .Single(candidate => candidate.EndsWith(".tmp", StringComparison.Ordinal)));
            }

            return null;
        };

        try
        {
            DurableFile.AtomicWriteAllBytes(destination, [1]);
            DurableFile.AtomicWriteAllBytes(destination, [2]);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        temporaryPaths.Should().HaveCount(2);
        temporaryPaths.Should().OnlyHaveUniqueItems();
    }

    private static IEnumerable<string> TemporarySiblings(string destination)
    {
        string directory = Path.GetDirectoryName(destination)!;
        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
    }
}
