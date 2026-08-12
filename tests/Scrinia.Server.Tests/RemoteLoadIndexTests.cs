using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// <c>HttpMemoryStore.LoadIndex</c> used to return <c>[]</c> unconditionally, so
/// <c>IMemoryStore</c>'s default <c>TryUpdateEntry</c> always found nothing and reported
/// "not found" for a memory that genuinely exists — entity-typing promotion writes evaporated
/// remotely with no error. This pins the read-side fix: <c>LoadIndex</c> now proxies to the
/// listing endpoint instead of lying.
///
/// <para>It also pins why <c>HttpMemoryStore</c> now overrides <c>TryUpdateEntry</c> instead of
/// inheriting the default: <c>Upsert</c> is a deliberate no-op remotely (writes go through
/// <c>WriteArtifactAsync</c> instead), so once <c>LoadIndex</c> stops lying, the inherited
/// default would find the entry, apply the mutation, hand it to the no-op <c>Upsert</c>, and
/// still report <c>true</c> — a success report for a write that never reached the server. That
/// violates the contract <c>IMemoryStore.TryUpdateEntry</c>'s own doc states (the return value
/// means the mutation was persisted), regardless of who currently reads it: the one production
/// caller, <c>EntityTypingEventHandler.PromoteAsync</c>, calls this as a discarded statement and
/// does not branch on the result or retry either way, so this is a correctness fix for the
/// contract rather than a fix for an observed live bug. The override fails closed: it returns
/// <c>false</c> whenever it cannot actually persist, rather than trading "reports failure for a
/// memory that exists" for "reports success for a write that didn't happen".</para>
///
/// <para>Must be driven over a real Kestrel socket, not <c>TestServer</c>'s in-memory handler:
/// <c>HttpMemoryStore</c> sends synchronously (<c>HttpClient.Send</c>), and
/// <c>Microsoft.AspNetCore.TestHost.ClientHandler.Send</c> throws <c>NotSupportedException</c>
/// unconditionally. See <c>Conformance/StoreConformanceFixture.cs</c> for the same pattern.</para>
/// </summary>
public sealed class RemoteLoadIndexTests : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private HttpClient _client = null!;
    private HttpMemoryStore _remote = null!;

    public Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized, which the
        // first touch of Services or CreateClient would do (mirrors StoreConformanceFixture).
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateAuthenticatedClient();
        _remote = new HttpMemoryStore(_client, _factory.PrimaryStore);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // HttpMemoryStore owns _client's disposal (its Dispose() disposes the HttpClient it was
        // constructed with), so no separate _client.Dispose() here.
        _remote.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A store client whose key carries exactly <paramref name="permissions"/> — the seam
    /// for asserting which permissions a member actually requires, rather than discovering it in a
    /// deployment.</summary>
    private HttpMemoryStore NarrowKeyStore(params string[] permissions)
    {
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, _, _) = keyStore.CreateKey(
            $"narrow-{Guid.NewGuid():N}", [_factory.PrimaryStore], permissions, "narrow");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);
        return new HttpMemoryStore(client, _factory.PrimaryStore);
    }

    private async Task StoreAsync(string name, string body,
        Dictionary<string, string[]>? attributes = null, string[]? keywords = null)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body, Attributes: attributes, Keywords: keywords));
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Stores a genuinely multi-chunk memory — one wire element per chunk. The sibling
    /// helper above always sends a single element, so nothing it stores can distinguish a client
    /// that preserves chunk boundaries from one that flattens them.</summary>
    private async Task StoreChunksAsync(string name, string[] chunks)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest(chunks, name, chunks[0]));
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The read-side half of the complete-entry wire contract: <c>ShowResponse.Content</c> is a
    /// chunk ARRAY, and the client must re-encode it as one NMP/2 chunk per element. It used to
    /// send joined text that the client re-encoded as a single chunk while the response claimed
    /// five — the flattening recorded as a residual in
    /// <c>RemoteWritePathMetadataTests.Compact_RemoteStore_ShortCircuits_...</c>.
    /// <para>Nothing else pins this. The chunk array landed with the wire reshape (Task 6), which
    /// forced <c>EncodeChunks</c> at the call site because <c>Encode</c> takes a <c>string</c> —
    /// so the fix is already in place and this test is green on arrival. That makes it a
    /// regression guard rather than a red-green step, and it is a real one: measured by reverting
    /// the call to <c>Encode(string.Concat(show.Content))</c>, which compiles and fails only
    /// this test and its sibling's five-chunk assertion.</para>
    /// </summary>
    [Fact]
    public async Task Remote_FiveChunkMemory_KeepsItsChunkBoundaries()
    {
        await StoreChunksAsync("chunky", ["alpha", "bravo", "charlie", "delta", "echo"]);

        string artifact = await _remote.ReadArtifactAsync("chunky", "local");

        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(5,
            because: "the wire carries the chunk array now; it used to send joined text that the " +
                     "client re-encoded as a single chunk while the response claimed five");
        Nmp2ChunkedEncoder.DecodeChunk(artifact, 3).Should().Contain("charlie");
    }

    /// <summary>The single-chunk case is not implied by the five-chunk one: a client that always
    /// re-encoded the array as N chunks would satisfy the test above and could still mis-handle
    /// the one-element array every ordinary memory in this suite produces.</summary>
    [Fact]
    public async Task Remote_SingleChunkMemory_StillRoundTrips()
    {
        await StoreChunksAsync("solo", ["only"]);
        Nmp2ChunkedEncoder.GetChunkCount(await _remote.ReadArtifactAsync("solo", "local")).Should().Be(1);
    }

    [Fact]
    public async Task LoadIndex_ReturnsTheStoredEntry()
    {
        await StoreAsync("loadindex-probe", "probe body for LoadIndex");

        var entries = _remote.LoadIndex("local");

        entries.Should().Contain(e => e.Name == "loadindex-probe",
            because: "the memory was just stored server-side and LoadIndex must proxy to the " +
                     "listing endpoint instead of returning an unconditional empty list");
    }

    /// <summary>
    /// The load-bearing assertion, and the one this task inverts. <c>TryUpdateEntry</c> used to
    /// fail closed — find the entry, run the mutation, then refuse — because a remote store had no
    /// channel that could persist a metadata-only change. It has one now
    /// (<c>GET /memories/{name}/entry</c> + <c>PATCH /memories/{name}</c>), so refusing would be
    /// the new lie: a caller told "not persisted" about a change the server would happily accept.
    ///
    /// <para>The assertion that carries the weight is the re-read, not the return value: it is made
    /// with a fresh, independent <c>LoadIndex</c> call rather than the in-memory <c>entry</c> local,
    /// so it can only pass if the mutation genuinely reached the server. A <c>=> true</c> stub
    /// satisfies <c>updated.Should().BeTrue()</c> and fails this.</para>
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_PersistsTheMutation_ThroughThePatchChannel()
    {
        await StoreAsync("tryupdate-probe", "probe body for TryUpdateEntry",
            attributes: new Dictionary<string, string[]> { ["mention"] = ["acme"] });

        bool mutateRan = false;
        bool updated = _remote.TryUpdateEntry("tryupdate-probe", "local", entry =>
        {
            mutateRan = true;
            return entry with
            {
                Attributes = new Dictionary<string, string[]>(entry.Attributes!) { ["person"] = ["acme"] },
                // Stamped deliberately, mirroring MemoryTools.UpdateMeta, the real caller. The
                // PATCH body's UpdatedAt is an optimistic-concurrency TOKEN compared against the
                // stored value, so it must carry what the client READ, not what the mutation wrote
                // — send the mutated one and every PATCH is a guaranteed 409. A mutation that
                // leaves UpdatedAt alone cannot tell those two apart.
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        });

        mutateRan.Should().BeTrue(
            because: "the entry must actually have been read back and handed to the mutation " +
                     "callback — a constant override would satisfy the return-value assertion " +
                     "below without exercising either");
        updated.Should().BeTrue(
            because: "the PATCH channel exists now, so a mutation the server accepts must be " +
                     "reported as persisted — the fail-closed refusal was honest only while there " +
                     "was nothing to persist through");

        var reloaded = _remote.LoadIndex("local").FirstOrDefault(e => e.Name == "tryupdate-probe");
        reloaded.Should().NotBeNull();
        reloaded!.Attributes.Should().ContainKey("person",
            because: "the return value means the mutation was PERSISTED, and the only way to " +
                     "prove that is to read the entry back off the server");
        reloaded.Attributes!["person"].Should().BeEquivalentTo(["acme"]);
        reloaded.Attributes.Should().ContainKey("mention",
            because: "a PATCH replaces the attribute map with the caller's edited copy — it must " +
                     "not drop the keys the caller left alone");
    }

    /// <summary>
    /// The measured Critical this task's first pass shipped: <c>update()</c> and <c>link()</c>
    /// reported full success remotely while silently discarding the BM25 boost they had computed
    /// for the attribute they just added — measured <c>returned=True; attributesPersisted=True;
    /// tfHasAcme=False</c>. Term frequencies are what BM25 scores against, so the entry became
    /// permanently unfindable by its own new attribute, and a remote corpus ranked differently from
    /// a local one forever, with nothing anywhere to say so.
    ///
    /// <para>It could not be fixed by letting the boost travel: <c>PATCH</c> refuses
    /// <c>TermFrequencies</c> from a body, deliberately and correctly, so a client must not be
    /// computing one at all. The rule moved into <c>MemoryDerivation.ForMetadataUpdate</c> and is
    /// applied inside <c>TryUpdateEntry</c> by whichever side holds the store — here, the server's
    /// own <c>FileMemoryStore</c> behind the PATCH handler.</para>
    ///
    /// <para>Read back with a fresh <c>LoadIndex</c>, never the returned value: the whole failure
    /// mode was a truthful-looking return over a write that had not fully happened.</para>
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_ReDerivesTermFrequencies_WhenAMutationAddsAnAttribute()
    {
        await StoreAsync("tf-rederive-probe", "probe body for term frequency re-derivation");

        bool updated = _remote.TryUpdateEntry("tf-rederive-probe", "local", entry => entry with
        {
            Attributes = new Dictionary<string, string[]>(
                entry.Attributes ?? [], StringComparer.OrdinalIgnoreCase) { ["person"] = ["zanzibar"] },
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        updated.Should().BeTrue();

        var reloaded = _remote.LoadIndex("local").Single(e => e.Name == "tf-rederive-probe");
        reloaded.Attributes.Should().ContainKey("person");
        reloaded.TermFrequencies.Should().NotBeNull()
            .And.ContainKey("zanzibar",
                because: "the attribute's value tokens must reach the BM25 index. Reporting the " +
                         "write as successful while the index never learned the term is the " +
                         "silent divergence this closes — the entry would be unfindable by the " +
                         "very attribute the caller just added");
    }

    /// <summary>
    /// The same mutation, on a local <c>FileMemoryStore</c> and on the remote store, must produce
    /// the same term-frequency delta. Asserting the remote side alone would pass against a remote
    /// store that seeded at some other weight than the local one — which is exactly what the code
    /// this replaced did (<c>UpdateMeta</c> seeded attributes at +5 while every store write path
    /// seeded at +3), so "it indexes something" is not the property worth pinning. "Both backends
    /// agree" is.
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_TermFrequencyReDerivation_AgreesWithTheLocalStore()
    {
        const string body = "probe body for local/remote term frequency parity";
        await StoreAsync("tf-parity-probe", body);

        using var localDir = new TempStoreDir();
        using var local = new Scrinia.Core.FileMemoryStore(localDir.Path);
        await local.StoreAsync(new MemoryWrite("tf-parity-probe", "local", [body], body));

        static ArtifactEntry Mutate(ArtifactEntry entry) => entry with
        {
            Attributes = new Dictionary<string, string[]>(
                entry.Attributes ?? [], StringComparer.OrdinalIgnoreCase) { ["person"] = ["zanzibar"] },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var before = local.LoadIndex("local").Single(e => e.Name == "tf-parity-probe");
        local.TryUpdateEntry("tf-parity-probe", "local", Mutate).Should().BeTrue();
        _remote.TryUpdateEntry("tf-parity-probe", "local", Mutate).Should().BeTrue();

        var localAfter = local.LoadIndex("local").Single(e => e.Name == "tf-parity-probe");
        var remoteAfter = _remote.LoadIndex("local").Single(e => e.Name == "tf-parity-probe");

        // Asserted before the arithmetic below so a store that derives nothing fails as a stated
        // expectation rather than as a KeyNotFoundException out of the indexer.
        localAfter.TermFrequencies.Should().NotBeNull().And.ContainKey("zanzibar",
            because: "the local store must seed the new attribute value — if it does not, there is " +
                     "no baseline to compare the remote one against and this test proves nothing");
        remoteAfter.TermFrequencies.Should().NotBeNull().And.ContainKey("zanzibar");

        // The delta, not the absolute map: the two stores derived their baselines from the same
        // content but the fixture's remote write went through a different entry point, so only the
        // change this mutation caused is meaningfully comparable.
        int localDelta = localAfter.TermFrequencies!["zanzibar"]
            - (before.TermFrequencies?.TryGetValue("zanzibar", out int b) == true ? b : 0);
        remoteAfter.TermFrequencies!["zanzibar"].Should().Be(localDelta,
            because: "one rule, one implementation, applied by whichever side holds the store — a " +
                     "remote metadata write must weight the index exactly as the local one does");
    }

    /// <summary>
    /// <c>MaintenanceEventSink</c>'s orphan branch was a silent no-op against a remote store: both
    /// of its sub-branches returned <c>Keywords = …</c>, <c>PATCH</c> refuses a keyword change,
    /// <c>TryUpdateEntry</c> returned <c>false</c>, and the call site discarded it. Locally the
    /// entry got marked; remotely it never did, so <c>search</c>/<c>list</c> answered differently
    /// on the two backends with nothing to explain why.
    ///
    /// <para>Closed by moving the marker to an <c>orphan</c> ATTRIBUTE — see the sink's own doc for
    /// why that is the right carrier and not a workaround. This drives the real sink against the
    /// real remote store and asserts the marker on a fresh read, which is the only thing that can
    /// tell "it works remotely" apart from "it returned without complaining".</para>
    /// </summary>
    [Fact]
    public async Task MaintenanceSink_MarksAnOrphan_ThroughTheRemoteStore()
    {
        const string body = "isolated content with no inbound references";
        await StoreAsync("orphan-remote-probe", body);

        await new MaintenanceEventSink().OnStoredAsync(
            "orphan-remote-probe", [body], _remote, CancellationToken.None);

        var entry = _remote.LoadIndex("local").Single(e => e.Name == "orphan-remote-probe");
        entry.Attributes.Should().NotBeNull().And.ContainKey("orphan",
            because: "the marker must reach the server. As a keyword it could not: PATCH refuses a " +
                     "keyword change, so this write 400'd and was discarded while the identical " +
                     "local write succeeded");
        entry.Keywords.Should().NotContain("orphan",
            because: "the sink must not be writing keywords at all — that is the carrier the wire " +
                     "correctly refuses, and using it is what made this remote-only dead code");
    }

    /// <summary>
    /// The clear must be TWO writes, and this is why. Marking leaves a legacy entry's <c>orphan</c>
    /// keyword alone, so dual-marked entries are the normal migration outcome — which makes this
    /// the common path, not a corner. Clearing both carriers in one <c>TryUpdateEntry</c> made a
    /// remote store refuse the whole mutation (<c>PATCH</c> rejects a <c>Keywords</c> change), so
    /// NEITHER was cleared and a memory with demonstrable inbound refs kept <c>orphan:true</c>
    /// indefinitely — strictly worse than clearing the attribute and leaving the keyword, which is
    /// what the code did one commit earlier.
    ///
    /// <para>Both halves are asserted deliberately. The attribute assertion is the floor that was
    /// lost; the keyword assertion records what a remote store genuinely cannot do, so a future
    /// reader does not "fix" the split back into one write on the assumption both should land.</para>
    /// </summary>
    [Fact]
    public async Task MaintenanceSink_ClearsTheOrphanAttributeRemotely_EvenWhenTheLegacyKeywordCannotBeRemoved()
    {
        const string body = "the once-orphaned entry";
        await StoreAsync("orphan-remote-clear-probe", body);

        // Dual-marked, exactly as migration leaves an entry written before the marker moved: the
        // attribute (written by the sink) alongside the legacy keyword.
        _remote.TryUpdateEntry("orphan-remote-clear-probe", "local", e => e with
        {
            Attributes = new Dictionary<string, string[]>(
                e.Attributes ?? [], StringComparer.OrdinalIgnoreCase) { ["orphan"] = ["true"] },
            UpdatedAt = DateTimeOffset.UtcNow,
        }).Should().BeTrue();

        // Planted through the server's own store: the wire refuses a keyword change, which is the
        // whole point — a legacy entry cannot be CREATED over the wire either.
        var serverStore = (Scrinia.Core.FileMemoryStore)_factory.Services
            .GetRequiredService<Scrinia.Server.Services.StoreManager>().GetStore(_factory.PrimaryStore);
        serverStore.TryUpdateEntry("orphan-remote-clear-probe", "local", e => e with
        {
            Keywords = [.. e.Keywords ?? [], "orphan"],
        }).Should().BeTrue();

        // Give it an inbound reference, so the sink must now clear rather than mark.
        await StoreAsync("orphan-remote-referrer", $"points at [[orphan-remote-clear-probe]]");

        await new MaintenanceEventSink().OnStoredAsync(
            "orphan-remote-clear-probe", [body], _remote, CancellationToken.None);

        var entry = _remote.LoadIndex("local").Single(e => e.Name == "orphan-remote-clear-probe");

        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            because: "the attribute clear must land remotely. Bundling it with the keyword removal " +
                     "made PATCH refuse both, so a non-orphan kept orphan:true forever and the " +
                     "sink retried and warned on every later store event");
        entry.Keywords.Should().Contain("orphan",
            because: "the legacy keyword genuinely cannot be removed over this wire — recorded so " +
                     "the two writes are not merged back together on the assumption both land");
    }

    /// <summary>
    /// <c>TryUpdateEntry</c> changed which permissions it needs, and that is a deployment-visible
    /// fact worth stating in the suite rather than leaving to be discovered. It used to read
    /// through <c>LoadIndex</c>, which needs <c>search</c>; it now uses
    /// <c>GET /memories/{name}/entry</c> (<c>read</c>) and <c>PATCH /memories/{name}</c>
    /// (<c>store</c>). The new requirements are the correct semantics — a metadata WRITE should
    /// need a write permission — and nothing depended on the old ones, because the whole remote
    /// metadata write was refused before this branch.
    ///
    /// <para>It <b>throws</b> rather than returning <c>false</c>, deliberately: a caller must not
    /// read "you are not allowed to do this" as "the entry was not there" or "the mutation was
    /// refused". That is the same distinction <c>LoadIndex</c> throws to preserve, and this method
    /// inherited the obligation when it took over that read.</para>
    /// </summary>
    [Theory]
    [InlineData("search", "store")]  // no `read` — the GET is refused
    [InlineData("read", "search")]   // no `store` — the PATCH is refused
    public async Task TryUpdateEntry_ThrowsOnAKeyMissingAPermissionItNeeds(params string[] permissions)
    {
        await StoreAsync("permission-probe", "probe body for permissions");
        using var narrow = NarrowKeyStore(permissions);

        var act = () => narrow.TryUpdateEntry("permission-probe", "local", e => e with
        {
            Attributes = new Dictionary<string, string[]>(
                e.Attributes ?? [], StringComparer.OrdinalIgnoreCase) { ["person"] = ["nobody"] },
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        act.Should().Throw<HttpRequestException>()
            .Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden,
                because: "a permission failure is not a refusal of the mutation and must not be " +
                         "reported as one — returning false here would make a misconfigured key " +
                         "look like a store that declined the write");
    }

    /// <summary>The positive half: a key carrying exactly <c>read</c> + <c>store</c> — and NOT
    /// <c>search</c>, which the replaced <c>LoadIndex</c> read required — completes the update.
    /// Without this, the theory above is satisfied by an implementation that throws for every key.
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_SucceedsWithReadAndStoreAlone()
    {
        await StoreAsync("permission-sufficient-probe", "probe body for sufficient permissions");
        using var narrow = NarrowKeyStore("read", "store");

        bool updated = narrow.TryUpdateEntry("permission-sufficient-probe", "local", e => e with
        {
            Attributes = new Dictionary<string, string[]>(
                e.Attributes ?? [], StringComparer.OrdinalIgnoreCase) { ["person"] = ["somebody"] },
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        updated.Should().BeTrue(
            because: "read + store is the complete set this member needs — `search` is no longer " +
                     "among them, and requiring it would be a permission this path does not use");
    }

    /// <summary>
    /// The refusal that survives, and it is a different one: <c>PATCH</c> rejects a body that
    /// CHANGES <c>Keywords</c> with a <c>400</c>, because keywords are derived from content and
    /// that endpoint has no content to derive against. The client must report that as "not
    /// persisted" rather than swallowing it — and must leave the entry untouched, which is the half
    /// a return-value-only assertion cannot see.
    /// <para>Without this, nothing on the client side distinguishes "the server accepted it" from
    /// "the server refused it and we returned true anyway".</para>
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_ReturnsFalse_WhenTheMutationChangesKeywords()
    {
        await StoreAsync("keyword-change-probe", "probe body for keyword changes", keywords: ["zebra"]);

        bool updated = _remote.TryUpdateEntry("keyword-change-probe", "local", entry =>
            entry with { Keywords = [.. entry.Keywords ?? [], "aardvark"] });

        updated.Should().BeFalse(
            because: "PATCH refuses a keyword change (it has no content to re-derive against), so " +
                     "the honest answer is 'not persisted' — that change must go through a store " +
                     "or an append");

        var reloaded = _remote.LoadIndex("local").Single(e => e.Name == "keyword-change-probe");
        reloaded.Keywords.Should().NotContain("aardvark",
            because: "a refused mutation must leave the remote entry exactly as it was, not " +
                     "partially applied");
    }

    /// <summary>A mutation that returns <c>null</c> means "nothing to change". It must not reach
    /// the wire at all: a PATCH here would bump <c>UpdatedAt</c> on an entry nobody edited, and
    /// would report <c>true</c> for a write that had no content.</summary>
    [Fact]
    public async Task TryUpdateEntry_ReturnsFalse_AndWritesNothing_WhenTheMutationDeclines()
    {
        await StoreAsync("decline-probe", "probe body for a declined mutation");
        var before = _remote.LoadIndex("local").Single(e => e.Name == "decline-probe");

        bool updated = _remote.TryUpdateEntry("decline-probe", "local", _ => null);

        updated.Should().BeFalse(because: "the caller declined to change anything");
        var after = _remote.LoadIndex("local").Single(e => e.Name == "decline-probe");
        after.UpdatedAt.Should().Be(before.UpdatedAt,
            because: "a declined mutation must not reach the server — a PATCH would stamp a new " +
                     "UpdatedAt on an entry nobody edited");
    }

    /// <summary>
    /// The pre-existing "not found" case must still behave exactly as before: LoadIndex correctly
    /// reports no such entry, and TryUpdateEntry returns false for the ordinary reason (nothing to
    /// mutate), not because of the fail-closed override above.
    /// </summary>
    [Fact]
    public void TryUpdateEntry_ReturnsFalse_WhenTheEntryDoesNotExist()
    {
        bool mutateRan = false;
        bool updated = _remote.TryUpdateEntry("does-not-exist", "local", entry =>
        {
            mutateRan = true;
            return entry;
        });

        updated.Should().BeFalse(because: "no such memory was ever stored");
        mutateRan.Should().BeFalse(
            because: "TryUpdateEntry must short-circuit on LoadIndex finding nothing, not invoke " +
                     "the mutation callback and then discard its result — that would make this " +
                     "case indistinguishable from the fail-closed one above for the wrong reason");
    }

    /// <summary>
    /// Reviewer-measured repro: seeding 205 entries over a Kestrel loopback and calling
    /// <c>LoadIndex("local")</c> returned 200, with "probe-0204" absent, because the proxy sent no
    /// <c>limit</c>/<c>offset</c> and the server's default list page size
    /// (<c>DefaultListLimit = 200</c>, <c>MemoryEndpoints.cs</c>) silently truncated it. That
    /// reproduces this task's own defect class above the pagination boundary: a scope this large
    /// would make <c>remember()</c>'s existing-entry lookup (<c>MemoryTools.Core.cs</c>) report
    /// "not found" for a memory that exists, skipping <c>ArchiveVersion</c> and resetting
    /// <c>CreatedAt</c> — silent data loss, just gated behind a size threshold instead of always.
    /// Seeded just over the boundary (205, not an arbitrarily larger number) to keep this fast.
    ///
    /// <para>Uses its own factory rather than the class's shared <see cref="_remote"/>: 205
    /// sequential writes plus the paged reads exceed the server's default rate limit (100
    /// requests/60s), which the fixture's other, much smaller tests don't need raised. Same
    /// override mechanism as <c>RateLimitTests.RateLimit_PartitionsByUser_NotSharedIp</c>.</para>
    /// </summary>
    /// <remarks>
    /// R-4: previously built via <c>ScriniaServerFactory.WithWebHostBuilder(b =>
    /// b.UseSetting("Scrinia:RateLimit:PermitLimit", ...))</c> + <c>UseKestrel(0)</c> — the exact
    /// shape measured (during PatchEndpointTests' M-1 investigation) to bind Kestrel's hard-coded
    /// default address (<c>127.0.0.1:5000</c>) instead of an ephemeral port, a pre-existing latent
    /// full-suite port collision this test happened not to trigger only because nothing else in the
    /// suite raced it onto the same port at the same moment. <see cref="PagingRateLimitServerFactory"/>
    /// is the same dedicated-<c>WebApplicationFactory{TEntryPoint}</c>-subclass fix
    /// <c>PatchEndpointTests.ReadOnlyClaimsServerFactory</c> uses; the assertion right after
    /// <c>StartServer()</c> below is the "verify by measurement" step — it fails if this ever
    /// regresses back to <c>:5000</c>.
    /// </remarks>
    [Fact]
    public async Task LoadIndex_ReturnsEveryEntry_AboveTheServersDefaultPageSize()
    {
        const int total = 205;

        using var factory = new PagingRateLimitServerFactory(total * 2);
        factory.UseKestrel(0);
        factory.StartServer();

        // Measured, not assumed: confirm this genuinely bound an ephemeral port rather than
        // falling back to Kestrel's hard-coded default, which is exactly what the WithWebHostBuilder
        // shape this replaced was silently doing.
        var addressesFeature = factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        addressesFeature.Should().NotBeNull();
        addressesFeature!.Addresses.Should().NotContain(a => a.EndsWith(":5000"),
            because: "a dedicated WebApplicationFactory subclass with UseKestrel(0) called directly " +
                     "on the instance (no WithWebHostBuilder in the chain) must bind an ephemeral " +
                     "port, not fall back to Kestrel's hard-coded default and collide with every " +
                     "other Kestrel-backed test in the suite");

        var client = factory.CreateAuthenticatedClient();
        // HttpMemoryStore owns client's disposal (see DisposeAsync above for the same pattern).
        using var remote = new HttpMemoryStore(client, factory.PrimaryStore);

        for (int i = 0; i < total; i++)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/v1/stores/{factory.PrimaryStore}/memories",
                new StoreRequest([$"pagination probe body {i}"], $"probe-{i:D4}", $"pagination probe body {i}"));
            resp.EnsureSuccessStatusCode();
        }

        var entries = remote.LoadIndex("local");

        entries.Should().HaveCount(total,
            because: "a scope with more entries than the server's default list page size (200) " +
                     "must still come back whole, not silently truncated at the first page");
        entries.Should().Contain(e => e.Name == "probe-0204",
            because: "the last entry seeded — the one a first-page-only read would have dropped " +
                     "— must be present");
    }

    /// <summary>
    /// A case-mismatched subject must still resolve to the one memory. The lookup moved from a
    /// client-side <c>LoadIndex</c> scan to the server's <c>GET /memories/{name}/entry</c>, so this
    /// now pins that the SERVER's resolution is case-insensitive too — a guarantee the client-side
    /// scan used to provide and which a new endpoint could silently have dropped. Falling out as
    /// "not found" would collapse two conditions the rest of this file exists to keep apart, and
    /// <c>mutateRan</c> is what tells them apart.
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_FindsTheEntry_WhenTheSubjectCasingDiffers()
    {
        await StoreAsync("MixedCaseProbe", "probe body for casing");

        bool mutateRan = false;
        bool updated = _remote.TryUpdateEntry("mixedcaseprobe", "local", entry =>
        {
            mutateRan = true;
            return entry;
        });

        mutateRan.Should().BeTrue(
            because: "the entry exists under a different casing and must be found — falling out at " +
                     "the not-found guard would report it absent, which is a different condition");
        updated.Should().BeTrue(
            because: "an unedited complete-entry round trip is a legal PATCH, so the persist half " +
                     "of the case-insensitive path is exercised too, not just the lookup");
    }

    /// <summary>
    /// The listing wire carried no keywords at all — <c>MemoryListItem</c> had no such field, so
    /// every remote <c>ArtifactEntry</c> came back with <c>Keywords</c> null regardless of what was
    /// stored, while the server held them in hand. <c>MaintenanceEventSink</c> reads that field in
    /// three places; at <c>:102</c> an empty read makes the orphan branch write
    /// <c>Keywords = ["orphan"]</c>, clobbering every other keyword. That is inert only because
    /// <c>HttpMemoryStore.TryUpdateEntry</c> refuses every write today — the E4 PATCH endpoint
    /// would arm it.
    /// </summary>
    [Fact]
    public async Task LoadIndex_CarriesKeywords()
    {
        await StoreAsync("keyword-probe", "probe body for keywords", keywords: ["zebra"]);

        var entry = _remote.LoadIndex("local").Single(e => e.Name == "keyword-probe");

        entry.Keywords.Should().NotBeNull(
            because: "the server holds this entry's keywords and the listing endpoint must project " +
                     "them — a null here is the wire dropping data the server has in hand");
        // Contain, not equality: MemoryOrchestrator merges agent keywords with auto-extracted ones,
        // so the stored set is a superset (measured: [body, keywords, probe, zebra]).
        entry.Keywords!.Should().Contain("zebra");
    }

    // ── The write surface: raw input in, SERVER-derived entry out ───────────────

    /// <summary>
    /// The task's thesis, end to end: the client hands the server a raw <see cref="MemoryWrite"/>
    /// and gets back the entry the SERVER derived — no <c>TextAnalysis</c> call, no assembled
    /// subset, no second round trip.
    /// <para>The assertions are chosen to be unsatisfiable by anything the client could have made
    /// up. <c>TermFrequencies</c> and <c>ChunkEntries</c> are pure derivation output — the request
    /// carries neither, and <see cref="MemoryWrite"/> has no field through which they could be
    /// supplied — so their presence proves the entry came from the store's pipeline. The keyword
    /// set proves the merge happened server-side: the caller sent one word and the entry comes back
    /// carrying it ALONGSIDE terms extracted from the content, which is something only the
    /// derivation pass produces.</para>
    /// </summary>
    [Fact]
    public async Task StoreAsync_ReturnsTheEntryTheServerDerived()
    {
        var entry = await _remote.StoreAsync(new MemoryWrite(
            "write-surface-probe", "local",
            ["widgets and gizmos in the first chunk", "more widgets in the second"],
            Description: "a curated description",
            Attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] },
            Keywords: ["zebra"]));

        entry.Name.Should().Be("write-surface-probe");
        entry.ChunkCount.Should().Be(2,
            because: "the wire carries the chunk array, so a two-element write is two chunks — it " +
                     "used to be joined into one on the way out");
        entry.ContentPreview.Should().Be("a curated description");
        entry.Attributes.Should().NotBeNull().And.ContainKey("person");

        entry.Keywords.Should().NotBeNull().And.Contain("zebra",
            because: "the caller's raw keyword survives the server's merge");
        entry.Keywords!.Should().Contain("widgets",
            because: "and the merge itself happened server-side — a term extracted from the CONTENT " +
                     "can only have come from the store's derivation pass, which is the whole claim");

        entry.TermFrequencies.Should().NotBeNull(
            because: "MemoryWrite has no field through which term frequencies could be supplied, so " +
                     "a populated map here is proof the entry was derived by the store and carried " +
                     "back whole");
        entry.ChunkEntries.Should().NotBeNull().And.HaveCount(2,
            because: "per-chunk derivation output, likewise unsuppliable by the caller");

        // Read back independently: the returned entry must describe a memory that genuinely exists,
        // not merely a well-formed response.
        _remote.LoadIndex("local").Should().Contain(e => e.Name == "write-surface-probe");
    }

    /// <summary>
    /// <c>AppendRequest</c> used to be <c>record AppendRequest(string Content)</c> — no description,
    /// no attributes, no keywords — so an append made over the wire silently dropped all three
    /// while the identical local call kept them (D-18). This pins the widened shape end to end: the
    /// values must reach the server's derivation and come back on the entry.
    /// </summary>
    [Fact]
    public async Task AppendAsync_CarriesTheDescriptionAttributesAndKeywordsToTheServer()
    {
        await _remote.StoreAsync(new MemoryWrite(
            "append-surface-probe", "local", ["the original body"]));

        var entry = await _remote.AppendAsync(new MemoryWrite(
            "append-surface-probe", "local", ["the appended body"],
            Description: "a description supplied at append time",
            Attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] },
            Keywords: ["zebra"]));

        entry.ChunkCount.Should().Be(2, because: "the append added a chunk rather than replacing");
        entry.UpdatedAt.Should().NotBeNull(
            because: "an append to an existing memory sets it — null would mean this silently " +
                     "became a create");
        entry.ContentPreview.Should().Be("a description supplied at append time");
        entry.Attributes.Should().NotBeNull().And.ContainKey("person");
        entry.Keywords.Should().NotBeNull().And.Contain("zebra");
    }

    /// <summary>The contract <see cref="IMemoryStore.AppendAsync"/> states and
    /// <c>FileMemoryStore</c> enforces: exactly one chunk, refused rather than merged, because the
    /// derivation pipeline reads only the last element and everything between the first and the
    /// last would vanish. The wire's scalar <c>Content</c> field cannot express a second chunk at
    /// all, but callers program against the interface, so the remote implementation has to refuse
    /// the same input the local one does — otherwise the two stores disagree about what is legal.
    /// </summary>
    [Fact]
    public async Task AppendAsync_RefusesMoreThanOneChunk()
    {
        var refuse = async () => await _remote.AppendAsync(
            new MemoryWrite("append-refusal-probe", "local", ["one", "two"]));

        await refuse.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// The three fields the listing wire carries and the client used to discard, asserted against a
    /// REAL server rather than a stub: this is what proves the server actually populates them, which
    /// <c>Scrinia.Tests.HttpMemoryStoreDerivedFieldsTests</c> can only assume.
    /// <para><c>FileHashes</c> is deliberately absent from these assertions — provoking it needs a
    /// real file under a real workspace root for the server's hasher to resolve, which this fixture
    /// has no business planting. The client's side of that field is pinned in the stub-driven test
    /// named above.</para>
    /// </summary>
    [Fact]
    public async Task LoadIndex_CarriesTermFrequenciesAndChunkEntries()
    {
        await StoreChunksAsync("derived-probe", ["widgets in the first chunk", "gizmos in the second"]);

        var entry = _remote.LoadIndex("local").Single(e => e.Name == "derived-probe");

        entry.TermFrequencies.Should().NotBeNull(
            because: "the server holds this entry's term frequencies and the listing endpoint " +
                     "projects them — a null here is the client throwing away what it was sent");
        entry.ChunkEntries.Should().NotBeNull().And.HaveCount(2,
            because: "two chunks were stored, and their per-chunk metadata crosses the wire too");
    }

    /// <summary>
    /// <c>ListScoped</c> is a second, separate construction site for the fix above — not a
    /// redundant sibling of <see cref="LoadIndex_CarriesKeywords"/>, which only exercises
    /// <c>LoadIndex</c>'s own <c>ArtifactEntry</c> build. This one was unguarded: nothing pinned
    /// that <c>ListScoped</c> threads <c>item.Keywords</c> into the <c>ArtifactEntry</c> it
    /// constructs, so dropping the `Keywords:` argument there would have passed every existing
    /// test. <c>MaintenanceEventSink.cs:95</c> reads <c>sa.Entry.Keywords</c> through exactly this
    /// path for inbound-ref detection, so this is real production logic, not a formality.
    /// </summary>
    [Fact]
    public async Task ListScoped_CarriesKeywords()
    {
        await StoreAsync("keyword-probe-scoped", "probe body for scoped keywords", keywords: ["zebra"]);

        var scoped = _remote.ListScoped("local").Single(sa => sa.Entry.Name == "keyword-probe-scoped");

        scoped.Entry.Keywords.Should().NotBeNull(
            because: "ListScoped builds its own ArtifactEntry independently of LoadIndex, and must " +
                     "also thread the server's keywords through rather than dropping them");
        // Contain, not equality — same reasoning as LoadIndex_CarriesKeywords: MemoryOrchestrator
        // merges agent keywords with auto-extracted ones, so the stored set is a superset.
        scoped.Entry.Keywords!.Should().Contain("zebra");
    }
}

