using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// remember() must hand the agent's attributes to the write seam, not assemble them afterwards.
/// Before the reorder they were built after WriteArtifactAsync and delivered via Upsert — which
/// works locally and silently discarded everything against a remote store.
/// </summary>
public class RememberWriteMetadataTests
{
    /// <summary>
    /// Records what WriteArtifactAsync was handed. Subclasses the existing
    /// <see cref="FakeMemoryStore"/> (tests/Scrinia.TestDoubles/FakeMemoryStore.cs) rather than
    /// writing a fresh 21-member delegator: it is public in the shared Scrinia.TestDoubles
    /// assembly and every member is virtual.
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

    /// <summary>A store that already holds one curated entry, and records the write it receives.
    /// Its <c>ReadArtifactAsync</c> returns a real two-chunk NMP/2 artifact so <c>compact()</c> gets
    /// past its "already a single chunk" early return.</summary>
    private sealed class SeededRecordingStore : FakeMemoryStore
    {
        public static readonly ArtifactEntry Seeded = new(
            Name: "note",
            Uri: "",
            OriginalBytes: 24,
            ChunkCount: 2,
            CreatedAt: DateTimeOffset.UnixEpoch,
            ContentPreview: "a curated description",
            Keywords: ["portcullis"],
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["tag"] = ["alpha", "beta"],
                ["anchor"] = ["how do widgets work"],
                ["person"] = ["maria"],
            });

        public ArtifactWriteMetadata? LastMeta { get; private set; }
        public bool WriteHappened { get; private set; }

        public override IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => [Seeded];

        private static string TwoChunks() =>
            Scrinia.Core.Encoding.Nmp2ChunkedEncoder.EncodeChunks(["first chunk", "second chunk"]);

        // Both: append resolves the existing artifact through ResolveArtifactAsync, compact reads
        // it through ReadArtifactAsync.
        public override Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default) =>
            Task.FromResult(TwoChunks());

        public override Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default) =>
            Task.FromResult(TwoChunks());

        public override Task WriteArtifactAsync(string subject, string scope, string artifactText,
            ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
        {
            LastMeta = meta;
            WriteHappened = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// append() had the same defect remember() did, one method over, and it survived the fix:
    /// it called <c>WriteArtifactAsync</c> with no <c>meta</c> and built the merged attribute map
    /// afterwards, for the no-op <c>Upsert</c>. Measured against a Kestrel host before the fix, an
    /// append to a memory carrying <c>tag/anchor/person</c> left only freshly extracted mentions
    /// behind. This pins the delivery at the seam; the end-to-end proof is
    /// <c>Scrinia.Server.Tests.RemoteWritePathMetadataTests</c>.
    /// </summary>
    [Fact]
    public async Task Append_HandsTheCarriedForwardMetadataToTheWriteSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new SeededRecordingStore();
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().MemoryAppend(path: "note", content: "an appended chunk");

        recording.WriteHappened.Should().BeTrue("precondition: the write ran");
        recording.LastMeta.Should().NotBeNull(
            because: "remotely the write is the only channel this metadata has — Upsert is a no-op");
        recording.LastMeta!.Attributes.Should().ContainKey("tag");
        recording.LastMeta.Attributes!["tag"].Should().Contain(["alpha", "beta"]);
        recording.LastMeta.Attributes.Should().ContainKey("anchor");
        recording.LastMeta.Attributes.Should().ContainKey("person");
        recording.LastMeta.Keywords.Should().Contain("portcullis");
        recording.LastMeta.Description.Should().Be("a curated description",
            because: "an append with no new description preserves the curated one");
    }

    /// <summary>
    /// compact() is the third site. It rewrites content, never metadata — so the existing entry's
    /// attributes must travel with the write. It also has to READ that entry before the write:
    /// remotely the write replaces the entry, so a read afterwards samples the clobbered value.
    /// <para>Driven through a double rather than end-to-end because a remote <c>compact()</c> never
    /// reaches its write at all today — <c>HttpMemoryStore.ResolveArtifactAsync</c> re-encodes the
    /// server's content as a single chunk, so compact returns "already a single chunk" first. That
    /// flattening is a separate, recorded residual; this seam is where the metadata contract
    /// lives.</para>
    /// </summary>
    [Fact]
    public async Task Compact_HandsTheExistingEntrysMetadataToTheWriteSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new SeededRecordingStore();
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().MemoryCompact(path: "note");

        recording.WriteHappened.Should().BeTrue("precondition: the compaction write ran");
        recording.LastMeta.Should().NotBeNull(
            because: "compaction must not be a metadata reset on a store whose Upsert is a no-op");
        recording.LastMeta!.Attributes.Should().ContainKey("tag");
        recording.LastMeta.Attributes!["person"].Should().Contain("maria");
        recording.LastMeta.Keywords.Should().Contain("portcullis");
        recording.LastMeta.Description.Should().Be("a curated description");
    }
}
