using System.Net;
using System.Text;
using FluentAssertions;
using Scrinia;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;
using Scrinia.Mcp;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Pins MCP tool behaviour when the ambient store is <b>remote</b>.
/// <para>Before this fixture existed, no test anywhere installed an <see cref="HttpMemoryStore"/>
/// as <see cref="MemoryStoreContext.Current"/> and called a tool. That gap is why the
/// ILocalMemoryStore split could hoist a <see cref="NotSupportedException"/> to the top of
/// <c>guide()</c>, <c>restore()</c>, ephemeral <c>remember()</c> and every <c>list()</c> mode
/// while 1707 tests stayed green.</para>
/// <para>The contract these tests encode: a tool may only fail remotely if it <i>actually
/// reaches</i> a local-filesystem member. Validation errors, early returns, and best-effort
/// sections wrapped in a swallowing <c>catch</c> must behave remotely exactly as they did before
/// the split.</para>
/// </summary>
public sealed class RemoteStoreToolTests
{
    private const string EmptyListJson = """{"memories":[],"total":0}""";

    private const string OneEntryListJson = """
        {"memories":[{"name":"alpha","qualifiedName":"local:alpha","scope":"local",
        "chunkCount":1,"originalBytes":128,"createdAt":"2026-01-01T00:00:00+00:00",
        "updatedAt":null,"description":"an entry","attributes":null}],"total":1}
        """;

