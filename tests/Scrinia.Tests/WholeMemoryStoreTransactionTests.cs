using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class WholeMemoryStoreTransactionTests
{
    [Fact]
    public async Task StoreAsync_LiveSidecarFailureRestoresBothMembersByteForByte()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));

        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<IOException>();

        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(oldBody);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task StoreAsync_PersistentWriteCommitsArtifactAndSidecarTogether()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;

        ArtifactEntry entry = await store.StoreAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        entry.Name.Should().Be("target");
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
        File.Exists(store.MetaPath("target")).Should().BeTrue();
    }
}
