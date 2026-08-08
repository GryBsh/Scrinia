using System.Net;
using System.Text;
using FluentAssertions;
using Scrinia;
using Scrinia.Core;
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
        "updatedAt":null,"description":"an entry","tags":null}],"total":1}
        """;

    /// <summary>
    /// Serves the one read endpoint these tools reach remotely (<c>GET .../memories</c>, used by
    /// <see cref="HttpMemoryStore.ListScoped(string?)"/>). Everything else 404s, which is
    /// deliberate: if a tool starts making an unexpected call, the test should notice.
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
            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.EndsWith("/memories", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(listJson, Encoding.UTF8, "application/json"),
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
    public async Task List_DriftMode_RemoteStore_StillThrows()
    {
        // Deliberate boundary, not a regression: drift detection resolves file paths against the
        // workspace root unconditionally, so it was never available remotely. Pinned so that a
        // future change to the drift branch is a conscious one. Task 3 gives this a clean error.
        using var scope = new RemoteStoreScope(OneEntryListJson);

        var act = async () => await Tools().MemoryList(mode: "drift");

        // Assert on the message, not just the type: HttpMessageHandler itself throws
        // NotSupportedException for an unimplemented sync path, so the bare type would let a
        // broken fixture masquerade as a passing test.
        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*local filesystem access*");
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
}
