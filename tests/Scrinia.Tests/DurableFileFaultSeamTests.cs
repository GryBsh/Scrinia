using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class DurableFileFaultSeamTests
{
    [Fact]
    public void FailOnceAt_ConsumesOnlyTheMatchingFault()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);

        store.FailOnceAt(AppendFaultPoint.JournalWrite);

        var failures = new List<Exception?>
        {
            FileMemoryStore.InjectAppendFault(AppendFaultPoint.JournalWrite, "op-1", "journal-path"),
            FileMemoryStore.InjectAppendFault(AppendFaultPoint.JournalWrite, "op-1", "journal-path")
        };

        failures[0].Should().NotBeNull();
        failures[1].Should().BeNull();
    }

    [Fact]
    public void FaultInjector_PreservesOperationAndDestinationArguments()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var calls = new List<(string OperationId, string Path)>();

        FileMemoryStore.AppendFaultInjector = (point, operationId, path) =>
        {
            calls.Add((operationId, path));
            return point == AppendFaultPoint.LiveArtifactWrite
                ? new IOException("injected")
                : null;
        };

        try
        {
            FileMemoryStore.InjectAppendFault(AppendFaultPoint.LiveArtifactWrite, "op-2", "exact-path")
                .Should().BeOfType<IOException>();
            FileMemoryStore.InjectAppendFault(AppendFaultPoint.LiveArtifactWrite, "op-2", "exact-path")
                .Should().BeOfType<IOException>();

            calls.Should().Equal(
                ("op-2", "exact-path"),
                ("op-2", "exact-path"));
        }
        finally
        {
            store.ClearFaults();
        }
    }

    [Fact]
    public void FailPersistentlyAt_RemainsActiveUntilCleared()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);

        store.FailPersistentlyAt(AppendFaultPoint.LiveArtifactWrite);

        FileMemoryStore.InjectAppendFault(AppendFaultPoint.LiveArtifactWrite, "op-3", "path")
            .Should().BeOfType<IOException>();
        FileMemoryStore.InjectAppendFault(AppendFaultPoint.LiveArtifactWrite, "op-3", "path")
            .Should().BeOfType<IOException>();

        store.ClearFaults();
        FileMemoryStore.InjectAppendFault(AppendFaultPoint.LiveArtifactWrite, "op-3", "path")
            .Should().BeNull();
    }

    [Fact]
    public void AppendDurableWritePath_InvokesTheOperationScopedSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        string destination = Path.Combine(scope.TempDir, "append-artifact.nmp2");
        var calls = new List<(AppendFaultPoint Point, string OperationId, string Path)>();
        FileMemoryStore.AppendFaultInjector = (point, operationId, path) =>
        {
            calls.Add((point, operationId, path));
            return new IOException("injected append write failure");
        };

        try
        {
            Action write = () => DurableFile.AtomicWriteAllBytesForAppend(
                destination, [1, 2, 3], "op-4",
                AppendFaultPoint.LiveArtifactWrite, AppendFaultPoint.LiveArtifactFlush);

            write.Should().Throw<DurableWriteException>();
            calls.Should().NotBeEmpty();
            calls.Should().OnlyContain(call =>
                call.Point == AppendFaultPoint.LiveArtifactWrite
                && call.OperationId == "op-4"
                && call.Path == destination);
        }
        finally
        {
            FileMemoryStore.AppendFaultInjector = null;
        }
    }

    [Fact]
    public void PreCommitFaultPoints_EnumeratesEveryPreCommitPoint()
    {
        FileMemoryStore.PreCommitFaultPoints.Should().Contain(AppendFaultPoint.RawArtifactRead);
        FileMemoryStore.PreCommitFaultPoints.Should().Contain(AppendFaultPoint.LiveSidecarFlush);
        FileMemoryStore.PreCommitFaultPoints.Should().NotContain(AppendFaultPoint.BeforeEvent);
        FileMemoryStore.PreCommitFaultPoints.Should().NotContain(AppendFaultPoint.AfterEvent);
    }

    [Fact]
    public async Task AppendAsync_InjectsArtifactFaultThroughTheRealDurableWrite()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        var calls = new List<(string OperationId, string Path)>();
        FileMemoryStore.AppendFaultInjector = (point, operationId, path) =>
        {
            if (point != AppendFaultPoint.LiveArtifactWrite)
                return null;
            calls.Add((operationId, path));
            return new IOException("injected append artifact failure");
        };

        try
        {
            Func<Task> append = () => store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

            await append.Should().ThrowAsync<DurableWriteException>();
            calls.Should().NotBeEmpty();
            calls.Should().OnlyContain(call =>
                !string.IsNullOrWhiteSpace(call.OperationId)
                && call.Path == store.FindArtifactPath("target", "local"));
        }
        finally
        {
            store.ClearFaults();
        }
    }

    [Fact]
    public async Task AppendAsync_InjectsSidecarFaultThroughTheRealDurableWrite()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        var calls = new List<(string OperationId, string Path)>();
        FileMemoryStore.AppendFaultInjector = (point, operationId, path) =>
        {
            if (point != AppendFaultPoint.LiveSidecarWrite)
                return null;
            calls.Add((operationId, path));
            return new IOException("injected append sidecar failure");
        };

        try
        {
            Func<Task> append = () => store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

            await append.Should().ThrowAsync<DurableWriteException>();
            calls.Should().NotBeEmpty();
            calls.Should().OnlyContain(call =>
                !string.IsNullOrWhiteSpace(call.OperationId)
                && call.Path == store.MetaPath("target"));
        }
        finally
        {
            store.ClearFaults();
        }
    }
}
