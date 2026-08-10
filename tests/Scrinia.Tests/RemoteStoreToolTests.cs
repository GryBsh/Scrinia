using System.Net;
using System.Text;
using FluentAssertions;
using Scrinia;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;
using Scrinia.Mcp;
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
    /// Serves the two endpoints these tools reach remotely: <c>GET .../memories</c> (used by
    /// <see cref="HttpMemoryStore.ListScoped(string?)"/>) and <c>POST .../memories</c> (used by
    /// <see cref="HttpMemoryStore.WriteArtifactAsync"/>, which throws
    /// <see cref="HttpRequestException"/> on any non-success status). Everything else 404s, which
    /// is deliberate: if a tool starts making an unexpected call, the test should notice.
    /// </summary>
    private sealed class StubBackendHandler(string listJson) : HttpMessageHandler
    {
        // Both overrides are required: HttpMemoryStore.ListScoped goes through SendSync, which
        // calls the synchronous HttpClient.Send. A handler that only overrides SendAsync throws
        // NotSupportedException from inside HttpMessageHandler — which would have looked exactly
        // like the regression under test.
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            bool isMemoriesCollection =
                request.RequestUri!.AbsolutePath.EndsWith("/memories", StringComparison.Ordinal);

            if (request.Method == HttpMethod.Get && isMemoriesCollection)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(listJson, Encoding.UTF8, "application/json"),
                };
            }

            // A persistent remote remember() writes through here. The body is ignored by the
            // caller — only the status matters — but it must be a success status or
            // WriteArtifactAsync throws before the code under test is ever reached.
            if (request.Method == HttpMethod.Post && isMemoriesCollection)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
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

        public RemoteStoreScope(string listJson = EmptyListJson)
        {
            _previous = MemoryStoreContext.Current;
            MemoryEventSinkContext.Default = null;
            var http = new HttpClient(new StubBackendHandler(listJson))
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
    /// A non-local store that records what was written to it. Subclasses <see cref="FakeMemoryStore"/>
    /// and overrides one member, rather than hand-writing 23. Its value over the
    /// <see cref="HttpMemoryStore"/> fixture is that <see cref="Upsert"/> is observable — over
    /// HTTP the stored entry's <c>FileHashes</c> simply never leaves the process.
    /// </summary>
    private sealed class RecordingRemoteStore : FakeMemoryStore
    {
        public List<ArtifactEntry> Stored { get; } = [];

        public override void Upsert(ArtifactEntry entry, string scope = "local") => Stored.Add(entry);
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
        // With `file` refs there IS something to hash, and remotely there is no disk to hash it
        // from. Degrade rather than refuse: the memory is stored, the drift hashes are simply
        // absent.
        //
        // Asserted through a recording double rather than the HttpMemoryStore fixture, because
        // FileHashes is unobservable over the wire: StoreApiRequest carries content, name,
        // description, attributes and keywords — the path->hash map is not among them — and
        // HttpMemoryStore.Upsert is a no-op. A double's Upsert is the only seam where the stored
        // entry can actually be inspected.
        //
        // The probe file lives in the CWD deliberately. The failure this guards against is
        // fabricating a root — `?? Directory.GetCurrentDirectory()` — which would silently hash
        // arbitrary files off the server's working directory. With the probe present, that
        // implementation populates FileHashes and this test fails.
        using var scope = new DoubleStoreScope(new RecordingRemoteStore());
        string probeName = $"drift-probe-{Guid.NewGuid():N}.txt";
        string probePath = Path.Combine(Directory.GetCurrentDirectory(), probeName);
        await File.WriteAllTextAsync(probePath, "contents that would hash to something");

        try
        {
            string yaml = await Tools().MemoryRemember(
                path: "/api/hashed",
                content: ["a note that references a file"],
                attributes: new Dictionary<string, string[]> { ["file"] = [probeName] });

            yaml.Should().Contain("status: success");

            var stored = ((RecordingRemoteStore)scope.Store).Stored;
            stored.Should().ContainSingle(because: "the memory must still be persisted");
            stored[0].FileHashes.Should().BeNull(
                because: "with no local workspace there is no root to resolve refs against, and " +
                         "a fabricated one would hash whatever happens to sit in the CWD");
            stored[0].Attributes.Should().NotBeNull().And.ContainKey("file",
                because: "the reference itself is the user's data and must survive; only the " +
                         "derived hashes are dropped");
        }
        finally
        {
            File.Delete(probePath);
        }
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