/// <summary>A self-deleting temp directory for a local <c>FileMemoryStore</c> used as the
/// comparison side of a local/remote parity assertion.</summary>
internal sealed class TempStoreDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "scrinia-parity-" + Guid.NewGuid().ToString("N")[..8]);

    public TempStoreDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
    }
}

/// <summary>
/// A store with a raised rate limit, provisioned via its own <c>ConfigureWebHost</c> override
/// rather than <c>ScriniaServerFactory.WithWebHostBuilder</c> (R-4: <c>WithWebHostBuilder</c> itself
/// — not any extra service registration riding along with it — is what makes <c>UseKestrel(0)</c>
/// bind Kestrel's hard-coded default address instead of an ephemeral port; measured, not assumed —
/// see the assertion in <see cref="RemoteLoadIndexTests.LoadIndex_ReturnsEveryEntry_AboveTheServersDefaultPageSize"/>).
/// Mirrors <c>ThrowingStoreServerFactory</c> in <c>ApiVersionHeaderOn500Tests.cs</c> and
/// <c>PatchEndpointTests.ReadOnlyClaimsServerFactory</c>.
/// </summary>
public sealed class PagingRateLimitServerFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    private readonly string _tempDir;
    public string PrimaryStore { get; } = "test-store";
    private string TestApiKey { get; set; } = "";

    public PagingRateLimitServerFactory(int permitLimit)
    {
        _permitLimit = permitLimit;
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinium-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    private readonly int _permitLimit;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Scrinia:DataDir", _tempDir);
        builder.UseSetting($"Scrinia:Stores:{PrimaryStore}", Path.Combine(_tempDir, "stores", PrimaryStore));
        builder.UseSetting("Scrinia:RateLimit:PermitLimit", _permitLimit.ToString());

        builder.ConfigureServices(services =>
        {
            var sp = services.BuildServiceProvider();
            var keyStore = sp.GetRequiredService<ApiKeyStore>();
            var (rawKey, _, _) = keyStore.CreateKey(
                "pagination-test-user", [PrimaryStore], ["search", "store"], "pagination-test");
            TestApiKey = rawKey;
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
