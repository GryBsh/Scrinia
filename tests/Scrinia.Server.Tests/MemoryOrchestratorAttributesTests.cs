using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// Verifies <see cref="MemoryOrchestrator.StoreAsync"/>/<see cref="MemoryOrchestrator.AppendAsync"/>
/// carry an attribute map with the same contract as the MCP Store/Append path (routed keyword
/// attributes, content-extracted attributes, and reference attributes, merged and seeded into term
/// frequencies).
/// </summary>
public sealed class MemoryOrchestratorAttributesTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public MemoryOrchestratorAttributesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_orch_fields_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia", "store", "local"));
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task StoreAsync_And_AppendAsync_CarryUnifiedAttributes()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["status: active\nBody."], "server-attributes", Keywords: ["person:Nick"]));

        var entry = _store.LoadIndex("local").Single(e => e.Name == "server-attributes");
        entry.Attributes!["status"].Should().BeEquivalentTo(["active"]);
        entry.Attributes["person"].Should().BeEquivalentTo(["Nick"]);
        entry.Keywords.Should().NotContain(k => k.StartsWith("person:"));

        await MemoryOrchestrator.AppendAsync(_store, "server-attributes", "status: done\nperson: Maria\nMore.");
        entry = _store.LoadIndex("local").Single(e => e.Name == "server-attributes");
        entry.Attributes!["status"].Should().BeEquivalentTo(["done"]);
        entry.Attributes["person"].Should().BeEquivalentTo(["Nick", "Maria"]);
    }

    /// <summary>
    /// P6.3 writer cutover: proves <see cref="Scrinia.Core.Migration.AttributeMigration.Forward"/> runs inside
    /// <see cref="MemoryOrchestrator.StoreAsync"/>'s persistent path AT WRITE TIME, not merely
    /// picked up later by <see cref="FileMemoryStore.LoadIndex"/>'s read-time forward-fill.
    ///
    /// Write/read-time isolation: <see cref="FileMemoryStore.LoadIndex"/> always forward-fills
    /// Attributes in-memory on every cold load (see its "Read-time forward-fill" comment) regardless
    /// of whether Attributes was already populated at write — so reading back through LoadIndex
    /// cannot distinguish "populated at write" from "upgraded on this read". This test instead
    /// reads the raw `*.meta.json` sidecar directly (via the `internal MetaPath` helper, which
    /// Scrinia.Core exposes to Scrinia.Server.Tests via InternalsVisibleTo) with a plain
    /// deserialize that never calls AttributeMigration.
    ///
    /// The request now supplies "tag"/"review" as attribute keys rather than through the removed
    /// Tags/ReviewAfter carrier fields, so what this pins is unchanged in substance: whatever the
    /// request said about tag and review is in the sidecar the write produced, not merely
    /// reconstructed by a later read.
    /// </summary>
    [Fact]
    public async Task StoreAsync_Persistent_FoldsToAttributesAtWriteTime()
    {
        string reviewDateToken = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture)
            .ToString("o", CultureInfo.InvariantCulture);

        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["Body about widgets."], "orch-writer-cutover-store",
                Attributes: new Dictionary<string, string[]>
                {
                    [KnownAttributes.TagKey] = ["widgets"],
                    [KnownAttributes.ReviewKey] = [reviewDateToken],
                }));

        string metaPath = _store.MetaPath("orch-writer-cutover-store", "local");
        string json = File.ReadAllText(metaPath);
        var persisted = JsonSerializer.Deserialize<ArtifactEntry>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        persisted.Should().NotBeNull();
        persisted!.Attributes.Should().NotBeNull();
        persisted.Attributes!["tag"].Should().Contain("widgets");
        persisted.Attributes!["review"].Should().NotBeEmpty();

        AttributeReaders.TryReadReview(persisted.Attributes, out var reviewDate, out _).Should().BeTrue();
        reviewDate.Should().NotBeNull();
    }

    /// <summary>
    /// Ephemeral counterpart: EphemeralEntry is in-memory-only and never forward-filled at read
    /// time, so asserting directly against the store (no sidecar/LoadIndex involved at all)
    /// proves the request's attribute map reaches <see cref="MemoryOrchestrator.StoreAsync"/>'s
    /// ephemeral branch at write, which has no read-time forward-fill to fall back on.
    /// </summary>
    [Fact]
    public async Task StoreAsync_Ephemeral_FoldsToAttributesAtWriteTime()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["Ephemeral scratch note about widgets."], "~orch-writer-cutover-eph",
                Attributes: new Dictionary<string, string[]> { [KnownAttributes.TagKey] = ["widgets"] }));

        var entry = _store.GetEphemeral("orch-writer-cutover-eph");
        entry.Should().NotBeNull();

        entry!.Attributes.Should().NotBeNull();
        entry.Attributes!["tag"].Should().Contain("widgets");
    }

    /// <summary>
    /// Merge 3 Task 2b: an explicit curated Description folds into ContentPreview at write
    /// time (option A) — it wins over the auto content snippet, on both the persistent and
    /// ephemeral <see cref="MemoryOrchestrator.StoreAsync"/> paths.
    /// </summary>
    [Fact]
    public async Task StoreAsync_ExplicitDescription_FoldsIntoContentPreview_PersistentAndEphemeral()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["Body about widgets."], "orch-preview-fold-persistent",
                Description: "curated widget notes"));

        var entry = _store.LoadIndex("local").Single(e => e.Name == "orch-preview-fold-persistent");
        entry.ContentPreview.Should().Be("curated widget notes",
            because: "an explicit non-blank description must fold into ContentPreview at write time");

        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["Ephemeral body about widgets."], "~orch-preview-fold-eph",
                Description: "curated eph notes"));

        var ephEntry = _store.GetEphemeral("orch-preview-fold-eph");
        ephEntry.Should().NotBeNull();
        ephEntry!.ContentPreview.Should().Be("curated eph notes",
            because: "an explicit non-blank description must fold into ContentPreview at write time (ephemeral path)");
    }

    /// <summary>
    /// Merge 3 Task 2b: <see cref="MemoryOrchestrator.AppendAsync"/> has no description param,
    /// so append must PRESERVE the existing entry's ContentPreview rather than regenerating it
    /// from content — regenerating would silently drop a curated description folded in by a
    /// prior Store call.
    /// </summary>
    [Fact]
    public async Task AppendAsync_PreservesExistingContentPreview_PersistentAndEphemeral()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["Body about widgets."], "orch-preview-preserve-persistent",
                Description: "curated widget notes"));
        await MemoryOrchestrator.AppendAsync(_store, "orch-preview-preserve-persistent", "More about widgets.");

        var entry = _store.LoadIndex("local").Single(e => e.Name == "orch-preview-preserve-persistent");
        entry.ContentPreview.Should().Be("curated widget notes",
            because: "append with no description param must preserve the curated ContentPreview, not regenerate from content");

        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["Ephemeral body about widgets."], "~orch-preview-preserve-eph",
                Description: "curated eph notes"));
        await MemoryOrchestrator.AppendAsync(_store, "~orch-preview-preserve-eph", "More ephemeral notes.");

        var ephEntry = _store.GetEphemeral("orch-preview-preserve-eph");
        ephEntry.Should().NotBeNull();
        ephEntry!.ContentPreview.Should().Be("curated eph notes",
            because: "append with no description param must preserve the curated ContentPreview (ephemeral path)");
    }

    /// <summary>
    /// Independently of the request's explicit attribute map, req.Keywords can route a
    /// "person:jolene"-shaped entry into the typed `person` attribute (via
    /// AttributeExtractor.RouteKeywords) — which collides with a `mention:jolene` span
    /// mechanically extracted from prose in the same content. Without the shadow-drop, both land
    /// in the persisted entry and double-count the entity in aggregation (entity graph,
    /// top-attributes facets). This is the server-path counterpart to the MCP path's
    /// agent-`attributes`-param collision, covered by MentionSpanWiringTests.
    /// </summary>
    [Fact]
    public async Task StoreAsync_KeywordRoutedTypedAttribute_ShadowsProseMention()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["We saw Jolene in France."], "server-mention-shadow",
                Keywords: ["person:jolene"]));

        var entry = _store.LoadIndex("local").Single(e => e.Name == "server-mention-shadow");
        entry.Attributes!["person"].Should().Contain("jolene");
        entry.Attributes.TryGetValue("mention", out var mentions);
        (mentions ?? []).Should().NotContain("jolene",
            because: "a keyword-routed typed attribute must not also be left as an untyped mention");
    }

    /// <summary>
    /// Records which artifact-read seam <see cref="MemoryOrchestrator.AppendAsync"/> reached for,
    /// and under which name. Holds one entry called "MixedCase" and returns a real two-chunk NMP/2
    /// artifact so the append proceeds past its create-new branch.
    /// </summary>
    private sealed class CasingRecordingStore : Scrinia.TestDoubles.FakeMemoryStore
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
    /// The ORDERING guard for <see cref="MemoryOrchestrator.AppendAsync"/>: the index lookup must
    /// run BEFORE the artifact read, and the read must be given the entry's own casing.
    ///
    /// <para>This asserts the order rather than the outcome, deliberately, and runs on every host —
    /// case-sensitive filesystem or not. The defect it guards — an append that resolves on the
    /// caller's casing, misses on a case-sensitive filesystem, falls through to
    /// <see cref="MemoryOrchestrator.StoreAsync"/>, and there lands on the existing memory (that
    /// lookup IS case-insensitive) and replaces its content with the appended chunk alone — is NOT
    /// reproducible through an outcome assertion on an ordinary NTFS volume, because
    /// <c>File.Exists</c> folds case there and the read succeeds either way. But NTFS does support
    /// per-directory case sensitivity, and <see cref="CaseSensitiveAppendTests"/>
    /// (tests/Scrinia.Server.Tests/CaseSensitiveAppendTests.cs) obtains one and asserts the outcome
    /// directly — the resulting content contains both the original and the appended text. The two
    /// guards are complementary, not redundant: this one proves the ORDER on every host; that one
    /// proves the OUTCOME, but only where a case-sensitive directory is available (it skips
    /// otherwise, never silently passing).</para>
    ///
    /// <para>Verified discriminating by running it: with the hoisted lookup moved back below the
    /// artifact read, this test fails on <c>ResolvedName</c> ("mixedcase" reached the
    /// caller's-casing seam). Note which assertion does the work — <c>ReadSubject</c> is NOT the
    /// discriminator here, because the rollback capture further down <c>AppendAsync</c> calls
    /// <c>ReadArtifactAsync(subject, scope)</c> with the canonical name as well, so it reads
    /// "MixedCase" with or without the hoist. The assertion that cannot be satisfied by accident is
    /// that <c>ResolveArtifactAsync</c> is never reached at all.</para>
    /// </summary>
    [Fact]
    public async Task AppendUnderADifferentCasing_ReadsTheArtifactUnderTheEntrysOwnCasing()
    {
        var recording = new CasingRecordingStore();

        var result = await MemoryOrchestrator.AppendAsync(recording, "mixedcase", "the appended body");

        recording.ReadSubject.Should().Be("MixedCase",
            because: "the index lookup must run first and hand its entry's own casing to the read — " +
                     "resolving on the caller's casing is what makes this append a replace on a " +
                     "case-sensitive filesystem");
        recording.ResolvedName.Should().BeNull(
            because: "the caller's-casing resolve is the seam that missed; nothing must reach it");
        recording.WrittenSubject.Should().Be("MixedCase",
            because: "the canonical casing has to carry through to the write as well as the read");
        result.ChunkCount.Should().Be(2,
            because: "the real append path ran — the create-new path would have written one chunk");
    }
}
