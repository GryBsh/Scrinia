using FluentAssertions;
using Scrinia;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;

namespace Scrinia.Tests;

/// <summary>
/// <see cref="EphemeralEntry"/> had no <c>FileHashes</c> member, so a <c>file</c> reference on an
/// ephemeral memory was derived by the pipeline and then discarded — the entry looked clean while
/// carrying no drift baseline at all. These tests pin that the baseline now survives the write, is
/// consistent across every projection of the same memory, and — for a remote client — is hashed
/// against the caller's real workspace rather than a throwaway temp directory.
/// </summary>
public sealed class EphemeralLifecycleTests : IDisposable
{
    private readonly string _workspace;
    private readonly FileMemoryStore _store;

    public EphemeralLifecycleTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "scrinia-eph-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(Path.Combine(_workspace, "src", "thing.cs"), "// hello");
        _store = new FileMemoryStore(_workspace);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task EphemeralWrite_RecordsADriftBaselineForItsFileReferences()
    {
        // A BARE path, not `file:src/thing.cs`. Prose `file:` is NOT a supported way to produce a
        // `file` attribute, and worse than unsupported: FilePathPattern's (?<!["'`=:]) lookbehind
        // does not suppress the match, it shifts the start one character — so `file:src/thing.cs`
        // silently yields the corrupt path `rc/thing.cs`. The supported forms are a bare path in
        // content (this one), `file:path` in the `keywords` argument, an explicit `attributes` map,
        // or a line-start `file: path` WITH whitespace after the colon.
        var written = await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"]));

        written.FileHashes.Should().ContainKey("src/thing.cs");

        _store.GetEphemeral("scratch")!.FileHashes.Should().ContainKey("src/thing.cs",
            because: "the stored entry and the returned entry are two views of one memory");
    }

    [Fact]
    public async Task AppendToEphemeral_PreservesTheOriginalBaselineEvenIfTheFileChangesLater()
    {
        // Step 1a: the ExistingMetadata an append carries forward used to read only
        // `existingEntry?.FileHashes` (an ArtifactEntry field) with no `?? existingEph?.FileHashes`
        // fallback — correct only while EphemeralEntry had no FileHashes member of its own. The
        // moment it gained one, this line would make `skip` null for every ephemeral append, so
        // MemoryDerivation.ForAppend's HashFileRefs would treat "src/thing.cs" as never-baselined
        // and re-hash it against whatever the file currently contains — silently moving the
        // baseline to drifted content and destroying the exact signal D-21 exists to provide.
        var written = await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"]));
        string originalHash = written.FileHashes!["src/thing.cs"];

        // Mutate the file AFTER the baseline was recorded. A correct append must not notice.
        File.WriteAllText(Path.Combine(_workspace, "src", "thing.cs"), "// changed after baseline");

        var appended = await _store.AppendAsync(
            new MemoryWrite("~scratch", "local", ["no new file reference in this chunk"]));

        appended.FileHashes.Should().ContainKey("src/thing.cs").WhoseValue.Should().Be(originalHash,
            because: "an append must carry the ORIGINAL baseline forward, not silently re-hash the " +
                "file to its current (possibly drifted) content");
        _store.GetEphemeral("scratch")!.FileHashes.Should().ContainKey("src/thing.cs")
            .WhoseValue.Should().Be(originalHash);
    }

    [Fact]
    public async Task ListEphemeral_ProjectsFileHashesLikeTheWriteResponseDoes()
    {
        // Three projections of one ephemeral memory exist (PersistEphemeral's stored entry, its
        // returned entry, and ListEphemeral's). A field present in some and null in others means
        // the answer depends on which read you came through.
        await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"]));

        var listed = _store.ListEphemeral().Single().Entry;
        // ContainKey first: BeEquivalentTo alone passes over two nulls, so it would stay green
        // with the whole feature (or just this projection) absent.
        listed.FileHashes.Should().ContainKey("src/thing.cs");
        listed.FileHashes.Should().BeEquivalentTo(_store.GetEphemeral("scratch")!.FileHashes);
    }

    [Fact]
    public async Task HttpMemoryStore_HashesEphemeralFileRefsAgainstTheSuppliedWorkspaceRoot()
    {
        // HttpMemoryStore derives its own ephemeral writes in-process via a FileMemoryStore helper
        // (WriteEphemeralInProcessAsync). Before Step 3, that helper was always rooted at a throwaway
        // temp directory, so a `file` reference resolved to nothing no matter what the caller passed
        // — the write would succeed with a baseline recording the file as unhashable. Passing the
        // real workspace root is what makes the hash correct rather than merely present.
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5000") };
        using var remote = new HttpMemoryStore(http, "default",
            retryOptions: new RetryOptions(MaxRetries: 0), workspaceRoot: _workspace);

        var written = await remote.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"]));

        string expectedHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(Path.Combine(_workspace, "src", "thing.cs"))));

        written.FileHashes.Should().ContainKey("src/thing.cs")
            .WhoseValue.Should().Be(expectedHash);
    }
}
