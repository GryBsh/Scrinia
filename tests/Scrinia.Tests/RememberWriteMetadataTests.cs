using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// What the MCP write tools hand to the store, now that the store is the only thing that derives.
///
/// <para><b>These tests moved seam.</b> They used to record <c>WriteArtifactAsync</c>'s
/// <c>ArtifactWriteMetadata</c>, because the tools assembled the whole metadata half themselves and
/// that record was its only channel to a remote store — everything else went through an
/// <c>Upsert</c> that a remote store no-ops. The tools assemble nothing now: they hand over a
/// <see cref="MemoryWrite"/> of raw inputs and the store derives from it
/// (<c>MemoryDerivation</c>), so the write itself is the seam and what crosses it is the caller's
/// own data. The question each test asks is unchanged — does the agent's metadata reach the store,
/// or is it dropped on the way? — and one it could not ask before is now askable: does anything
/// DERIVED get handed back in as input (D-01/D-29), which is the mistake that silently reclassifies
/// every auto-extracted term as agent-supplied.</para>
/// </summary>
public class RememberWriteMetadataTests
{
    /// <summary>
    /// Records the <see cref="MemoryWrite"/>s handed to the store. Subclasses the existing
    /// <see cref="FakeMemoryStore"/> (tests/Scrinia.TestDoubles/FakeMemoryStore.cs) rather than
    /// writing a fresh 21-member delegator: it is public in the shared Scrinia.TestDoubles
    /// assembly and every member is virtual.
    /// </summary>
    private class RecordingStore : FakeMemoryStore
    {
        public MemoryWrite? LastStore { get; private set; }
        public MemoryWrite? LastAppend { get; private set; }

        public override Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default)
        {
            LastStore = write;
            return base.StoreAsync(write, ct);
        }

