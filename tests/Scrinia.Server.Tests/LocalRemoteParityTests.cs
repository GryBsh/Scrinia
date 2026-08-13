using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// The spec's thesis, written as an assertion: the STORE owns derivation, so the same raw input
/// through a local <see cref="FileMemoryStore"/> and through an <see cref="HttpMemoryStore"/>
/// against a real server produces the SAME entry — field for field, on every write path.
///
/// <para>Derivation used to be implemented four times (see
/// <c>docs/superpowers/analysis/2026-08-11-derivation-inventory.md</c>) and had drifted far enough
/// to lose data: a REST append destroyed every curated keyword (D-12) and the whole drift baseline
/// (D-21), and nothing anywhere said so. Those four sites are one pipeline now
/// (<see cref="MemoryDerivation"/>), and this is the guard that stops them separating again. It
/// would have caught the append drift on the day it landed.</para>
///
/// <para><b>Every field is compared, and the one exclusion is named.</b> <c>Uri</c> is a
/// synthesized placeholder remotely — the wire carries no address and making <c>remote://</c>
/// dereferenceable is a separate spec's job, explicitly out of scope. Nothing else is excluded:
/// <see cref="AssertSameDerivation"/> walks <c>Keywords</c>, <c>AgentKeywords</c>,
/// <c>TermFrequencies</c>, <c>Attributes</c>, <c>ChunkEntries</c> (all four of their members),
/// <c>ContentPreview</c>, <c>ChunkCount</c>, <c>OriginalBytes</c>, <c>FileHashes</c> and
/// <c>Name</c>. <c>CreatedAt</c>/<c>UpdatedAt</c> are genuinely time-dependent, so their SEMANTICS
/// are compared instead of their values (see the helper, and
/// <see cref="ReStore_PreservesCreatedAtAndSetsUpdatedAt_OnBothPaths"/>).</para>
///
/// <para><b>Both the returned entry and the persisted one.</b> Every case asserts parity on what
/// the write surface returned AND on what a fresh read gets back, because those are two different
/// claims: the append drift was invisible in the return value and lived entirely in what was
/// written. Fixtures are created here rather than sampled from any existing corpus — a pre-cutover
/// memory carries a locally-parsed <c>review</c> timestamp that converges on UTC only when it is
/// next rewritten, so it would differ on that field alone for reasons that are not this test's
/// subject.</para>
///
/// <para>Kestrel, not <c>TestServer</c>: <c>HttpMemoryStore</c> sends synchronously and
/// <c>TestHost.ClientHandler.Send</c> throws unconditionally. Same pattern as
/// <c>RemoteWritePathMetadataTests</c> and <c>Conformance/StoreConformanceFixture</c>.</para>
/// </summary>
public sealed class LocalRemoteParityTests : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private readonly string _localRoot = Path.Combine(
        Path.GetTempPath(), "scrinia-parity-local-" + Guid.NewGuid().ToString("N")[..8]);

    private FileMemoryStore _local = null!;
    private HttpMemoryStore _remote = null!;
    private IMemoryStore? _previousStore;

    /// <summary>The dependency both memories cite. It exists byte-for-byte under BOTH workspace
    /// roots, which is what makes the <c>FileHashes</c> comparison mean something: D-04 gives a
    /// remote write the drift baseline it used to have none of, and two nulls would prove
    /// nothing.</summary>
    private const string DriftFile = "probe.txt";
    private const string DriftFileBody = "the dependency this memory cites, byte for byte";

    public async Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized.
        _factory.UseKestrel(0);
        _factory.StartServer();
        _remote = new HttpMemoryStore(_factory.CreateAuthenticatedClient(), _factory.PrimaryStore);

        Directory.CreateDirectory(_localRoot);
        _local = new FileMemoryStore(_localRoot);

        // The server's own store is a FileMemoryStore with a workspace root of its own; the hasher
        // resolves a `file` value against whichever root the deriving store holds, so the file has
        // to be under both for the two baselines to agree.
        string serverRoot = ((ILocalMemoryStore)_factory.Services
            .GetRequiredService<StoreManager>()
            .GetStore(_factory.PrimaryStore)).WorkspaceRoot;
        await File.WriteAllTextAsync(Path.Combine(_localRoot, DriftFile), DriftFileBody);
        await File.WriteAllTextAsync(Path.Combine(serverRoot, DriftFile), DriftFileBody);

        _previousStore = MemoryStoreContext.Current;
    }

    public Task DisposeAsync()
    {
        MemoryStoreContext.Current = _previousStore;
        _local.Dispose();
        _remote.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_localRoot, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>The store write both paths are handed. Deliberately dense: a curated description,
    /// a plain agent keyword and a <c>key:value</c> one (routed to attributes, D-19), a bare
    /// <c>review</c> date (canonicalized, D-06), count-shaped content (the auto review condition,
    /// D-05), a <c>file</c> reference (the drift baseline, D-04), a scalar <c>tag</c> set and an
    /// <c>anchor</c> — and two chunks, so <c>ChunkEntries</c> is populated rather than null.</summary>
    private static MemoryWrite StoreWrite(string name) => new(
        name, "local",
        ["Acme ships 12 tools per crate. ", "The second chunk mentions jolene and the harbour."],
        Description: "curated preview",
        Attributes: new Dictionary<string, string[]>
        {
            ["tag"] = ["alpha", "beta"],
            ["anchor"] = ["how do widgets work"],
            ["review"] = ["2026-01-01"],
            ["file"] = [DriftFile],
        },
        Keywords: ["vulpine", "person:jolene"]);

    /// <summary>The append both paths are handed. New tag (unioned, not replaced — D-17), a new
    /// routed keyword (D-19), a new plain keyword, and no description (so the existing preview must
    /// survive, D-20).</summary>
    private static MemoryWrite AppendWrite(string name) => new(
        name, "local",
        ["An appended chunk about maria and the cargo ships."],
        Attributes: new Dictionary<string, string[]>
        {
            ["tag"] = ["gamma"],
        },
        Keywords: ["cetacean", "person:maria"]);

    private ArtifactEntry ReloadLocal(string name) =>
        _local.LoadIndex("local").FirstOrDefault(e => e.Name == name)
            ?? throw new InvalidOperationException($"'{name}' is not in the local index.");

    private ArtifactEntry ReloadRemote(string name) =>
        _remote.LoadIndex("local").FirstOrDefault(e => e.Name == name)
            ?? throw new InvalidOperationException($"'{name}' is not in the remote listing.");

    // ── The cases ────────────────────────────────────────────────────────────

    /// <summary>
    /// Store. The brief's case, widened to every field the entry carries.
    /// </summary>
    [Fact]
    public async Task Store_SameInput_ProducesTheSameEntry_FieldForField()
    {
        const string name = "parity-store";

        var local = await _local.StoreAsync(StoreWrite(name));
        var remote = await _remote.StoreAsync(StoreWrite(name));

        AssertStorePathIsSubstantial(local);
        AssertSameDerivation(local, remote, "the store write surface");
        AssertSameDerivation(ReloadLocal(name), ReloadRemote(name), "the persisted store entry");
    }

    /// <summary>
    /// <c>CreatedAt</c> and <c>UpdatedAt</c> are the two fields whose VALUES cannot agree — the two
    /// writes happen at different instants — so what has to agree is their semantics, and this is
    /// where they are pinned rather than waved at. <c>UpdatedAt</c> null is what
    /// <c>MemoryOrchestrator.AppendAsync</c> discriminates a create-via-append on, so the two
    /// backends disagreeing about it would change a REST response shape, not just a timestamp.
    /// </summary>
    [Fact]
    public async Task ReStore_PreservesCreatedAtAndSetsUpdatedAt_OnBothPaths()
    {
        const string name = "parity-restore";

        var localFirst = await _local.StoreAsync(StoreWrite(name));
        var remoteFirst = await _remote.StoreAsync(StoreWrite(name));

        localFirst.UpdatedAt.Should().BeNull(
            because: "a first write has never been updated — this is the discriminator the REST " +
                     "append response selects its whole message on");
        remoteFirst.UpdatedAt.Should().BeNull(
            because: "and the remote path must call a first write a first write for the same reason");

        var localSecond = await _local.StoreAsync(StoreWrite(name));
        var remoteSecond = await _remote.StoreAsync(StoreWrite(name));

        localSecond.CreatedAt.Should().Be(localFirst.CreatedAt,
            because: "a re-store rewrites content, not the memory's age");
        remoteSecond.CreatedAt.Should().Be(remoteFirst.CreatedAt,
            because: "and the remote store must carry CreatedAt forward exactly as the local one does");

        localSecond.UpdatedAt.Should().NotBeNull();
        remoteSecond.UpdatedAt.Should().NotBeNull(
            because: "a re-store IS an update on both paths — reporting null here would make the " +
                     "server answer a re-store the way it answers a first write");

        // Everything else must still agree after the second write, which is the write that also
        // exercises the archive-and-replace branch rather than the create branch.
        AssertSameDerivation(localSecond, remoteSecond, "the re-store write surface");
        AssertSameDerivation(ReloadLocal(name), ReloadRemote(name), "the persisted re-stored entry");
    }

    /// <summary>
    /// Append — the path whose drift was the worst. A REST append used to pass <c>null</c> where the
    /// memory's own keywords belonged (D-12), so it destroyed every curated keyword, and it built an
    /// entry with no <c>FileHashes</c> at all (D-21), so it destroyed the drift baseline. Both were
    /// invisible to the MCP suite because MCP-in-remote-mode POSTs to <c>/memories</c> and never
    /// reaches the append route.
    /// </summary>
    [Fact]
    public async Task Append_SameInput_ProducesTheSameEntry_FieldForField()
    {
        const string name = "parity-append";

        await _local.StoreAsync(StoreWrite(name));
        await _remote.StoreAsync(StoreWrite(name));

        var local = await _local.AppendAsync(AppendWrite(name));
        var remote = await _remote.AppendAsync(AppendWrite(name));

        AssertAppendPathIsSubstantial(local);
        AssertSameDerivation(local, remote, "the append write surface");
        AssertSameDerivation(ReloadLocal(name), ReloadRemote(name), "the persisted appended entry");
    }

    /// <summary>
    /// Compact. It derived NOTHING before this branch — it handed the existing entry's metadata
    /// straight back to the write seam and nulled <c>ChunkEntries</c> — and it now routes through
    /// the store's <see cref="MemoryDerivation.ForStore"/> pass like any other whole-memory rewrite
    /// (D-27, D-28). It is also the only consumer of <c>AgentKeywords</c>: it reads the provenance
    /// off the entry to decide which keywords may travel back in as caller input, because handing
    /// the merged list back would re-boost every auto term to +5 on every compaction (D-29).
    ///
    /// <para><b>The curated keyword's text does not survive the compaction, deliberately.</b>
    /// <c>harbour</c> appears only in the chunk <c>keepRecent: 1</c> drops, so after the rewrite
    /// nothing in the content yields it and the subtraction heuristic cannot recover it. Only the
    /// recorded provenance can — which is exactly where local and remote could plausibly diverge,
    /// since remotely that provenance has to survive a wire round trip before the client's
    /// compaction reads it. If <c>agentKeywords</c> fell off the wire the remote entry would keep
    /// <c>harbour</c> at auto weight, or lose it entirely, while the local one kept it at +5.</para>
    ///
    /// <para>Compaction is MCP-only — there is no server compact route — so it is driven through
    /// the tool with the ambient store swapped underneath it, which is precisely the "same caller,
    /// two backends" shape the thesis is about.</para>
    /// </summary>
    [Fact]
    public async Task Compact_SameMemory_ProducesTheSameEntry_FieldForField()
    {
        const string name = "parity-compact";
        string[] content = ["A harbour full of boats. ", "The surviving chunk is about cargo and crates."];
        string[] keywords = ["harbour"];

        // The curated state a compaction must carry INTO the write rather than reset: remotely it
        // reaches the compaction only by travelling back over the wire first, so these are the
        // fields where a read-side omission would show up as a store-mode divergence.
        var attributes = new Dictionary<string, string[]>
        {
            ["tag"] = ["delta"],
            ["file"] = [DriftFile],
        };

        var beforeLocal = await _local.StoreAsync(new MemoryWrite(
            name, "local", content, Description: "curated preview", Attributes: attributes,
            Keywords: keywords));
        var beforeRemote = await _remote.StoreAsync(new MemoryWrite(
            name, "local", content, Description: "curated preview", Attributes: attributes,
            Keywords: keywords));

        beforeLocal.AgentKeywords.Should().Equal(["harbour"],
            because: "precondition: the pipeline recorded the provenance the compaction below reads");
        beforeRemote.AgentKeywords.Should().Equal(["harbour"],
            because: "precondition: and the wire carried it back — without this the remote " +
                     "compaction would be guessing and the comparison would be against a guess");

        MemoryStoreContext.Current = _local;
        (await new ScriniaMcpTools().MemoryCompact(path: name, keepRecent: 1))
            .Should().Contain("2 -> 1", because: "the local compaction has to actually run");

        MemoryStoreContext.Current = _remote;
        (await new ScriniaMcpTools().MemoryCompact(path: name, keepRecent: 1))
            .Should().Contain("2 -> 1", because: "and so does the remote one");

        MemoryStoreContext.Current = null;

        var local = ReloadLocal(name);
        var remote = ReloadRemote(name);

        local.ChunkCount.Should().Be(1, because: "precondition: a chunk really was dropped");
        local.AgentKeywords.Should().Equal(["harbour"],
            because: "precondition: the curated keyword survived a compaction that removed every " +
                     "occurrence of its text — if it did not, this case is not testing what it says");
        local.Keywords.Should().Contain("harbour");
        local.TermFrequencies.Should().NotBeNull().And.ContainKey("harbour");
        local.ContentPreview.Should().Be("curated preview",
            because: "precondition: compaction rewrites content, not the curated description");
        local.Attributes.Should().NotBeNull().And.ContainKey("tag");
        local.FileHashes.Should().NotBeNull().And.ContainKey(DriftFile,
            because: "precondition: the drift baseline is re-derived from the `file` attribute the " +
                     "compaction carried into the write, so it is a live field in this comparison");

        AssertSameDerivation(local, remote, "the compacted entry");
    }

    /// <summary>
    /// Metadata update. <c>RemoteLoadIndexTests.TryUpdateEntry_TermFrequencyReDerivation_AgreesWithTheLocalStore</c>
    /// already pins the narrow claim — the two backends seed the same term-frequency DELTA for an
    /// added attribute — and this does not repeat it. It closes the hole beside it: that test
    /// compares one dictionary entry's delta, over two entries written through different entry
    /// points, and says nothing about the rest of the entry. Here both sides start from the same
    /// raw input, so the whole entry is comparable in absolute terms after the mutation, including
    /// the fields a metadata update must leave alone — <c>AgentKeywords</c> in particular, which
    /// <see cref="MemoryDerivation.ForMetadataUpdate"/> writes and which nothing else in the suite
    /// checks agrees across backends.
    /// </summary>
    [Fact]
    public async Task MetadataUpdate_SameMutation_ProducesTheSameEntry_FieldForField()
    {
        const string name = "parity-update";

        await _local.StoreAsync(StoreWrite(name));
        await _remote.StoreAsync(StoreWrite(name));

        // Attributes only: PATCH refuses a keyword change, deliberately, so a keyword mutation is
        // not a comparable operation across the two backends (and is pinned as a refusal in
        // RemoteLoadIndexTests).
        static ArtifactEntry Mutate(ArtifactEntry entry) => entry with
        {
            Attributes = new Dictionary<string, string[]>(
                entry.Attributes ?? [], StringComparer.OrdinalIgnoreCase) { ["person"] = ["zanzibar"] },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _local.TryUpdateEntry(name, "local", Mutate).Should().BeTrue();
        _remote.TryUpdateEntry(name, "local", Mutate).Should().BeTrue(
            because: "the remote metadata channel has to persist the write, not report success " +
                     "over one that never reached the server");

        var local = ReloadLocal(name);
        var remote = ReloadRemote(name);

        local.Attributes.Should().NotBeNull().And.ContainKey("person",
            because: "precondition: the mutation landed locally, so there is something to compare");
        local.TermFrequencies.Should().NotBeNull().And.ContainKey("zanzibar",
            because: "precondition: the local store re-derived the boost — comparing two stores " +
                     "that both derived nothing would pass and prove nothing");

        AssertSameDerivation(local, remote, "the metadata-updated entry");
    }

    /// <summary>
    /// The ephemeral write, asserted honestly.
    ///
    /// <para><b>A parity assertion here would be vacuous.</b> <c>HttpMemoryStore</c> serves a
    /// <c>~name</c> write IN PROCESS, through a local <see cref="FileMemoryStore"/> helper, because
    /// in that store an ephemeral name addresses this process and not the store behind it. So
    /// "local and remote derive the same entry" reduces to "a FileMemoryStore agrees with itself" —
    /// it cannot fail, and a test that cannot fail is worse than none.</para>
    ///
    /// <para>What CAN regress is the routing, and that is what this pins: POSTing the write instead
    /// would land the memory in the SERVER's ephemeral map, where nothing on this side can ever read
    /// it back — <c>remember({ path: '/temp/x' })</c> would report success and <c>recall</c> would
    /// answer "not found". So: readable here, absent there, and derived (not a bare echo of the
    /// input) on the way through.</para>
    /// </summary>
    [Fact]
    public async Task EphemeralWrite_IsDerivedInProcess_AndNeverReachesTheServer()
    {
        const string name = "~parity-ephemeral";
        const string key = "parity-ephemeral";

        var entry = await _remote.StoreAsync(new MemoryWrite(
            name, "local", ["An ephemeral note about the harbour."], Keywords: ["harbour"]));

        entry.Keywords.Should().Contain("harbour");
        entry.AgentKeywords.Should().Equal(["harbour"],
            because: "the in-process write goes through the same MemoryDerivation pass a persistent " +
                     "one does — an ephemeral is not a metadata-free echo of its input");
        entry.TermFrequencies.Should().NotBeNull().And.ContainKey("harbour");

        _remote.GetEphemeral(key).Should().NotBeNull(
            because: "the memory is addressed to THIS process and every ephemeral read here is " +
                     "answered from the client's own map");

        var serverStore = _factory.Services.GetRequiredService<StoreManager>()
            .GetStore(_factory.PrimaryStore);
        serverStore.GetEphemeral(key).Should().BeNull(
            because: "sending it over the wire would put it in the server's ephemeral map, which " +
                     "nothing on the client can read — a success report over an unreadable memory");
    }

    // ── The comparison ───────────────────────────────────────────────────────

    /// <summary>
    /// Every field of the entry, one at a time, with the single exclusion named in the class doc.
    ///
    /// <para><b>Nothing here is order-insensitive by accident.</b> Keyword ORDER is load-bearing —
    /// D-12's guarantee is that the memory's own keywords LEAD the merged list, so nothing it
    /// carried can be crowded out — and attribute value order is what the caller supplied. Only key
    /// SETS (which a dictionary does not order) are compared without ordering.</para>
    /// </summary>
    private static void AssertSameDerivation(ArtifactEntry local, ArtifactEntry remote, string what)
    {
        remote.Name.Should().Be(local.Name, because: $"{what}: the resolved name");
        remote.OriginalBytes.Should().Be(local.OriginalBytes, because: $"{what}: OriginalBytes");
        remote.ChunkCount.Should().Be(local.ChunkCount, because: $"{what}: ChunkCount");
        remote.ContentPreview.Should().Be(local.ContentPreview, because: $"{what}: ContentPreview");

        SameStrings(local.Keywords, remote.Keywords, $"{what}: Keywords");
        SameStrings(local.AgentKeywords, remote.AgentKeywords, $"{what}: AgentKeywords");
        SameCounts(local.TermFrequencies, remote.TermFrequencies, $"{what}: TermFrequencies");
        SameAttributes(local.Attributes, remote.Attributes, $"{what}: Attributes");
        SameHashes(local.FileHashes, remote.FileHashes, $"{what}: FileHashes");
        SameChunkEntries(local.ChunkEntries, remote.ChunkEntries, $"{what}: ChunkEntries");

        // CreatedAt and UpdatedAt are genuinely time-dependent — the two writes happen at different
        // instants — so what is compared is the semantics: "has this been updated" must be the same
        // answer on both backends. The VALUE relationships (CreatedAt survives a re-store, UpdatedAt
        // appears on the second write) are pinned in ReStore_PreservesCreatedAtAndSetsUpdatedAt.
        if (local.UpdatedAt is null)
            remote.UpdatedAt.Should().BeNull(because: $"{what}: UpdatedAt is unset locally");
        else
            remote.UpdatedAt.Should().NotBeNull(because: $"{what}: UpdatedAt is set locally");

        // Uri is the one excluded field. It is a synthesized placeholder remotely — the wire carries
        // no address — and making remote:// dereferenceable is a separate spec's job, out of scope
        // here. Asserted rather than skipped in silence, so the exclusion stays honest about what it
        // is excluding.
        local.Uri.Should().NotBeEmpty(because: $"{what}: a local entry addresses its own artifact");
        remote.Uri.Should().BeEmpty(
            because: $"{what}: the only excluded field, and the exclusion holds because the remote " +
                     "entry carries no address at all rather than a differently-shaped one");
    }

    /// <summary>The store case really did derive something under every field it compares — two
    /// nulls agree perfectly and prove nothing.</summary>
    private static void AssertStorePathIsSubstantial(ArtifactEntry local)
    {
        local.ChunkCount.Should().Be(2);
        local.ContentPreview.Should().Be("curated preview");
        local.Keywords.Should().NotBeNullOrEmpty().And.Contain("vulpine");
        local.AgentKeywords.Should().Equal(["vulpine"],
            because: "the plain agent keyword is provenance; person:jolene was routed to an " +
                     "attribute before the merge and so is not in the keyword list to be marked");
        local.TermFrequencies.Should().NotBeNullOrEmpty();
        local.ChunkEntries.Should().NotBeNull().And.HaveCount(2);
        local.Attributes.Should().NotBeNull()
            .And.ContainKey("tag")
            .And.ContainKey("anchor")
            .And.ContainKey("person")
            .And.ContainKey("review")
            .And.ContainKey("file");
        local.Attributes!["review"].Should().HaveCountGreaterThan(1,
            because: "the bare date was canonicalized (D-06) and count-shaped content added the " +
                     "auto review condition (D-05) — both are derivation output worth comparing");
        local.FileHashes.Should().NotBeNull().And.ContainKey(DriftFile,
            because: "D-04 gives a write its drift baseline through whichever store holds the " +
                     "workspace — comparing two absent baselines would be the vacuous case");
    }

    /// <summary>The same, for append: the three things a REST append used to destroy or never
    /// build.</summary>
    private static void AssertAppendPathIsSubstantial(ArtifactEntry local)
    {
        local.ChunkCount.Should().Be(3);
        local.ContentPreview.Should().Be("curated preview",
            because: "the append carried no description, so the existing preview survives (D-20)");
        local.Keywords.Should().Contain("vulpine",
            because: "D-12: the memory's curated keyword is carried forward, not dropped");
        local.AgentKeywords.Should().Contain("vulpine").And.Contain("cetacean",
            because: "the recorded provenance plus this append's own plain keyword");
        local.Attributes.Should().NotBeNull();
        local.Attributes!["tag"].Should().BeEquivalentTo(["alpha", "beta", "gamma"],
            because: "D-17: append unions tag rather than letting the scalar merge replace it");
        local.Attributes.Should().ContainKey("person");
        local.Attributes["person"].Should().Contain("maria",
            because: "D-19: a key:value keyword on an append becomes an attribute");
        local.FileHashes.Should().NotBeNull().And.ContainKey(DriftFile,
            because: "D-21: the drift baseline is carried forward — a REST append used to have no " +
                     "FileHashes field at all");
        local.ChunkEntries.Should().NotBeNull().And.HaveCount(3);
    }

    private static void SameStrings(string[]? local, string[]? remote, string field)
    {
        if (local is null)
        {
            remote.Should().BeNull(because: $"{field} is null locally");
            return;
        }

        remote.Should().NotBeNull(because: $"{field} is populated locally");
        remote!.Should().BeEquivalentTo(local, o => o.WithStrictOrdering(),
            because: $"{field} must match, in order — position is meaning in a keyword list");
    }

    private static void SameCounts(
        Dictionary<string, int>? local, Dictionary<string, int>? remote, string field)
    {
        if (local is null)
        {
            remote.Should().BeNull(because: $"{field} is null locally");
            return;
        }

        remote.Should().NotBeNull(because: $"{field} is populated locally");
        remote!.Keys.ToArray().Should().BeEquivalentTo(local.Keys.ToArray(),
            because: $"{field} must index exactly the same terms");
        foreach (var (term, count) in local)
            remote[term].Should().Be(count, because: $"{field}['{term}'] must weigh the same");
    }

    private static void SameAttributes(
        Dictionary<string, string[]>? local, Dictionary<string, string[]>? remote, string field)
    {
        if (local is null)
        {
            remote.Should().BeNull(because: $"{field} is null locally");
            return;
        }

        remote.Should().NotBeNull(because: $"{field} is populated locally");
        remote!.Keys.ToArray().Should().BeEquivalentTo(local.Keys.ToArray(),
            because: $"{field} must carry exactly the same keys");
        foreach (var (key, values) in local)
            remote[key].Should().BeEquivalentTo(values, o => o.WithStrictOrdering(),
                because: $"{field}['{key}'] must carry the same values, in the same order");
    }

    private static void SameHashes(
        Dictionary<string, string>? local, Dictionary<string, string>? remote, string field)
    {
        if (local is null)
        {
            remote.Should().BeNull(because: $"{field} is null locally");
            return;
        }

        remote.Should().NotBeNull(because: $"{field} is populated locally");
        remote!.Keys.ToArray().Should().BeEquivalentTo(local.Keys.ToArray(),
            because: $"{field} must baseline exactly the same paths");
        foreach (var (path, hash) in local)
            remote[path].Should().Be(hash, because: $"{field}['{path}'] must be the same hash");
    }

    private static void SameChunkEntries(
        ChunkEntry[]? local, ChunkEntry[]? remote, string field)
    {
        if (local is null)
        {
            remote.Should().BeNull(
                because: $"{field} is null locally — a chunk entry present on one path only is " +
                          "the D-23/D-28 class of drift");
            return;
        }

        remote.Should().NotBeNull(because: $"{field} is populated locally");
        remote!.Length.Should().Be(local.Length, because: $"{field} must have the same count");

        for (int i = 0; i < local.Length; i++)
        {
            remote[i].ChunkIndex.Should().Be(local[i].ChunkIndex, because: $"{field}[{i}].ChunkIndex");
            remote[i].ContentPreview.Should().Be(local[i].ContentPreview,
                because: $"{field}[{i}].ContentPreview — including null vs \"\", which is D-23");
            SameStrings(local[i].Keywords, remote[i].Keywords, $"{field}[{i}].Keywords");
            SameCounts(local[i].TermFrequencies, remote[i].TermFrequencies,
                $"{field}[{i}].TermFrequencies");
        }
    }
}
