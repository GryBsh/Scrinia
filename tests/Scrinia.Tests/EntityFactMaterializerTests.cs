using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

public sealed class EntityFactMaterializerTests
{
    [Fact]
    public void EntityFactMaterializer_CachesBuiltStoreUntilInvalidated()
    {
        int sourceCalls = 0;
        var materializer = CreateMaterializer(_ =>
        {
            sourceCalls++;
            return [new MemoryEntities("local:one", "one", DateTimeOffset.UnixEpoch,
                [EntityRef.Of("person", "maria")])];
        });
        var item = Item("local:one", "one");

        var first = materializer.Materialize([item], new Dictionary<string, PersistedFactResult>());
        var second = materializer.Materialize([item], new Dictionary<string, PersistedFactResult>());

        second.Should().BeSameAs(first);
        sourceCalls.Should().Be(1);

        materializer.Invalidate();
        materializer.Materialize([item], new Dictionary<string, PersistedFactResult>());
        sourceCalls.Should().Be(2);
    }

    [Fact]
    public async Task FlushAsync_RetainsFailedPayload_ThenRetriesLatestQueuedPayload()
    {
        var oldMemories = new[]
        {
            new MemoryEntities("local", "memo", DateTimeOffset.UnixEpoch,
                [EntityRef.Of("topic", "old")]),
        };
        var latestMemories = new[]
        {
            new MemoryEntities("local", "memo", DateTimeOffset.UnixEpoch,
                [EntityRef.Of("topic", "latest")]),
        };
        IReadOnlyList<MemoryEntities> sourceMemories = oldMemories;
        IBackgroundWorkParticipant? participant = null;
        var writes = new List<string>();
        bool failFirstWrite = true;
        var materializer = new EntityFactMaterializer(
            _ => sourceMemories,
            _ => "facts.json",
            (_, content) =>
            {
                if (failFirstWrite)
                {
                    failFirstWrite = false;
                    throw new IOException("deferred writer failure");
                }

                writes.Add(content);
                return (true, []);
            },
            registered =>
            {
                participant = registered;
                return new TestRegistration();
            });
        using (materializer)
        {
            var item = Item("local", "memo");
            materializer.Materialize([item], new Dictionary<string, PersistedFactResult>());

            await Assert.ThrowsAsync<IOException>(() => participant!.FlushAsync(CancellationToken.None));
            materializer.IsDirty.Should().BeTrue();
            materializer.DirtyQueue.Should().ContainSingle()
                .Which.Value.Content.Should().Be(EntityFactSidecar.Serialize(
                    EntityFactMaterializer.ComputeToken([item]), oldMemories));

            sourceMemories = latestMemories;
            materializer.Invalidate();
            materializer.Materialize([item], new Dictionary<string, PersistedFactResult>());
            materializer.IsDirty.Should().BeTrue();

            await participant.FlushAsync(CancellationToken.None);

            materializer.IsDirty.Should().BeFalse();
            materializer.DirtyQueue.Should().BeEmpty();
            writes.Should().ContainSingle().Which.Should().Be(EntityFactSidecar.Serialize(
                EntityFactMaterializer.ComputeToken([item]), latestMemories));
        }
    }

    private static EntityFactMaterializer CreateMaterializer(EntityFactMaterializerSource source) =>
        new(source, _ => null, (_, _) => (true, []), _ => new TestRegistration());

    private static ScopedArtifact Item(string scope, string name) =>
        new(scope, new ArtifactEntry(name, $"file://{name}", 1, 1, DateTimeOffset.UnixEpoch));

    private sealed class TestRegistration : IBackgroundWorkRegistration
    {
        public void Dispose() { }
    }
}
