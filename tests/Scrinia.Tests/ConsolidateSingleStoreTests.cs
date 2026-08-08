using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class ConsolidateSingleStoreTests
{
    [Fact]
    public async Task CompactEntryToSingleChunk_InvalidatesTheStoreIndexCache()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;
        const string scopeName = "local:sessions";
        const string subject = "day-one";

        string artifact = Nmp2ChunkedEncoder.EncodeChunks(["alpha chunk", "beta chunk"]);
        await store.WriteArtifactAsync(subject, scopeName, artifact);
        store.Upsert(
            new ArtifactEntry(
                Name: subject,
                Uri: "",
                OriginalBytes: 22,
                ChunkCount: 2,
                CreatedAt: DateTimeOffset.UtcNow.AddDays(-90),
                ContentPreview: "alpha"),
            scopeName);

        // Warm the in-memory index cache so a stale read is observable.
        store.LoadIndex(scopeName).Single().ChunkCount.Should().Be(2);

        var item = new ScopedArtifact(scopeName, store.LoadIndex(scopeName).Single());
        await ScriniaCommands.CompactEntryToSingleChunk(item, store, CancellationToken.None);

        // Must reflect the compaction without a process restart.
        store.LoadIndex(scopeName).Single().ChunkCount.Should().Be(1);
    }
}
