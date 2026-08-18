using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// Task 4 of the event-pipeline-unification branch: proves the two write doors — REST via
/// <c>PluginPipeline</c> and the hosted MCP endpoint via <c>MemoryEventSinkContext</c> — now
/// produce the same maintenance outcome for the same write, and that the fix for that (an
/// <c>IMemoryOperationHook</c> adapter carrying the registered sinks onto REST) does not make a
/// dual-interface sink fire twice.
///
/// <para><b>The hazard this suite exists to avoid:</b> Tasks 2 and 3 already fixed the underlying
/// behaviour, so every test below passes on arrival. A test that has never been observed failing
/// is not evidence of anything — see the task report for the three breaks each test was proven
/// against, and their exact recorded output.</para>
/// </summary>
public sealed class EventPipelineParityTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public EventPipelineParityTests(ScriniaServerFactory factory) => _factory = factory;

    /// <summary>
    /// Pins the fix's headline guarantee: <c>MaintenanceEventSink</c>'s ref-linking must run on a
    /// REST write exactly as it already did on an MCP write — a REST-stored memory B whose content
    /// references memory A must leave A carrying the inbound <c>ref</c> attribute, the same as the
    /// identical scenario driven entirely over MCP.
    ///
    /// <para>REST writes to <see cref="ScriniaServerFactory.PrimaryStore"/>, MCP writes to
    /// <see cref="ScriniaServerFactory.SecondaryStore"/>, under different memory names, so the two
    /// halves cannot collide or mask one another. Both outcomes are gathered BEFORE either is
    /// asserted, and both are asserted inside one <see cref="AssertionScope"/>: a regression that
    /// breaks only the REST half must show up as exactly one failed expectation here, with the MCP
    /// one passing silently beside it — never as an early exception that hides whether the MCP
    /// half would have passed too. (Verified: see the task report's Step 3 for the REST-only
    /// failure this produces when the REST door's hook registration is removed.)</para>
    /// </summary>
    [Fact]
    public async Task RefLinking_AddsTheInboundRefAttribute_OnBothDoors()
    {
        var client = _factory.CreateAuthenticatedClient();

        // ── REST half ──────────────────────────────────────────────────────────
        await StoreViaRestAsync(client, _factory.PrimaryStore, "parity-rest-ref-a",
            "a target memory, reached only by an inbound reference");
        await StoreViaRestAsync(client, _factory.PrimaryStore, "parity-rest-ref-b",
            "points at [[parity-rest-ref-a]]");
        // EventSinkOperationHook now dispatches sinks fire-and-forget too (I-3), matching the MCP
        // door's BackgroundWork detach — the REST response can return before ref-linking has run.
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));
        var restTarget = await GetEntryAsync(client, _factory.PrimaryStore, "parity-rest-ref-a");

        // ── MCP half ───────────────────────────────────────────────────────────
        string mcpSession = await InitializeMcpSessionAsync(client, _factory.SecondaryStore);
        await StoreViaMcpAsync(client, _factory.SecondaryStore, mcpSession, "parity-mcp-ref-a",
            "a target memory, reached only by an inbound reference");
        await StoreViaMcpAsync(client, _factory.SecondaryStore, mcpSession, "parity-mcp-ref-b",
            "points at [[parity-mcp-ref-a]]");
        // The MCP door dispatches its event sink fire-and-forget (FireEventSinkAsync in
        // MemoryTools.Core.cs) — the write response can return before ref-linking has run.
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));
        var mcpTarget = await GetEntryAsync(client, _factory.SecondaryStore, "parity-mcp-ref-a");

        using (new AssertionScope())
        {
            restTarget.Attributes.Should().NotBeNull(
                    because: "a REST write must trigger the same ref-linking maintenance the MCP " +
                             "door gets — this is the exact original defect: REST got embeddings " +
                             "but no ref-linking or orphan marking")
                .And.ContainKey("ref")
                .WhoseValue.Should().Contain(
                    v => v.Equals("parity-rest-ref-b", StringComparison.OrdinalIgnoreCase),
                    because: "the referencing memory's name must be recorded as the inbound link");

            mcpTarget.Attributes.Should().NotBeNull(
                    because: "this is the MCP baseline the REST half above must match — MCP " +
                             "already did this before this branch existed")
                .And.ContainKey("ref")
                .WhoseValue.Should().Contain(
                    v => v.Equals("parity-mcp-ref-b", StringComparison.OrdinalIgnoreCase),
                    because: "the referencing memory's name must be recorded as the inbound link");
        }
    }

    /// <summary>
    /// The other half of <c>MaintenanceEventSink</c>'s job: a memory nothing references must carry
    /// the orphan marker (<see cref="KnownAttributes.OrphanKey"/> = <c>"true"</c>), on both doors.
    ///
    /// <para>Same shape as <see cref="RefLinking_AddsTheInboundRefAttribute_OnBothDoors"/> — REST
    /// against <see cref="ScriniaServerFactory.PrimaryStore"/>, MCP against
    /// <see cref="ScriniaServerFactory.SecondaryStore"/>, both outcomes gathered first and asserted
    /// together in one <see cref="AssertionScope"/> so a REST-only regression reads as exactly one
    /// failed expectation rather than an early exception. (Verified: see the task report's Step 3
    /// for the REST-only failure this produces when the REST door's hook registration is
    /// removed.)</para>
    /// </summary>
    [Fact]
    public async Task OrphanMarking_FlagsAMemoryWithNoInboundReferences_OnBothDoors()
    {
        var client = _factory.CreateAuthenticatedClient();

        await StoreViaRestAsync(client, _factory.PrimaryStore, "parity-rest-orphan",
            "isolated content nothing else refers to");
        // REST sink dispatch is detached (I-3) — drain before asserting, same as the MCP half below.
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));
        var restEntry = await GetEntryAsync(client, _factory.PrimaryStore, "parity-rest-orphan");

        string mcpSession = await InitializeMcpSessionAsync(client, _factory.SecondaryStore);
        await StoreViaMcpAsync(client, _factory.SecondaryStore, mcpSession, "parity-mcp-orphan",
            "isolated content nothing else refers to");
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));
        var mcpEntry = await GetEntryAsync(client, _factory.SecondaryStore, "parity-mcp-orphan");

        using (new AssertionScope())
        {
            restEntry.Attributes.Should().NotBeNull(
                    because: "a REST write with no inbound references must be orphan-marked " +
                             "exactly as the identical MCP write is")
                .And.ContainKey(KnownAttributes.OrphanKey)
                .WhoseValue.Should().Contain("true",
                    because: "the marker's only value is \"true\" — its presence, not its " +
                             "content, is what makes an entry an orphan");

            mcpEntry.Attributes.Should().NotBeNull(
                    because: "this is the MCP baseline the REST half above must match")
                .And.ContainKey(KnownAttributes.OrphanKey)
                .WhoseValue.Should().Contain("true",
                    because: "the marker's only value is \"true\"");
        }
    }

    // ── REST helpers ─────────────────────────────────────────────────────────────

    private static async Task StoreViaRestAsync(HttpClient client, string store, string name, string body)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{store}/memories", new StoreRequest([body], name, body));
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: $"the REST store of '{name}' must succeed for this test's REST-side " +
                     "scenario to mean anything");
    }

    private static async Task<MemoryEntry> GetEntryAsync(HttpClient client, string store, string name)
    {
        var response = await client.GetAsync($"/api/v1/stores/{store}/memories/{name}/entry");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"'{name}' must exist and be readable for this test's assertions to mean " +
                     "anything");
        var entry = await response.Content.ReadFromJsonAsync<MemoryEntry>();
        entry.Should().NotBeNull();
        return entry!;
    }

    // ── MCP helpers — same mechanism as McpEndpointTests, not a second one ─────────
    //
    // McpEndpointTests' handshake helpers (BuildMcpRequest/CreateMcpInitRequest/CallToolAsync) are
    // private instance methods on that class and cannot be called from here, so this reproduces
    // the same JSON-RPC/Streamable-HTTP shape rather than inventing a different way to drive the
    // endpoint: same protocol version, same headers, same initialize → notifications/initialized →
    // tools/call sequence.

    private static async Task<string> InitializeMcpSessionAsync(HttpClient client, string store)
    {
        var initResponse = await client.SendAsync(CreateMcpInitRequest(store));
        initResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the MCP session must initialize before any tool can be called through it");
        string? sessionId = initResponse.Headers.TryGetValues("Mcp-Session-Id", out var ids)
            ? ids.FirstOrDefault() : null;
        sessionId.Should().NotBeNullOrEmpty(
            "initialize must hand back a session id to drive further calls with");

        using var notified = BuildMcpRequest(store, sessionId!,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await client.SendAsync(notified);

        return sessionId!;
    }

    private static async Task StoreViaMcpAsync(
        HttpClient client, string store, string sessionId, string name, string body)
    {
        // Built with JsonObject rather than string interpolation: the ref-linking scenario's
        // content contains "[[" / "]]", and this avoids hand-rolling JSON escaping for it.
        var rpc = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "remember",
                ["arguments"] = new JsonObject
                {
                    ["path"] = name,
                    ["content"] = new JsonArray(JsonValue.Create(body)),
                },
            },
        };

        using var request = BuildMcpRequest(store, sessionId, rpc.ToJsonString());
        var response = await client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("\"result\"",
            because: $"the MCP remember() call storing '{name}' must succeed for this test's " +
                     "MCP-side baseline to mean anything");
    }

    private static HttpRequestMessage BuildMcpRequest(string store, string sessionId, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp?store={store}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static HttpRequestMessage CreateMcpInitRequest(string store)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp?store={store}");
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""",
            Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }
}

/// <summary>
/// Guards the exclusion filter in <c>EventSinkOperationHook</c>
/// (<c>.Where(s =&gt; s is not IMemoryOperationHook)</c>) — the highest-risk line on this branch.
/// Without it, a component that is BOTH an <see cref="IMemoryEventSink"/> and an
/// <see cref="IMemoryOperationHook"/> (the shape <c>BuiltInEmbeddingsService</c> has in
/// production) fires twice per REST write: once directly as a hook via <c>PluginPipeline</c>, and
/// again through <c>EventSinkOperationHook</c>'s composite, which would otherwise re-fire every
/// registered sink including itself.
///
/// <para>The default <see cref="ScriniaServerFactory"/> cannot exercise this: it never registers
/// <c>BuiltInEmbeddingsService</c>, because no Model2Vec model is downloaded under the test
/// binary's output directory, so <c>IsAvailable</c> is false and the only real dual-interface
/// component in the codebase never gets registered against it — deleting the exclusion filter
/// would change nothing observable there. A spy that implements both interfaces, registered
/// directly by this factory, is what makes the double-fire observable without embeddings. See the
/// task report's Step 3 for the exact count (1 → 2) this test was proven against when the filter
/// is removed.</para>
/// </summary>
public sealed class SinkFiresExactlyOnceTests : IClassFixture<DoubleFireSpyServerFactory>
{
    private readonly DoubleFireSpyServerFactory _factory;

    public SinkFiresExactlyOnceTests(DoubleFireSpyServerFactory factory) => _factory = factory;

    [Fact]
    public async Task RestWrite_InvokesADualInterfaceSink_ExactlyOnce()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest(["content for the double-fire probe"], "double-fire-probe",
                "content for the double-fire probe"));
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "the write itself must succeed for the invocation count below to mean " +
                     "anything");

        _factory.Spy.StoreInvocationCount.Should().Be(1,
            because: "a component registered as BOTH an IMemoryEventSink and an " +
                     "IMemoryOperationHook must fire exactly once per REST write — twice means " +
                     "EventSinkOperationHook's exclusion filter let it re-fire through the " +
                     "composite after it had already run as a hook");
    }
}

/// <summary>
/// Registers <see cref="DoubleFireSpy"/> as both sink interfaces directly, deterministically
/// reproducing the dual-interface shape <c>BuiltInEmbeddingsService</c> has in production without
/// needing a downloaded Model2Vec model or a running embedding endpoint. Mirrors the established
/// small-sealed-<c>WebApplicationFactory</c> pattern (<c>PagingRateLimitServerFactory</c> in
/// <c>RemoteLoadIndexTests.cs</c>, <c>EmbeddingsAvailableServerFactory</c>/
/// <c>NoEmbeddingsServerFactory</c> in <c>ServerSinkRegistrationTests.cs</c>).
/// </summary>
public sealed class DoubleFireSpyServerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;

    public string PrimaryStore { get; } = "test-store";
    public DoubleFireSpy Spy { get; } = new();
    private string TestApiKey { get; set; } = "";

    public DoubleFireSpyServerFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinium-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Scrinia:DataDir", _tempDir);
        builder.UseSetting($"Scrinia:Stores:{PrimaryStore}", Path.Combine(_tempDir, "stores", PrimaryStore));

        builder.ConfigureServices(services =>
        {
            // Registered as BOTH interfaces on the SAME instance — exactly the shape
            // BuiltInEmbeddingsService has in production (Program.cs registers it as both an
            // IMemoryEventSink and an IMemoryOperationHook), and exactly what
            // EventSinkOperationHook's exclusion filter exists to keep from double-firing on REST.
            services.AddSingleton<IMemoryEventSink>(Spy);
            services.AddSingleton<IMemoryOperationHook>(Spy);

            var sp = services.BuildServiceProvider();
            var keyStore = sp.GetRequiredService<ApiKeyStore>();
            var (rawKey, _, _) = keyStore.CreateKey(
                "double-fire-test-user", [PrimaryStore], ["read", "store"], "test");
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

/// <summary>
/// Counts store-side invocations across both interfaces it implements, so a REST write through
/// <c>PluginPipeline</c> (which sees this as an <see cref="IMemoryOperationHook"/>) and
/// <c>EventSinkOperationHook</c>'s composite (which would see this as an
/// <see cref="IMemoryEventSink"/> if the exclusion filter did not exclude it) land in the SAME
/// counter — exactly the double-fire the filter exists to prevent.
/// </summary>
public sealed class DoubleFireSpy : IMemoryEventSink, IMemoryOperationHook
{
    private int _storeInvocationCount;

    public int StoreInvocationCount => Volatile.Read(ref _storeInvocationCount);

    public Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct)
    {
        Interlocked.Increment(ref _storeInvocationCount);
        return Task.CompletedTask;
    }

    public Task OnAppendedAsync(string qualifiedName, string content, IPluginStore store, CancellationToken ct)
        => Task.CompletedTask;

    public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IPluginStore store, CancellationToken ct)
        => Task.CompletedTask;

    public Task OnAfterStoreAsync(AfterStoreContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _storeInvocationCount);
        return Task.CompletedTask;
    }
}
