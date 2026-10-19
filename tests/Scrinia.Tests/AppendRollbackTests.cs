using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AppendRollbackTests
{
    [InlineData((int)AppendFaultPoint.RawArtifactRead)]
    [InlineData((int)AppendFaultPoint.RawSidecarRead)]
    [InlineData((int)AppendFaultPoint.JournalWrite)]
    [InlineData((int)AppendFaultPoint.JournalFlush)]
    [Theory]
    [InlineData((int)AppendFaultPoint.LiveArtifactWrite)]
    [InlineData((int)AppendFaultPoint.LiveArtifactFlush)]
    [InlineData((int)AppendFaultPoint.LiveSidecarWrite)]
    [InlineData((int)AppendFaultPoint.LiveSidecarFlush)]
    [InlineData((int)AppendFaultPoint.VerificationRead)]
public async Task AppendAsync_CreateFailureRemovesOnlyItsPartialMembers(int faultPointValue)
    {
        using var workspace = new TempWorkspace();
        string unrelatedPath = Path.Combine(workspace.Root, "unrelated.txt");
        await File.WriteAllTextAsync(unrelatedPath, "preserve me");
        workspace.Store.FailPersistentlyAt((AppendFaultPoint)faultPointValue);

        await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/new-target"), "local", ["created"])))
            .Should().ThrowAsync<Exception>();

        File.Exists(workspace.Store.ArtifactPath("new-target")).Should().BeFalse();
        File.Exists(workspace.Store.MetaPath("new-target")).Should().BeFalse();
        (await File.ReadAllTextAsync(unrelatedPath)).Should().Be("preserve me");
    }

    [Fact]
    public async Task AppendAsync_OrphanSidecarCreateFailurePreservesExactSidecarBytes()
    {
        using var workspace = new TempWorkspace();
        string sidecarPath = workspace.Store.MetaPath("orphan");
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/orphan"), "local", ["old"]));
        byte[] originalSidecar = await File.ReadAllBytesAsync(sidecarPath);
        File.Delete(workspace.Store.ArtifactPath("orphan"));
        workspace.Store.FailPersistentlyAt(AppendFaultPoint.LiveSidecarWrite);

        var exception = await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/orphan"), "local", ["created"])))
            .Should().ThrowAsync<DurableWriteException>();

        exception.Which.AppendOperationId.Should().NotBeNullOrWhiteSpace();
        exception.Which.Operation.Should().Be("write text");
        File.Exists(workspace.Store.ArtifactPath("orphan")).Should().BeFalse();
        (await File.ReadAllBytesAsync(sidecarPath)).Should().Equal(originalSidecar);
    }

    [Fact]
    public async Task Multi_chunk_append_failure_restores_the_complete_prior_memory()
    {
        using var workspace = new TempWorkspace();
        var address = MemoryAddress.Parse("/batch");
        await workspace.Store.StoreAsync(MemoryWrite.Create(address, "local", ["original"]));
        byte[] artifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("batch"));
        byte[] sidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("batch"));
        workspace.Store.FailPersistentlyAt(AppendFaultPoint.LiveSidecarWrite);

        await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(address, "local", ["first new chunk", "second new chunk"])))
            .Should().ThrowAsync<Exception>();

        (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("batch"))).Should().Equal(artifact);
        (await File.ReadAllBytesAsync(workspace.Store.MetaPath("batch"))).Should().Equal(sidecar);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "scrinia-append-create-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new FileMemoryStore(Root);
        }

        public string Root { get; }
        public FileMemoryStore Store { get; }

        public void Dispose()
        {
            Store.ClearFaults();
            Store.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
