using System.Linq;
using FluentAssertions;
using Scrinia;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Server.Tests;

/// <summary>
/// The MCP write tools driven against a <b>real remote store</b> — an <c>HttpMemoryStore</c>
/// installed as <c>MemoryStoreContext.Current</c> and pointed at a Kestrel loopback host — asserting
/// on what the server actually persisted, read back over the wire.
///
/// <para>This is the gap that let this branch ship two regressions in the exact defect class it
/// exists to close. Every other test of these tools runs against a <c>FileMemoryStore</c>, where
/// <c>Upsert</c> persists and metadata survives whatever the write path does; remotely
/// <c>Upsert</c> is a deliberate no-op and <c>WriteArtifactAsync</c> is the only channel, so a tool
/// that assembles metadata around the write rather than handing it to the write loses it, silently,
/// while reporting success. Assertions here read the entry back with a fresh <c>LoadIndex</c>, never
/// the in-process value the tool returned.</para>
///
/// <para>Kestrel, not <c>TestServer</c>: <c>HttpMemoryStore</c> sends synchronously and
/// <c>TestHost.ClientHandler.Send</c> throws unconditionally. Same pattern as
/// <c>Conformance/StoreConformanceFixture</c> and <c>RemoteLoadIndexTests</c>.</para>
/// </summary>
public sealed class RemoteWritePathMetadataTests : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private HttpMemoryStore _remote = null!;
    private IMemoryStore? _previousStore;

    public Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized.
        _factory.UseKestrel(0);
        _factory.StartServer();
        _remote = new HttpMemoryStore(_factory.CreateAuthenticatedClient(), _factory.PrimaryStore);

        _previousStore = MemoryStoreContext.Current;
        MemoryEventSinkContext.Default = null;
        MemoryStoreContext.Current = _remote;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        MemoryStoreContext.Current = _previousStore;
        MemoryEventSinkContext.Default = null;
        _remote.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static ScriniaMcpTools Tools() => new();

    /// <summary>Re-reads the entry from the server. Never the in-process value a tool returned —
    /// that is exactly what looked fine while nothing was persisted.</summary>
    private ArtifactEntry Reload(string path)
    {
        var (scope, subject) = _remote.ParseQualifiedName(path);
        return _remote.LoadIndex(scope).FirstOrDefault(e => e.Name == subject)
            ?? throw new InvalidOperationException($"'{path}' is not in the remote listing.");
    }

    private static string[] Values(ArtifactEntry entry, string key) =>
        entry.Attributes is not null && entry.Attributes.TryGetValue(key, out var v) ? v : [];

    /// <summary>
    /// Measured before the fix: <c>remember()</c> persisted
    /// <c>tag=[alpha;beta], anchor=[how do widgets work], person=[maria]</c>, and the very next
    /// <c>append()</c> left <c>mention=[body;more]</c> and nothing else. <c>append</c> called
    /// <c>WriteArtifactAsync</c> with no <c>meta</c>, so the server re-ran its whole store path and
    /// replaced the entry with server-extracted attributes only; the curated map it then built went
    /// to the no-op <c>Upsert</c>, and the tool reported success. Identical calls against a local
    /// store preserved everything — a remote-only divergence.
    /// </summary>
    [Fact]
    public async Task Append_RemoteStore_PreservesTheAttributesRememberPersisted()
    {
        string remembered = await Tools().MemoryRemember(
            path: "/probe/appendmeta",
            content: ["body of the memory"],
            attributes: new Dictionary<string, string[]>
            {
                ["tag"] = ["alpha", "beta"],
                ["anchor"] = ["how do widgets work"],
                ["person"] = ["maria"],
            });
        remembered.Should().Contain("status: success");

        Values(Reload("/probe/appendmeta"), "tag").Should().BeEquivalentTo(["alpha", "beta"]);

        string appended = await Tools().MemoryAppend(path: "/probe/appendmeta", content: "more body");
        appended.Should().Contain("status: success");

        var after = Reload("/probe/appendmeta");
        Values(after, "tag").Should().BeEquivalentTo(["alpha", "beta"],
            because: "append rewrites content, not curated metadata — and remotely the write is " +
                     "the only channel that metadata has");
        Values(after, "anchor").Should().Contain("how do widgets work");
        Values(after, "person").Should().Contain("maria");
    }

    /// <summary>
    /// <c>compact()</c> had the same missing-<c>meta</c> defect, but it is NOT provable end-to-end
    /// here, and this pins why rather than pretending otherwise: a remote <c>compact()</c> never
    /// reaches its write at all. Multi-element content is flattened on the way back —
    /// <c>HttpMemoryStore.ResolveArtifactAsync</c> re-encodes the server's joined content as a
    /// single chunk — so a memory stored as two chunks reads back as one and compact returns
    /// "already a single chunk" before writing anything. An earlier version of this test asserted
    /// the attributes survived and passed against the UNFIXED code for exactly that reason.
    /// <para>The flattening is a separate, recorded residual. The compact metadata contract is
    /// pinned at the write seam instead, in
    /// <c>Scrinia.Tests.RememberWriteMetadataTests.Compact_HandsTheExistingEntrysMetadataToTheWriteSeam</c>,
    /// which does fail without the fix.</para>
    /// </summary>
    [Fact]
    public async Task Compact_RemoteStore_ShortCircuits_BecauseTheArtifactReadsBackAsOneChunk()
    {
        await Tools().MemoryRemember(
            path: "/probe/compactmeta",
            content: ["first chunk", "second chunk"],
            attributes: new Dictionary<string, string[]> { ["tag"] = ["gamma"] });

        string compacted = await Tools().MemoryCompact(path: "/probe/compactmeta");

        compacted.Should().Contain("Already a single chunk",
            because: "a remote read flattens multi-element content, so compact has nothing to do — " +
                     "the residual that makes an end-to-end compact assertion here vacuous");
        Values(Reload("/probe/compactmeta"), "tag").Should().Contain("gamma",
            because: "a no-op compact must at least leave the entry alone");
    }

    /// <summary>
    /// Measured before the fix: <c>link()</c> answered
    /// <c>status: success / action: linked / Linked '/probe/doc' &lt;-&gt; '/probe/other'</c> while the
    /// entry gained no <c>ref</c> attribute at all. <c>UpdateMeta</c> reached the no-op
    /// <c>Upsert</c> directly, bypassing the <c>TryUpdateEntry</c> override that fails closed. It
    /// now routes through <c>TryUpdateEntry</c>, so the refusal is reported instead of invented.
    /// <para>The assertion is deliberately two-sided: the response must be an error AND the entry
    /// must be unchanged. Either alone would pass for the wrong reason.</para>
    /// </summary>
    [Fact]
    public async Task Link_RemoteStore_ReportsFailure_RatherThanClaimingASuccessItCannotBack()
    {
        await Tools().MemoryRemember(path: "/probe/doc", content: ["the source memory"]);
        await Tools().MemoryRemember(path: "/probe/other", content: ["the target memory"]);

        string yaml = await Tools().MemoryLink(path: "/probe/doc", destination: "/probe/other");

        yaml.Should().NotContain("status: success",
            because: "nothing was persisted, so reporting a link is a lie — the precise defect " +
                     "this branch exists to close, reintroduced by fixing LoadIndex");
        yaml.Should().Contain("status: error");

        Values(Reload("/probe/doc"), "ref").Should().BeEmpty(
            because: "the honest report must match reality: no ref attribute reached the server");
        Values(Reload("/probe/other"), "ref").Should().BeEmpty();
    }

}
