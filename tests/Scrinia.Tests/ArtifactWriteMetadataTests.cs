using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Pins the one asymmetry in the write seam: the local store ignores ArtifactWriteMetadata
/// because Upsert is its real metadata channel, and the remote store cannot ignore it because
/// its Upsert is a no-op. A future reader deleting the "unused" parameter from FileMemoryStore
/// would break remote writes and nothing local, so the intent is pinned here.
/// </summary>
public class ArtifactWriteMetadataTests
{
    [Fact]
    public async Task LocalWriteArtifact_IgnoresMetadata_AndUpsertRemainsTheAuthority()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        string artifact = Nmp2ChunkedEncoder.Encode("body text");
        var meta = new ArtifactWriteMetadata(
            Attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] },
            Keywords: ["ignored"],
            Description: "ignored");

        await store.WriteArtifactAsync("subject", "local", artifact, meta);

        // The write alone creates no index entry — Upsert does that, which is exactly why the
        // local store can afford to ignore meta.
        store.LoadIndex("local").Should().BeEmpty(
            because: "WriteArtifactAsync persists content; Upsert persists the entry");
    }

    [Fact]
    public async Task LocalWriteArtifact_AcceptsNullMetadata()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        string artifact = Nmp2ChunkedEncoder.Encode("body text");
        var act = async () => await store.WriteArtifactAsync("subject", "local", artifact);

        await act.Should().NotThrowAsync();
    }
}
