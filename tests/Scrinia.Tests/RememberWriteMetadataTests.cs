using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// remember() must hand the agent's attributes to the write seam, not assemble them afterwards.
/// Before the reorder they were built after WriteArtifactAsync and delivered via Upsert — which
/// works locally and silently discarded everything against a remote store.
/// </summary>
public class RememberWriteMetadataTests
{
    /// <summary>
    /// Records what WriteArtifactAsync was handed. Subclasses the existing FakeMemoryStore
    /// (tests/Scrinia.Tests/FakeMemoryStore.cs) rather than writing a fresh 21-member delegator:
    /// it is internal to this same assembly and every member is virtual.
    /// </summary>
    private sealed class RecordingStore : FakeMemoryStore
    {
        public ArtifactWriteMetadata? LastMeta { get; private set; }
        public bool WriteHappened { get; private set; }

        public override Task WriteArtifactAsync(string subject, string scope, string artifactText,
            ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
        {
            LastMeta = meta;
            WriteHappened = true;
            return base.WriteArtifactAsync(subject, scope, artifactText, meta, ct);
        }
    }

    [Fact]
    public async Task Remember_HandsTheAttributesMapToTheWriteSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new RecordingStore();
        MemoryStoreContext.Current = recording;

        // "3 tests" is deliberate: it matches CountPattern (MemoryTools.cs:13), which drives the
        // auto-derived reviewWhen. That is the assertion below that actually pins the ordering.
        await new ScriniaMcpTools().MemoryRemember(
            path: "/findings/note",
            content: ["a body with 3 tests in it"],
            attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] },
            keywords: ["portcullis"]);

        recording.WriteHappened.Should().BeTrue("precondition: the write ran");
        recording.LastMeta.Should().NotBeNull(
            because: "the metadata must cross the seam, not arrive later via Upsert");

        // The agent's own map crosses. True in either ordering — "person" is merged hundreds of
        // lines above the write — so this pins delivery, not sequence.
        recording.LastMeta!.Attributes.Should().ContainKey("person");
        recording.LastMeta.Attributes!["person"].Should().Contain("maria");

        // This one pins the sequence. "review" exists only because the carrier fold ran: content
        // matching CountPattern sets reviewWhen := "when counts in this memory change", which
        // BuildCarrierAttributes emits as the `review` key. Move the carrier block back below
        // WriteArtifactAsync — the bug this test guards — and meta.Attributes has no `review` key.
        recording.LastMeta.Attributes.Should().ContainKey("review",
            "the carrier fold must run before the write, not after it");
        recording.LastMeta.Attributes["review"].Should().Contain("when counts in this memory change");

        // The other two fields of the record cross too, not just Attributes.
        recording.LastMeta.Keywords.Should().Contain("portcullis",
            "the agent's keywords are the remote store's only copy of them");
        recording.LastMeta.Description.Should().Be("a body with 3 tests in it",
            because: "with no curated description the auto content preview is what crosses");
    }
}
