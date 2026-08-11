using System.Linq;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// <c>MemoryOrchestrator</c>'s existing-entry lookups compared <c>e.Name</c> (from the index)
/// against the <c>subject</c> <c>ParseQualifiedName</c> produced (from the caller's name) with an
/// ordinal <c>==</c>. Neither side folds case, so a memory created as <c>MixedCase</c> and written
/// again as <c>mixedcase</c> was reported "new": the prior version was never archived and
/// <c>CreatedAt</c> was reset — while the write itself still landed on the same file, because the
/// path is built from <c>SanitizeName(subject)</c> and NTFS is case-insensitive. The previous
/// content was destroyed with no archive behind it.
///
/// <para>Mirrors <see cref="RemoteLoadIndexTests"/>'s fixture (<c>UseKestrel(0)</c> +
/// <c>StartServer()</c> + <c>CreateAuthenticatedClient()</c>). Kestrel is not strictly required
/// here — nothing in these tests sends synchronously — but the shape is kept identical so the two
/// files stay comparable.</para>
/// </summary>
public sealed class CaseInsensitiveRestoreTests : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized, which the
        // first touch of Services or CreateClient would do (mirrors RemoteLoadIndexTests).
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateAuthenticatedClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task StoreAsync(string name, string body, Dictionary<string, string[]>? attributes = null)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body, Attributes: attributes));
        resp.EnsureSuccessStatusCode();
    }

    private async Task AppendAsync(string name, string body)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories/{Uri.EscapeDataString(name)}/append",
            new AppendRequest(body));
        resp.EnsureSuccessStatusCode();
    }

    private async Task<MemoryListItem> ListEntryAsync(string name)
    {
        var list = await _client.GetFromJsonAsync<ListResponse>(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories");
        list.Should().NotBeNull();
        var matches = list!.Memories
            .Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        matches.Should().ContainSingle(
            because: "a case-mismatched write must update the one existing memory, not sit " +
                     "alongside it as a second entry");
        return matches[0];
    }

    /// <summary>
    /// The server's <c>versions/</c> directory for the primary store's default scope. Reached
    /// through the live <see cref="StoreManager"/> rather than a test-owned path because the
    /// factory does not expose its temp dir; this is the same store instance the request pipeline
    /// hands each endpoint (<c>Program.cs</c> resolves <c>StoreManager.GetStore</c> per request).
    /// </summary>
    private string VersionsDir()
    {
        var store = _factory.Services.GetRequiredService<StoreManager>().GetStore(_factory.PrimaryStore);
        store.Should().BeOfType<FileMemoryStore>(
            because: "the archive is only observable on disk, and only the file-backed store has one");
        return Path.Combine(((FileMemoryStore)store).WorkspaceRoot, ".scrinia", "memories", "versions");
    }

    /// <summary>
    /// <c>MemoryOrchestrator.cs:113</c>. Two assertions, both directly observable: the version
    /// archive on disk (the data that was actually lost) and <c>CreatedAt</c>/<c>UpdatedAt</c> on
    /// the wire (the visible symptom, derived from the same <c>existingEntry</c> lookup at
    /// <c>:114-115</c>).
    /// </summary>
    [Fact]
    public async Task ReStoringUnderADifferentCasing_ArchivesThePriorVersion_AndKeepsCreatedAt()
    {
        await StoreAsync("CasingVersioned", "the first body");
        var first = await ListEntryAsync("CasingVersioned");
        first.UpdatedAt.Should().BeNull(because: "precondition: this is a brand new memory");

        await Task.Delay(50); // so a reset CreatedAt is distinguishable from the original

        await StoreAsync("casingversioned", "the second body");

        string versionsDir = VersionsDir();
        Directory.Exists(versionsDir).Should().BeTrue(
            because: "the re-store overwrote the memory's content, so ArchiveVersion must have " +
                     "run first — with the ordinal name match it did not, and the first body was " +
                     "destroyed with nothing behind it");
        Directory.GetFiles(versionsDir, "*.nmp2").Should().HaveCount(1,
            because: "exactly one prior version existed to archive");

        var second = await ListEntryAsync("CasingVersioned");
        second.CreatedAt.Should().Be(first.CreatedAt,
            because: "a re-store under a different casing is an update of the same memory, so its " +
                     "creation time is the original one, not now");
        second.UpdatedAt.Should().NotBeNull(
            because: "UpdatedAt is null only for a memory that has never been rewritten");
    }

    /// <summary>
    /// <c>MemoryOrchestrator.cs:257</c>, the append path. With <c>existingEntry</c> null every
    /// value carried forward from the prior entry is lost: the caller's attribute map, the curated
    /// content preview, <c>CreatedAt</c>, and the version archive.
    /// <para><c>ChunkCount</c> is deliberately NOT the assertion here even though it is a wire
    /// field: it is computed from the appended artifact (<c>Nmp2ChunkedEncoder.GetChunkCount</c>,
    /// <c>MemoryOrchestrator.cs:194</c>), never from <c>existingEntry</c>, so it reads 2 both
    /// before and after this fix. Measured, not assumed — see the task report.</para>
    /// </summary>
    [Fact]
    public async Task AppendingUnderADifferentCasing_KeepsThePriorEntrysMetadata()
    {
        await StoreAsync("CasingAppended", "the first body",
            attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] });
        var first = await ListEntryAsync("CasingAppended");
        first.Attributes.Should().ContainKey("person", because: "precondition: the store carried it");

        await Task.Delay(50);

        await AppendAsync("casingappended", "the appended body");

        var second = await ListEntryAsync("CasingAppended");

        // Asserted first, on purpose: this is the measurement that showed ChunkCount cannot pin
        // this defect. It holds before the fix as well as after.
        second.ChunkCount.Should().Be(2, because: "both chunks are in the artifact either way");

        second.Attributes.Should().ContainKey("person",
            because: "the append merges the existing entry's attributes with the ones it extracts " +
                     "from the new chunk — a null existingEntry drops everything the memory knew");
        second.Attributes!["person"].Should().Contain("maria");
        second.CreatedAt.Should().Be(first.CreatedAt,
            because: "appending to a memory does not create it");
    }
}