        public override Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default)
        {
            LastAppend = write;
            return base.AppendAsync(write, ct);
        }
    }

    [Fact]
    public async Task Remember_HandsTheAgentsOwnMetadataToTheStore()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new RecordingStore();
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().MemoryRemember(
            path: "/findings/note",
            content: ["a body with portcullis in it"],
            attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] },
            keywords: ["portcullis"]);

        recording.LastStore.Should().NotBeNull(because: "precondition: the write ran");

        // The agent's own map crosses, unmodified.
        recording.LastStore!.Attributes.Should().ContainKey("person");
        recording.LastStore.Attributes!["person"].Should().Contain("maria");

        // D-01, and the reason MemoryWrite has the shape it has: what crosses is the caller's RAW
        // keyword list, never one the tool merged with auto-extracted terms. "portcullis" appears
        // in the content, so a tool that merged first would hand back a list containing every
        // auto-extracted term as well — and the store would then classify each of them as
        // agent-supplied and seed it at +5 instead of +2.
        recording.LastStore.Keywords.Should().BeEquivalentTo(["portcullis"],
            because: "a derived list handed back in as input is the defect this write surface " +
                     "exists to make unexpressible");
    }

    /// <summary>
    /// The tool surface's four carrier parameters have no home of their own on
    /// <see cref="MemoryWrite"/> — they are ordinary keys in its one flat attribute map (D-03,
    /// D-07, D-08). This pins the mapping, and pins that the review value travels VERBATIM: the
    /// store canonicalizes a bare date into ISO-8601 round-trip form (D-06), and a tool that
    /// canonicalized it first would be a second implementation of the step D-06 exists to unify.
    /// </summary>
    [Fact]
    public async Task Remember_FoldsTagsAnchorsAndReviewIntoTheOneAttributeMap()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new RecordingStore();
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().Remember(
            ["a body"], "/findings/carrier",
            tags: ["alpha"],
            anchors: ["how do widgets work"],
            reviewAfter: "2026-08-01",
            reviewWhen: "when the auth system changes");

        recording.LastStore.Should().NotBeNull(because: "precondition: the write ran");
        var attributes = recording.LastStore!.Attributes;
        attributes.Should().NotBeNull();
        attributes!["tag"].Should().Contain("alpha");
        attributes["anchor"].Should().Contain("how do widgets work");
        // Verbatim and in order: the date first, then the condition. The date is the STORE's to
        // canonicalize (D-06) — a tool that emitted ISO-8601 here would be the second copy of it.
        attributes["review"].Should().Equal(["2026-08-01", "when the auth system changes"]);
    }

    /// <summary>
    /// append() had the same defect remember() did, one method over: it called
    /// <c>WriteArtifactAsync</c> with no <c>meta</c> and built the merged attribute map afterwards,
    /// for the no-op <c>Upsert</c>. Measured against a Kestrel host before that fix, an append to a
    /// memory carrying <c>tag/anchor/person</c> left only freshly extracted mentions behind.
    /// <para>The merge itself is no longer the tool's to do — the store carries forward the
    /// memory's curated keywords (D-12), its drift baseline (D-21), its earlier chunk entries and
    /// its preview (D-20), and unions <c>tag</c>/<c>anchor</c> (D-17), all pinned in
    /// <c>FileMemoryStoreWriteSurfaceTests</c> and <c>MemoryDerivationTests</c>. What is pinned
    /// here is the half that is still the tool's: the caller's additions cross, the APPENDED chunk
    /// crosses alone, and nothing that was read off the existing entry crosses back as input.</para>
    /// <para>End to end, that the memory's curated metadata survives an MCP append is
    /// <c>ScriniaMcpToolsTests.Append_PreservesCuratedAnchorsTagsAndDescription</c>.</para>
    /// </summary>
    [Fact]
    public async Task Append_HandsTheCallersAdditionsAndTheAppendedChunkAlone()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new SeededRecordingStore();
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().Append(
            "an appended chunk", "note",
            tags: ["delta"], keywords: ["harbour"],
            attributes: new Dictionary<string, string[]> { ["org"] = ["acme"] });

        recording.LastAppend.Should().NotBeNull(because: "precondition: the append ran");
        recording.LastAppend!.Content.Should().Equal(["an appended chunk"],
            because: "an append writes exactly one chunk; the full post-append text is the store's " +
                     "to reassemble, and a tool that reassembled it would be decoding the artifact " +
                     "to derive from it");
        recording.LastAppend.Attributes!["org"].Should().Contain("acme");
        recording.LastAppend.Attributes["tag"].Should().Contain("delta");

        recording.LastAppend.Keywords.Should().BeEquivalentTo(["harbour"],
            because: "the memory's existing keywords are a DERIVED list; carrying them forward is " +
                     "the store's job (D-12) and handing them back in as input would boost every " +
                     "auto-extracted term among them to agent weight (D-01)");
        recording.LastAppend.Attributes.Should().NotContainKey("anchor",
            because: "the entry's curated anchors are carried forward by the store, not re-sent " +
                     "by the tool");
    }

    /// <summary>A store that already holds one curated entry, and records the writes it receives.
    /// Its <c>ReadArtifactAsync</c> returns a real two-chunk NMP/2 artifact so <c>compact()</c> gets
    /// past its "already a single chunk" early return.</summary>
    private sealed class SeededRecordingStore : RecordingStore
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
    }

    /// <summary>
    /// compact() is the third write site. It rewrites content, never metadata — so the existing
    /// entry's attributes and curated description must travel INTO the write, because a re-store
    /// derives its whole metadata half from what it is handed and would otherwise reset them. It
    /// also has to READ that entry before the write, for the same reason it always did.
    /// <para>Its keywords deliberately do NOT travel (D-29): the entry's <c>Keywords</c> are a
    /// derived list with no record of which terms the agent supplied, so handing them back in would
    /// promote every auto-extracted term to agent weight. They are re-derived from the compacted
    /// text instead (D-27).</para>
    /// </summary>
    [Fact]
    public async Task Compact_HandsTheExistingEntrysMetadataToTheStore()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new SeededRecordingStore();
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().MemoryCompact(path: "note");

        recording.LastStore.Should().NotBeNull(because: "precondition: the compaction write ran");
        recording.LastStore!.Attributes.Should().ContainKey("tag");
        recording.LastStore.Attributes!["person"].Should().Contain("maria");
        recording.LastStore.Description.Should().Be("a curated description");
        recording.LastStore.Keywords.Should().BeNull(
            because: "a derived keyword list must never be handed back in as input (D-29)");
    }

    /// <summary>
    /// The same seam, reached under a casing the entry was not stored with. <c>compact()</c>'s
    /// existing-entry lookup compared <c>e.Name</c> against <c>ParseQualifiedName</c>'s subject with
    /// an ordinal <c>==</c>, so "Note" found nothing for a memory stored as "note" — and the write
    /// it guards went out with no metadata at all, which is exactly the reset the read exists to
    /// prevent. The compaction still landed on the same artifact file (NTFS is case-insensitive),
    /// so this is silent loss, not a failed call.
    /// </summary>
    [Fact]
    public async Task Compact_UnderADifferentCasing_StillHandsTheExistingEntrysMetadataToTheStore()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new SeededRecordingStore();
        MemoryStoreContext.Current = recording;

        // The seeded entry is named "note"; this asks for it as "Note".
        await new ScriniaMcpTools().MemoryCompact(path: "Note");

        recording.LastStore.Should().NotBeNull(because: "precondition: the compaction write ran");
        recording.LastStore!.Name.Should().Be("note",
            because: "the entry's own casing has to carry through to the write, or the compaction " +
                     "lands beside the memory on a case-sensitive filesystem");
        recording.LastStore.Attributes.Should().ContainKey("tag");
        recording.LastStore.Attributes!["person"].Should().Contain("maria");
        recording.LastStore.Description.Should().Be("a curated description");
    }

    // DELETED WITH THE SEAM IT WATCHED: Append_UnderADifferentCasing_ReadsTheArtifactUnderThe
    // EntrysOwnCasing. It asserted that MCP's Append resolved the entry's canonical casing from the
    // index BEFORE reading the artifact, because reading under the caller's casing missed on a
    // case-sensitive filesystem, fell through to the create path, and replaced the memory with the
    // appended chunk alone. Append no longer reads the artifact at all — IMemoryStore.AppendAsync
    // does, and the ordering ruling moved there with it. Two guards cover it now, and both are
    // stronger than an ordering assertion on a double:
    //   * FileMemoryStoreWriteSurfaceTests.AppendAsync_ResolvesAMismatchedCasingRatherThan
    //     ReplacingTheMemory — runs on every host, against the real store, and asserts the OUTCOME.
    //   * CaseSensitiveMcpAppendTests — drives MemoryAppend end to end against a genuinely
    //     case-sensitive directory and asserts the resulting content holds both bodies. It skips
    //     rather than silently passing where the filesystem cannot provide one.
}