    /// <summary>
    /// Serves the endpoints these tools reach remotely: <c>GET .../memories</c> (used by
    /// <see cref="HttpMemoryStore.ListScoped(string?)"/>), <c>POST .../memories</c> (used by
    /// <see cref="HttpMemoryStore.WriteArtifactAsync"/>, which throws
    /// <see cref="HttpRequestException"/> on any non-success status), and the metadata pair
    /// <c>GET .../memories/{name}/entry</c> + <c>PATCH .../memories/{name}</c> that
    /// <see cref="HttpMemoryStore.TryUpdateEntry"/> uses. Everything else 404s, which is
    /// deliberate: if a tool starts making an unexpected call, the test should notice.
    ///
    /// <para>The single-entry GET is served BY SELECTING FROM <paramref name="listJson"/> rather
    /// than from a second literal, so a fixture that says "this store holds one entry called alpha"
    /// cannot end up saying something different depending on which endpoint is asked.</para>
    ///
    /// <para><paramref name="patchStatus"/> is what the real server would answer for the mutation
    /// under test: <c>200</c> when it accepts the metadata write, <c>400</c> when it refuses one
    /// (a body that CHANGES <c>Keywords</c> earns that, because PATCH has no content to re-derive
    /// them from). It is a parameter rather than something inferred from the body because inferring
    /// it means re-implementing the endpoint's rules here; the rules themselves are pinned against
    /// a real server in <c>Scrinia.Server.Tests.RemoteLoadIndexTests</c>.</para>
    /// </summary>
    private sealed class StubBackendHandler(
        string listJson, HttpStatusCode patchStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        /// <summary>Every request that reached the wire, as "METHOD path". Lets a test assert that
        /// an operation made NO network call — which no status-code fixture can show, because a
        /// call that should not have happened still gets a plausible answer here.</summary>
        public List<string> Requests { get; } = [];

        // Both overrides are required: HttpMemoryStore.ListScoped goes through SendSync, which
        // calls the synchronous HttpClient.Send. A handler that only overrides SendAsync throws
        // NotSupportedException from inside HttpMessageHandler — which would have looked exactly
        // like the regression under test.
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));

        /// <summary>The entry named by a <c>.../memories/{name}/entry</c> path, as raw JSON, or
        /// null when the listing holds no such entry. Case-insensitive, matching every
        /// name lookup in this codebase.</summary>
        private string? FindEntryJson(string absolutePath)
        {
            const string suffix = "/entry";
            int lastSlash = absolutePath.LastIndexOf('/', absolutePath.Length - suffix.Length - 1);
            string name = Uri.UnescapeDataString(
                absolutePath[(lastSlash + 1)..^suffix.Length]);

            using var doc = System.Text.Json.JsonDocument.Parse(listJson);
            foreach (var m in doc.RootElement.GetProperty("memories").EnumerateArray())
            {
                string? qualified = m.GetProperty("qualifiedName").GetString();
                string? bare = m.GetProperty("name").GetString();
                if (string.Equals(qualified, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(bare, name, StringComparison.OrdinalIgnoreCase))
                    return m.GetRawText();
            }
            return null;
        }

        /// <summary>The <c>StoreResponse</c> a POST to <c>.../memories</c> answers with, echoing the
        /// posted name and chunk count back. Derived fields are all null: this stub is a transport,
        /// not a second derivation pipeline, and no test in this file reads them. What it must get
        /// right is the SHAPE — an <c>entry</c> object is present, because the client refuses a
        /// write response without one.</summary>
        private static string StoredEntryJson(HttpRequestMessage request)
        {
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            string name = doc.RootElement.GetProperty("name").GetString() ?? "";
            var chunks = doc.RootElement.GetProperty("content").EnumerateArray().ToList();
            long bytes = chunks.Sum(c => (long)Encoding.UTF8.GetByteCount(c.GetString() ?? ""));
            string quoted = System.Text.Json.JsonSerializer.Serialize(name);

            return $$"""
                {"entry":{"name":{{quoted}},"qualifiedName":{{quoted}},"scope":"local",
                "chunkCount":{{chunks.Count}},"originalBytes":{{bytes}},
                "createdAt":"2026-01-01T00:00:00+00:00","updatedAt":null,"description":"",
                "attributes":null,"keywords":null,"termFrequencies":null,"chunkEntries":null,
                "fileHashes":null},"message":"Remembered"}
                """;
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            bool isMemoriesCollection = path.EndsWith("/memories", StringComparison.Ordinal);

            HttpResponseMessage response;
            if (request.Method == HttpMethod.Get && isMemoriesCollection)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(listJson, Encoding.UTF8, "application/json"),
                };
            }
            // A persistent remote remember() writes through here. The body is NOT ignored: a write
            // answers with the complete entry the server derived, and HttpMemoryStore.StoreAsync
            // refuses a 2xx that carries none rather than completing it on the client side — which
            // would mean deriving. A "{}" body was enough while the tools wrote through
            // WriteArtifactAsync, which read only the status; it is not enough now, and the shape
            // below is the one Scrinia.Server.Models.StoreResponse really sends (pinned byte-for-
            // byte in Scrinia.Server.Tests.ApiVersionAssertionTests).
            else if (request.Method == HttpMethod.Post && isMemoriesCollection)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(StoredEntryJson(request), Encoding.UTF8, "application/json"),
                };
            }
            else if (request.Method == HttpMethod.Get && path.EndsWith("/entry", StringComparison.Ordinal))
            {
                string? entry = FindEntryJson(path);
                response = entry is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(entry, Encoding.UTF8, "application/json"),
                    };
            }
            else if (request.Method == HttpMethod.Patch)
            {
                response = new HttpResponseMessage(patchStatus);
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // Stamped with a matching API version header (remote-write-correctness Task 7) on
            // every response, including the 404 fallback above — HttpMemoryStore now fails closed
            // on any response missing this header, and this fixture is about tool behaviour over
            // an already-recognised remote store, not about the version check itself.
            response.Headers.TryAddWithoutValidation(RemoteApiVersion.HeaderName, RemoteApiVersion.Current);
            return response;
        }
    }

    /// <summary>
    /// Installs an <see cref="HttpMemoryStore"/> as the ambient store — the remote-mode analogue
    /// of <see cref="TestHelpers.StoreScope"/>. Clears the sink context for the same reason
    /// StoreScope does (process-wide static that leaks across tests).
    /// </summary>
    private sealed class RemoteStoreScope : IDisposable
    {
        private readonly IMemoryStore? _previous;
        public HttpMemoryStore Store { get; }

        /// <summary>The backend the store talks to, exposed so a test can assert what did — or did
        /// not — reach it.</summary>
        public StubBackendHandler Handler { get; }

        public RemoteStoreScope(string listJson = EmptyListJson, HttpStatusCode patchStatus = HttpStatusCode.OK)
        {
            _previous = MemoryStoreContext.Current;
            MemoryEventSinkContext.Default = null;
            Handler = new StubBackendHandler(listJson, patchStatus);
            var http = new HttpClient(Handler)
            {
                BaseAddress = new Uri("http://localhost:5000"),
            };
            // MaxRetries: 0 — no backoff sleeps; nothing here exercises the retry path.
            Store = new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));
            MemoryStoreContext.Current = Store;
        }

        public void Dispose()
        {
            MemoryEventSinkContext.Default = null;
            MemoryStoreContext.Current = _previous;
            Store.Dispose();
        }
    }

    /// <summary>
    /// A non-local store that records the <see cref="MemoryWrite"/>s handed to it. Subclasses
    /// <see cref="FakeMemoryStore"/> and overrides one member, rather than hand-writing 23.
    /// <para>It used to record <c>Upsert</c>, because the tools built the entry themselves and
    /// handed it over. They hand over the raw write now, so the write is the seam — and it is the
    /// better one for this test: it shows exactly what the tool asked the store to persist, with no
    /// derivation in between to attribute a value to.</para>
    /// </summary>
    private sealed class RecordingRemoteStore : FakeMemoryStore
    {
        public List<MemoryWrite> Writes { get; } = [];

        public override Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default)
        {
            Writes.Add(write);
            return base.StoreAsync(write, ct);
        }
    }

    /// <summary>
    /// A non-local store whose listing carries drift hashes.
    /// <para><see cref="HttpMemoryStore.ListScoped(string?)"/> never populates
    /// <c>FileHashes</c>, so the CLI's remote path cannot reach the drift column in
    /// <c>BuildFullList</c>. The server can: <c>Scrinia.Server/Program.cs</c> installs
    /// <c>MemoryStoreContext.Current = sm.GetStore(storeName)</c>, and <c>StoreManager.GetStore</c>
    /// returns whatever <c>IStorageBackend.CreateStore</c> produced — a documented plugin seam
    /// (<c>{dataDir}/plugins/*.dll</c>) with no requirement that the store be an
    /// <see cref="ILocalMemoryStore"/>. A backend that populates <c>FileHashes</c> and is not
    /// local is therefore constructible; <c>BundleEndpointTests.FakeNonLocalBackend</c> already
    /// builds one.</para>
    /// </summary>
    private sealed class HashCarryingNonLocalStore : FakeMemoryStore
    {
        public override List<ScopedArtifact> ListScoped(string? scopes = null) =>
        [
            new ScopedArtifact("local", new ArtifactEntry(
                Name: "hashed",
                Uri: "plugin://default/local/hashed",
                OriginalBytes: 128,
                ChunkCount: 1,
                CreatedAt: DateTimeOffset.UnixEpoch,
                ContentPreview: "an entry that references a file",
                FileHashes: new Dictionary<string, string> { ["src/Program.cs"] = "deadbeef" })),
        ];
    }

    /// <summary>Installs an arbitrary <see cref="IMemoryStore"/> double as the ambient store.</summary>
    private sealed class DoubleStoreScope : IDisposable
    {
        private readonly IMemoryStore? _previous;
        public IMemoryStore Store { get; }

        public DoubleStoreScope(IMemoryStore store)
        {
            _previous = MemoryStoreContext.Current;
            MemoryEventSinkContext.Default = null;
            Store = store;
            MemoryStoreContext.Current = store;
        }

        public void Dispose()
        {
            MemoryEventSinkContext.Default = null;
            MemoryStoreContext.Current = _previous;
        }
    }

    private static ScriniaMcpTools Tools() => new();

    // ── C1: guide() and restore() ────────────────────────────────────────────
    //
    // BuildRestoreContextAsync reads WorkspaceRoot inside three try/catch blocks whose catches
    // swallow. Remotely the store threw *into* those catches, so the method returned a valid
    // context minus the conflict-scan / skills / agent sections. It must keep doing that.

    [Fact]
    public async Task Guide_RemoteStore_ReturnsGuideInsteadOfThrowing()
    {
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().Guide();

        yaml.Should().NotBeNullOrWhiteSpace(
            because: "guide() is the first tool an agent calls every session; it must work remotely");
    }

    [Fact]
    public async Task Restore_RemoteStore_ReturnsContextInsteadOfThrowing()
    {
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().MemoryRestore();

        yaml.Should().Contain("restored",
            because: "the local-only sections are skipped remotely, but restore still succeeds");
    }

    // ── I2: ephemeral remember() ─────────────────────────────────────────────
    //
    // HttpMemoryStore implements ephemeral storage for real (in-process dictionary). The
    // ephemeral branch returns before the method ever reads WorkspaceRoot.

    [Fact]
    public async Task Remember_EphemeralPath_RemoteStore_Succeeds()
    {
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().MemoryRemember(path: "/temp/scratch", content: ["ephemeral note"]);

        yaml.Should().Contain("ephemeral",
            because: "a remote store implements ephemeral storage for real, so /temp/ writes work");
        scope.Store.GetEphemeral("scratch").Should().NotBeNull();
    }

    /// <summary>
    /// The half the assertion above cannot make, and the one that matters: the memory can be READ
    /// BACK.
    ///
    /// <para>In this store a <c>~name</c> addresses THIS PROCESS. Every ephemeral read here —
    /// <c>GetEphemeral</c>, the listing, and the short-circuit at the top of
    /// <c>ResolveArtifactAsync</c> — is answered from the in-process map without asking the server,
    /// so an ephemeral write that went over the wire would land in the SERVER's ephemeral map and
    /// be unreadable from this side forever: <c>remember</c> reporting success and <c>recall</c>
    /// answering "not found", with no error anywhere in between. That hazard was recorded as latent
    /// when <c>StoreAsync</c>/<c>AppendAsync</c> landed, because every ephemeral path called
    /// <c>RememberEphemeral</c> directly and never reached a write member. Repointing the MCP tools
    /// onto the write surface is what made it reachable, so this is the guard that goes with it:
    /// <c>HttpMemoryStore</c> serves an ephemeral write in process, through its local helper store.
    ///
    /// <para>Written as a round trip through the TOOLS rather than as an assertion about which
    /// store member ran, because "readable afterwards" is the actual guarantee — a future change
    /// that moves ephemeral storage server-side legitimately may, as long as reads follow it.</para>
    /// </summary>
    [Fact]
    public async Task Remember_EphemeralPath_RemoteStore_IsReadableAfterwards()
    {
        using var scope = new RemoteStoreScope();

        await Tools().MemoryRemember(path: "/temp/scratch", content: ["an ephemeral note"]);
        string recalled = await Tools().MemoryRecall(path: "~scratch");

        recalled.Should().Contain("an ephemeral note",
            because: "a write this store cannot serve a read for is silent data loss, whatever it " +
                     "reported at the time");
    }

    /// <summary>The same guarantee for the other write member: an ephemeral append reads back with
    /// BOTH chunks. It also pins that the append found the existing memory at all — an append that
    /// reached the server would find nothing there and create a fresh one-chunk memory.</summary>
    [Fact]
    public async Task Append_EphemeralPath_RemoteStore_IsReadableAfterwards()
    {
        using var scope = new RemoteStoreScope();

        await Tools().MemoryRemember(path: "/temp/scratch", content: ["the first line"]);
        string appended = await Tools().MemoryAppend(path: "~scratch", content: "the second line");
        appended.Should().Contain("Appended chunk 2",
            because: "the memory the append is adding to has to be the one just written");

        string recalled = await Tools().MemoryRecall(path: "~scratch");
        recalled.Should().Contain("the first line").And.Contain("the second line");
    }

    /// <summary>An ephemeral write must not reach the network at all. Complements the two round
    /// trips above: they would still pass if the write were POSTed AND mirrored locally, which
    /// would put the memory in two places and leak scratch content to the server.</summary>
    [Fact]
    public async Task Remember_EphemeralPath_RemoteStore_SendsNothingOverTheWire()
    {
        using var scope = new RemoteStoreScope();

        await Tools().MemoryRemember(path: "/temp/scratch", content: ["an ephemeral note"]);

        scope.Handler.Requests.Should().BeEmpty(
            because: "a ~name addresses this process; sending it to the server both leaks it and " +
                     "puts it where no read on this side will look");
    }

    // ── I4: list() in every mode ─────────────────────────────────────────────
    //
    // Only the drift branch is unconditionally local. Summary mode, the empty-store early
    // return, and full mode over entries with no file attributes never touch a layout member.

    [Fact]
    public async Task List_EmptyRemoteStore_ReturnsNoMemories()
    {
        using var scope = new RemoteStoreScope(EmptyListJson);

        string yaml = await Tools().MemoryList();

        yaml.Should().Contain("No memories stored.");
    }

    [Fact]
    public async Task List_SummaryMode_RemoteStore_Succeeds()
    {
        using var scope = new RemoteStoreScope(OneEntryListJson);

        string yaml = await Tools().MemoryList(mode: "summary");

        yaml.Should().Contain("listed");
    }

    [Fact]
    public async Task List_FullMode_RemoteStore_Succeeds()
    {
        using var scope = new RemoteStoreScope(OneEntryListJson);

        string yaml = await Tools().MemoryList(mode: "full");

        yaml.Should().Contain("alpha",
            because: "full mode only needs WorkspaceRoot when an entry carries file attributes");
    }

    [Fact]
    public async Task List_FullMode_NonLocalStoreWithFileHashes_OmitsDriftColumn()
    {
        // The last local-only site inside a tool that otherwise works remotely. `list(mode:'full')`
        // renders a [drift] marker for entries carrying FileHashes, which needs the workspace root.
        // A non-local store has none — so the page comes back without the marker, exactly as it
        // does for a remote store today, rather than a raw NotSupportedException escaping the tool.
        //
        // Driven through a double rather than the HttpMemoryStore fixture because HttpMemoryStore
        // never populates FileHashes; the reachable caller is an IStorageBackend plugin store
        // installed ambiently by the server. See HashCarryingNonLocalStore.
        using var scope = new DoubleStoreScope(new HashCarryingNonLocalStore());

        string yaml = await Tools().MemoryList(mode: "full");

        yaml.Should().Contain("hashed", because: "the listing itself needs no local filesystem");
        yaml.Should().NotContain("[drift]",
            because: "with no workspace root there is nothing to re-hash against, so the column " +
                     "is omitted rather than guessed at");
        yaml.Should().NotContain(ErrorCodes.NotSupported,
            because: "a missing drift column is a degraded success, not a refused capability");
    }

    [Fact]
    public async Task List_DriftMode_RemoteStore_ReturnsNotSupported()
    {
        // Drift detection resolves file paths against the workspace root unconditionally, so it
        // genuinely cannot run remotely. That is a reason to answer, not to throw: this replaces
        // the earlier pin that asserted a NotSupportedException escaped to the caller.
        using var scope = new RemoteStoreScope(OneEntryListJson);

        string yaml = await Tools().MemoryList(mode: "drift");

        yaml.Should().Contain(ErrorCodes.NotSupported);
        yaml.Should().Contain("mode='drift'",
            because: "the refusal must name the plain listing as the way forward");
    }

    // ── I3: validation errors must stay structured errors ────────────────────

    [Fact]
    public async Task Export_NoTopics_RemoteStore_ReturnsStructuredError()
    {
        using var scope = new RemoteStoreScope();

        var response = await Tools().Export(topics: []);

        response.ToYaml().Should().Contain("At least one topic name is required.",
            because: "argument validation runs before the store is touched, remote or not");
    }

    [Fact]
    public async Task SkillCreate_InvalidName_RemoteStore_ReturnsStructuredError()
    {
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().MemoryRemember(path: "/skill/bad name", content: ["instructions"]);

        yaml.Should().Contain("Invalid skill name",
            because: "name validation runs before the store is touched, remote or not");
    }

    // ── Task 3: local-only tools answer NOT_SUPPORTED instead of throwing ────
    //
    // Each of these entry points needs the workspace filesystem for its whole body, so there is
    // no degraded result to return. What there is, is a caller who deserves a structured refusal
    // and a recovery action rather than an exception surfacing as an MCP transport failure.

    [Fact]
    public async Task Export_RemoteStore_ReturnsNotSupported()
    {
        using var scope = new RemoteStoreScope();

        var response = await Tools().Export(topics: ["api"]);

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.NotSupported);
        response.ActionNeeded.Should().NotBeNullOrEmpty(
            because: "a refusal without a next step leaves the caller stuck");
    }

    [Fact]
    public async Task Import_RemoteStore_ReturnsNotSupported()
    {
        using var scope = new RemoteStoreScope();

        var response = await Tools().Import(bundlePath: "exports/anything.scrinia-bundle");

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.NotSupported);
    }

    [Fact]
    public async Task Reconcile_RemoteStore_ReturnsNotSupported()
    {
        using var scope = new RemoteStoreScope();

        var response = await Tools().Reconcile();

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.NotSupported);
    }

    [Fact]
    public async Task SkillCreate_RemoteStore_ReturnsNotSupported()
    {
        // remember() on a /skill/ path routes to SkillCreate, which writes .scrinia/skills/*.md.
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().MemoryRemember(path: "/skill/api-reviewer", content: ["instructions"]);

        yaml.Should().Contain(ErrorCodes.NotSupported);
    }

    [Fact]
    public async Task SkillLoad_RemoteStore_ReturnsNotSupported()
    {
        // recall() on a /skill/ path routes to SkillLoad, which enumerates .scrinia/skills/.
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().MemoryRecall(path: "/skill/");

        yaml.Should().Contain(ErrorCodes.NotSupported);
    }

    [Fact]
    public async Task Remember_AgentPath_RemoteStore_ReturnsNotSupported()
    {
        // The /agent/ branch writes a .md file plus a JSON sidecar into the workspace.
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().MemoryRemember(path: "/agent/profile", content: ["agent config"]);

        yaml.Should().Contain(ErrorCodes.NotSupported);
    }

    // ── Task 3: persistent remember() no longer needs a workspace root ───────

    [Fact]
    public async Task Remember_PersistentPath_NoFileRefs_RemoteStore_Succeeds()
    {
        // A remember() with no `file` attribute has nothing to hash, so it needs no workspace
        // root at all. Before the fix, the root was read unconditionally and every non-ephemeral
        // remote remember() threw — the single largest hole in remote mode.
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().MemoryRemember(path: "/api/note", content: ["a note with no file references"]);

        yaml.Should().Contain("status: success");
        yaml.Should().NotContain(ErrorCodes.NotSupported);
    }

    [Fact]
    public async Task Remember_PersistentPath_WithFileRefs_RemoteStore_StoresWithoutHashes()
    {
        // With `file` refs there IS something to hash, and the tool has no disk to hash it from.
        // Degrade rather than refuse: the memory is stored, the reference survives, and no hash is
        // fabricated on the way.
        //
        // WHAT CHANGED, AND WHY THIS IS NOW THE WEAKER HALF OF THE GUARANTEE. Hashing moved into
        // the store (D-04): whichever store holds the workspace supplies the hasher, so a remote
        // write finally gets a real drift baseline computed by the SERVER instead of none at all,
        // and the tool cannot compute one either way. MemoryWrite has no field a hash could travel
        // in — asserted directly below — so a regression that resolves a fabricated workspace root
        // out here has nothing to put the result in. That is a structural guarantee where this used
        // to be a behavioural one.
        //
        // The probe files stay: they are what a fabricated-root regression would hash, and the
        // assertions below would still be reached if MemoryWrite ever grew a place to put it.
        //
        // Probe files sit at CWD (`?? Directory.GetCurrentDirectory()`) and
        // `AppContext.BaseDirectory` (the executable's own directory, a common "give me *some*
        // path" fallback) — the two roots a regression is plausible enough to fabricate. Under
        // VSTest these are the same directory, so the second write lands on top of the first and
        // the pair adds no discriminating power beyond either alone. The test still catches a
        // regression that resolves to that directory, which is what makes it a real guard — it
        // just does not additionally prove anything about CWD vs. BaseDirectory as distinct
        // roots. A regression that falls back to an arbitrary hardcoded literal path (neither of
        // the above) is NOT covered — there is no finite set of locations that discriminates
        // every possible literal, so widening stops here; see Task 1 report for why this is the
        // accepted limit.
        using var scope = new DoubleStoreScope(new RecordingRemoteStore());
        string probeName = $"drift-probe-{Guid.NewGuid():N}.txt";
        string probePathCwd = Path.Combine(Directory.GetCurrentDirectory(), probeName);
        string probePathBaseDir = Path.Combine(AppContext.BaseDirectory, probeName);
        await File.WriteAllTextAsync(probePathCwd, "contents that would hash to something");
        await File.WriteAllTextAsync(probePathBaseDir, "contents that would hash to something");

        try
        {
            string yaml = await Tools().MemoryRemember(
                path: "/api/hashed",
                content: ["a note that references a file"],
                attributes: new Dictionary<string, string[]> { ["file"] = [probeName] });

            yaml.Should().Contain("status: success");

            var writes = ((RecordingRemoteStore)scope.Store).Writes;
            writes.Should().ContainSingle(because: "the memory must still be persisted");
            writes[0].Attributes.Should().NotBeNull().And.ContainKey("file",
                because: "the reference itself is the user's data and must survive; only the " +
                         "derived hashes are the store's to compute");

            typeof(MemoryWrite).GetProperties().Select(p => p.Name)
                .Should().NotContain(n => n.Contains("Hash", StringComparison.OrdinalIgnoreCase),
                    because: "the write surface carries raw input only — a caller that could hand " +
                             "over a hash could hand over one it fabricated from a guessed root, " +
                             "which is the defect this test was written against");
        }
        finally
        {
            File.Delete(probePathCwd);
            File.Delete(probePathBaseDir);
        }
    }

    // ── Final review, Critical 1: UpdateMeta must not report an unpersisted write ──

    /// <summary>
    /// <c>UpdateMeta</c> — reached by <c>update()</c> and by both halves of <c>link()</c> — used to
    /// go LoadIndex → mutate → <c>Upsert</c> → <c>ResponseBuilder.Success</c>. Remotely
    /// <c>Upsert</c> is a deliberate no-op, so once <c>LoadIndex</c> was fixed to return the real
    /// listing, the entry was found, mutated, dropped, and reported as updated. It routes through
    /// <c>TryUpdateEntry</c> now, which really does write — over
    /// <c>GET .../entry</c> + <c>PATCH</c> — so the success it reports is one it can back up.
    /// <para>The list JSON here makes the entry EXIST, so a NOT_FOUND answer would be wrong for a
    /// different reason than a fabricated success would be.</para>
    /// </summary>
    [Fact]
    public async Task UpdateMeta_RemoteStore_ReportsSuccess_WhenTheServerAcceptsTheWrite()
    {
        using var scope = new RemoteStoreScope(OneEntryListJson);

        var response = await Tools().UpdateMeta("alpha", keywords: ["person:maria"]);

        response.Status.Should().Be("success",
            because: "the PATCH channel accepted it — refusing here would be the mirror image of " +
                     "the fabricated success this test was originally written against");
    }

    /// <summary>
    /// The refusal that survives the channel landing, and why it must not be swallowed: a mutation
    /// that CHANGES <c>Keywords</c> is rejected by <c>PATCH</c> with a <c>400</c> (keywords are
    /// derived from content, which that endpoint does not have). The entry EXISTS, so the answer
    /// must be NOT_SUPPORTED — a missing capability, not a missing memory — and it must carry a
    /// next step, because "re-store it instead" is not guessable.
    /// <para>The stub is told to answer <c>400</c> rather than deciding it from the body; that the
    /// real server genuinely answers <c>400</c> for this input is pinned separately, over a real
    /// socket, by
    /// <c>Scrinia.Server.Tests.RemoteLoadIndexTests.TryUpdateEntry_ReturnsFalse_WhenTheMutationChangesKeywords</c>.</para>
    /// </summary>
    [Fact]
    public async Task UpdateMeta_RemoteStore_ReportsNotSupported_WhenTheServerRefusesTheWrite()
    {
        using var scope = new RemoteStoreScope(OneEntryListJson, patchStatus: HttpStatusCode.BadRequest);

        var response = await Tools().UpdateMeta("alpha", keywords: ["curated"]);

        response.Status.Should().Be("error",
            because: "the server refused the write, so reporting success would be a report of " +
                     "something that did not happen");
        response.ErrorCode.Should().Be(ErrorCodes.NotSupported,
            because: "the entry exists — this is a refused capability, not a missing memory");
        response.ActionNeeded.Should().NotBeNullOrEmpty(
            because: "a refusal without a next step leaves the caller stuck");
    }

    /// <summary>The pre-existing NOT_FOUND case must still answer NOT_FOUND: the fail-closed path
    /// above must not swallow the ordinary "no such memory" answer.</summary>
    [Fact]
    public async Task UpdateMeta_RemoteStore_StillReportsNotFound_WhenTheEntryDoesNotExist()
    {
        using var scope = new RemoteStoreScope(EmptyListJson);

        var response = await Tools().UpdateMeta("absent", keywords: ["curated"]);

        response.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>And the "nothing to change" case must still be the warning it was, not an error:
    /// TryUpdateEntry returns false for a null mutation too, and the three causes have to stay
    /// distinguishable.</summary>
    [Fact]
    public async Task UpdateMeta_RemoteStore_StillWarns_WhenNothingWasSpecified()
    {
        using var scope = new RemoteStoreScope(OneEntryListJson);

        var response = await Tools().UpdateMeta("alpha");

        response.Status.Should().Be("warning");
    }

    // ── Task 3: the narrowed optional site in Recall ─────────────────────────

    [Fact]
    public async Task Recall_AgentPath_RemoteStore_FallsThroughInsteadOfThrowing()
    {
        // Recall's /agent/ branch prefers a workspace .md file but is not local-only: it narrows
        // with `as` and falls through to NMP/2 resolution when there is no local store. Remotely
        // that fallback reaches the backend, 404s, and surfaces as a clean NOT_FOUND.
        //
        // This is the branch's only narrowed-optional site that had no remote coverage. An eager
        // narrowing here would throw instead, which is exactly what the fixture exists to catch.
        using var scope = new RemoteStoreScope();

        string yaml = await Tools().MemoryRecall(path: "/agent/profile");

        yaml.Should().Contain(ErrorCodes.NotFound);
        yaml.Should().NotContain(ErrorCodes.NotSupported,
            because: "a missing agent file remotely is a lookup miss, not a refused capability");
    }
}
