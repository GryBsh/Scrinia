using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// <see cref="MemoryOrchestrator"/> as it is after the derivation collapse: it resolves the caller's
/// name, hands the RAW request to the store's write surface, and shapes the REST response from the
/// entry the store returns. Nothing here derives, archives, writes an artifact or upserts.
///
/// <para>The two recording tests pin the seam (what the orchestrator hands over, and that it reports
/// the name the STORE resolved rather than the one the caller asked for). The rollback test pins the
/// live defect this repoint fixes: the orchestrator used to delete the artifact unconditionally when
/// <c>Upsert</c> threw, which on a re-store destroyed the memory's only live copy.</para>
/// </summary>
public sealed class MemoryOrchestratorWriteSurfaceTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public MemoryOrchestratorWriteSurfaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_orch_writesurface_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia", "store", "local"));
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Records the <see cref="MemoryWrite"/> the orchestrator hands over, and answers with an entry
    /// whose <c>Name</c> deliberately differs in casing from the one the caller asked for — the way a
    /// real store answers when it resolves an existing memory. That is what makes the response
    /// assertions discriminate: building the qualified name from the requested subject instead of the
    /// returned entry yields "local:mixedcase".
    /// </summary>
    private sealed class RecordingWriteStore : Scrinia.TestDoubles.FakeMemoryStore
    {
        public MemoryWrite? StoreWrite { get; private set; }
        public MemoryWrite? AppendWrite { get; private set; }

        public override Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default)
        {
            StoreWrite = write;
            return Task.FromResult(new ArtifactEntry(
                Name: "MixedCase", Uri: "", OriginalBytes: 24, ChunkCount: write.Content.Length,
                CreatedAt: DateTimeOffset.UnixEpoch));
        }

        public override Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default)
        {
            AppendWrite = write;
            return Task.FromResult(new ArtifactEntry(
                Name: "MixedCase", Uri: "", OriginalBytes: 41, ChunkCount: 2,
                CreatedAt: DateTimeOffset.UnixEpoch, UpdatedAt: DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task StoreAsync_HandsTheRawRequestToTheStoreAndReportsTheNameTheStoreResolved()
    {
        var recording = new RecordingWriteStore();

        var result = await MemoryOrchestrator.StoreAsync(recording,
            new StoreRequest(["the body"], "mixedcase", Description: "curated", Keywords: ["vulpine"]));

        recording.StoreWrite.Should().NotBeNull(
            because: "the store owns the write — the orchestrator must not persist anything itself");
        recording.StoreWrite!.Name.Should().Be("mixedcase");
        recording.StoreWrite.Scope.Should().Be("local",
            because: "the store's members take the resolved (scope, subject) pair, not a qualified path");
        recording.StoreWrite.Content.Should().Equal(["the body"]);
        recording.StoreWrite.Description.Should().Be("curated");
        recording.StoreWrite.Keywords.Should().Equal(["vulpine"],
            because: "D-01: the write carries the CALLER's raw keyword list, never a derived one — " +
                     "the orchestrator derives nothing to merge into it");

        result.Name.Should().Be("MixedCase",
            because: "the response reports the name the store resolved, not the one the caller sent");
        result.QualifiedName.Should().Be("local:MixedCase");
        result.ChunkCount.Should().Be(1);
        result.OriginalBytes.Should().Be(24);
    }

    [Fact]
    public async Task AppendAsync_HandsExactlyOneChunkToTheStoreAndReportsTheNameTheStoreResolved()
    {
        var recording = new RecordingWriteStore();

        var result = await MemoryOrchestrator.AppendAsync(recording, "mixedcase", "the appended body");

        recording.AppendWrite.Should().NotBeNull();
        recording.AppendWrite!.Content.Should().Equal(["the appended body"],
            because: "an append writes exactly one chunk; the store refuses any other count");
        recording.AppendWrite.Name.Should().Be("mixedcase");
        recording.AppendWrite.Scope.Should().Be("local");
        recording.StoreWrite.Should().BeNull(
            because: "the append surface handles a missing memory itself — the orchestrator must not " +
                     "probe for one and route around it");

        result.Name.Should().Be("local:MixedCase",
            because: "the response reports the name the store resolved, not the one the caller sent");
        result.ChunkCount.Should().Be(2);
        result.Message.Should().StartWith("Appended chunk 2 to local:MixedCase");
    }

    /// <summary>
    /// The other side of that branch, and the reason it needs its own guard. Appending to a memory
    /// that does not exist yet is a store, and the STORE performs it — so the orchestrator no longer
    /// knows which happened from its own control flow and reads it back off the returned entry
    /// (<c>UpdatedAt</c> is null only for a first write). That inference selects between two
    /// different wire responses, so it is a behaviour of this class rather than something inherited
    /// from the code it replaced: a create-via-append answers the way a store does — an unqualified
    /// <c>Name</c> and a "Remembered:" message — which is also what MCP's <c>Append</c> returns for
    /// the same input, because it hands off to <c>Remember</c>.
    /// <para>Runs against the real <see cref="FileMemoryStore"/>, not the recording double: the
    /// double answers every call with the same echo entry and so cannot reach one branch rather than
    /// the other.</para>
    /// </summary>
    [Fact]
    public async Task AppendAsync_OnAMemoryThatDoesNotExistYet_AnswersTheWayAStoreDoes()
    {
        var result = await MemoryOrchestrator.AppendAsync(_store, "never-stored", "the only body");

        result.Name.Should().Be("never-stored",
            because: "a create-via-append reports the store's unqualified name, as StoreAsync does");
        result.Message.Should().StartWith("Remembered:",
            because: "nothing was appended to — the memory was created, and saying otherwise " +
                     "reports a chunk count as a position in a memory that had none");
        result.ChunkCount.Should().Be(1);

        _store.LoadIndex("local").Should().ContainSingle(e => e.Name == "never-stored",
            because: "the store created it — the response is not describing a write that did not happen");
    }

    /// <summary>
    /// The live defect this repoint removes. <c>MemoryOrchestrator.StoreAsync</c> used to catch a
    /// failed <c>Upsert</c> and call <c>DeleteArtifact</c> UNCONDITIONALLY — but on a re-store the
    /// archive and the artifact write have already run, so that delete removed the memory's only live
    /// artifact while the stale index entry survived pointing at a missing file. Nothing in this
    /// codebase reads <c>versions/</c> back, so there was no recovery path through any API: the
    /// memory was simply unreadable afterwards.
    ///
    /// <para>The failure is forced the way <c>FileMemoryStoreWriteSurfaceTests</c> forces it — a
    /// DIRECTORY where the sidecar file belongs, so the sidecar's atomic temp-then-move cannot land
    /// on any filesystem. The artifact path is untouched, so the write that must be rolled back
    /// succeeds first. The archive assertion is a PRECONDITION check: <c>ArchiveVersion</c> runs only
    /// when the existing entry resolved, so if it ever stopped resolving this test would fail there
    /// rather than passing vacuously against the brand-new-memory path.</para>
    /// </summary>
    [Fact]
    public async Task StoreAsync_FailedReStore_LeavesThePreviousContentReadable()
    {
        await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["the original body"], "orch-rollback-target"));

        string storeDir = _store.GetStoreDirForScope("local");
        string sidecar = Path.Combine(storeDir, "orch-rollback-target.meta.json");
        File.Delete(sidecar);
        Directory.CreateDirectory(sidecar);

        var restore = async () => await MemoryOrchestrator.StoreAsync(_store,
            new StoreRequest(["the replacement body"], "orch-rollback-target"));

        await restore.Should().ThrowAsync<Exception>(
            because: "a store that could not persist its entry must not report success");

        Directory.GetFiles(Path.Combine(storeDir, "versions"), "orch-rollback-target_*.nmp2")
            .Should().ContainSingle(
                because: "the existing entry resolved and its version was archived — without that " +
                         "this test would be asserting the brand-new-memory path instead");

        Directory.Delete(sidecar);

        var shown = await MemoryOrchestrator.ShowAsync(_store, "orch-rollback-target");
        shown.Should().NotBeNull(
            because: "the unconditional rollback delete left the memory unreadable through show() — " +
                     "and nothing reads versions/ back, so there was no way to get it back");
        shown!.Content.Should().Equal(["the original body"],
            because: "a failed re-store must leave the memory exactly as it was");
    }
}
