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

        // Both: compact reads the existing artifact through ReadArtifactAsync, and append does too
        // for a persistent name (it takes ResolveArtifactAsync only for an ephemeral one). Both are
        // overridden so this double keeps working whichever seam a caller reaches for.
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
    /// Records which artifact-read seam <c>append()</c> reached for, and under which name. Holds
    /// one entry called "MixedCase" and returns a real single-chunk NMP/2 artifact so the append
    /// proceeds past its create-new branch.
    /// </summary>
    private sealed class CasingRecordingStore : FakeMemoryStore
    {
        public static readonly ArtifactEntry Seeded = new(
            Name: "MixedCase",
            Uri: "",
            OriginalBytes: 24,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch);

        public string? ReadSubject { get; private set; }
        public string? ResolvedName { get; private set; }
        public string? WrittenSubject { get; private set; }

        public override IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => [Seeded];

        private static string OneChunk() =>
            Scrinia.Core.Encoding.Nmp2ChunkedEncoder.Encode("the first body");

        public override Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default)
        {
            ReadSubject = subject;
            return Task.FromResult(OneChunk());
        }

        public override Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default)
        {
            ResolvedName = nameOrArtifact;
            return Task.FromResult(OneChunk());
        }

        public override Task WriteArtifactAsync(string subject, string scope, string artifactText,
            ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
        {
            WrittenSubject = subject;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The ORDERING guard for the MCP <c>append()</c> persistent branch: the index lookup must run
    /// BEFORE the artifact read, and the read must be given the entry's own casing.
    ///
    /// <para>This asserts the order rather than the outcome, deliberately, and runs on every host —
    /// case-sensitive filesystem or not. The defect it guards — an append that resolves on the
    /// caller's casing, misses on a case-sensitive filesystem, falls through to the
    /// <c>Remember()</c> create path, and there lands on the existing memory (that lookup IS
    /// case-insensitive) and replaces its content with the appended chunk alone — is NOT
    /// reproducible through an outcome assertion on an ordinary NTFS volume, because
    /// <c>File.Exists</c> folds case there and the read succeeds either way. But NTFS does support
    /// per-directory case sensitivity, and <see cref="CaseSensitiveMcpAppendTests"/>
    /// (tests/Scrinia.Tests/CaseSensitiveMcpAppendTests.cs) obtains one and asserts the outcome
    /// directly — the resulting content contains both the original and the appended text. The two
    /// guards are complementary, not redundant: this one proves the ORDER on every host; that one
    /// proves the OUTCOME, but only where a case-sensitive directory is available (it skips
    /// otherwise, never silently passing).</para>
    ///
    /// <para>Verified discriminating by running it: with the hoisted lookup moved back below the
    /// artifact read — and the case-insensitive comparison left intact, so only the ORDER changes —
    /// <c>ReadSubject</c> IS set, to "mixedcase" (the caller's casing that reached the read), and
    /// this test fails on a value mismatch against the expected "MixedCase" — not on a null check.
    /// Unlike the server-side <c>AppendAsync</c>, this MCP <c>Append</c> persistent branch has no
    /// rollback-capture read further down to overwrite <c>ReadSubject</c> with the corrected
    /// casing, so nothing here masks the discriminator the way it does on the server side (see
    /// <see cref="Scrinia.Server.Tests.MemoryOrchestratorAttributesTests"/> for that contrast). That
    /// split is the whole point: on NTFS the outcome is unchanged and only the ordering is
    /// observable.</para>
    /// </summary>
    [Fact]
    public async Task Append_UnderADifferentCasing_ReadsTheArtifactUnderTheEntrysOwnCasing()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new CasingRecordingStore();
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().MemoryAppend(path: "mixedcase", content: "an appended chunk");

        recording.ReadSubject.Should().Be("MixedCase",
            because: "the index lookup must run first and hand its entry's own casing to the read — " +
                     "resolving on the caller's casing is what makes this append a replace on a " +
                     "case-sensitive filesystem");
        recording.ResolvedName.Should().BeNull(
            because: "the caller's-casing resolve is the seam that missed; nothing must reach it");
        recording.WrittenSubject.Should().Be("MixedCase",
            because: "the canonical casing has to carry through to the write as well as the read");
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

    /// <summary>
    /// The same seam, reached under a casing the entry was not stored with. <c>compact()</c>'s
    /// existing-entry lookup (<c>MemoryTools.Lifecycle.cs:583-584</c>) compared <c>e.Name</c> against
    /// <c>ParseQualifiedName</c>'s subject with an ordinal <c>==</c>, so "Note" found nothing for a
    /// memory stored as "note" — and the write it guards went out with <c>meta: null</c>, which is
    /// exactly the metadata reset the comment at <c>:571-577</c> says the read exists to prevent.
    /// The compaction still landed on the same artifact file (NTFS is case-insensitive), so this is
    /// silent loss, not a failed call.
    /// </summary>
    [Fact]
    public async Task Compact_UnderADifferentCasing_StillHandsTheExistingEntrysMetadataToTheWriteSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new SeededRecordingStore();
        MemoryStoreContext.Current = recording;

        // The seeded entry is named "note"; this asks for it as "Note".
        await new ScriniaMcpTools().MemoryCompact(path: "Note");

        recording.WriteHappened.Should().BeTrue("precondition: the compaction write ran");
        recording.LastMeta.Should().NotBeNull(
            because: "the entry exists under a different casing — failing to find it turns the " +
                     "compaction into a metadata reset on a store whose Upsert is a no-op");
        recording.LastMeta!.Attributes.Should().ContainKey("tag");
        recording.LastMeta.Attributes!["person"].Should().Contain("maria");
        recording.LastMeta.Keywords.Should().Contain("portcullis");
        recording.LastMeta.Description.Should().Be("a curated description");
    }
}
