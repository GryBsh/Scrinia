using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Pins the three <see cref="FileMemoryStore"/> behaviours that shipped as ruling comments instead
/// of tests, on the premise that case sensitivity is unobservable on Windows. It is observable:
/// <see cref="CaseSensitiveDirectory"/> obtains a per-directory NTFS case-sensitive volume, and each
/// test below constructs a store rooted there. If the host cannot provide one, these tests SKIP
/// (never silently pass) via <see cref="Skip.If(bool, string)"/>.
/// </summary>
public sealed class CaseSensitiveFileStoreTests : IDisposable
{
    private readonly CaseSensitiveDirectory? _dir;

    public CaseSensitiveFileStoreTests()
    {
        _dir = CaseSensitiveDirectory.TryCreate("scrinia-case-fms");
    }

    public void Dispose() => _dir?.Dispose();

    /// <summary>
    /// Before the <c>File.Exists(SanitizeName(subject) + ".meta.json")</c> fast-path gate was
    /// removed from <see cref="FileMemoryStore.TryUpdateEntry"/>, that gate answered "no such entry"
    /// for a subject differing only in casing from its sidecar — on a case-sensitive filesystem the
    /// gate's exact-name probe missed "MixedCase.meta.json" for a lookup of "mixedcase", and
    /// TryUpdateEntry returned false without ever invoking <c>mutate</c>. The lookup below the gate
    /// was already case-insensitive; removing the gate let it be the sole authority.
    /// </summary>
    [SkippableFact]
    public void TryUpdateEntry_ResolvesCaseMismatchedSubject()
    {
        Skip.If(_dir is null, "This host cannot provide a case-sensitive directory.");
        using var store = new FileMemoryStore(_dir!.Path);

        store.Upsert(new ArtifactEntry("MixedCase", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new() { ["mention"] = ["seraphim"] }));

        bool changed = store.TryUpdateEntry("mixedcase", "local", e =>
            e with { Attributes = new(e.Attributes!, StringComparer.OrdinalIgnoreCase) { ["thing"] = ["seraphim"] } });

        changed.Should().BeTrue();
        var reloaded = store.LoadIndex().Single(e => e.Name == "MixedCase");
        reloaded.Attributes!.Should().ContainKey("thing");
    }

    /// <summary>
    /// <see cref="FileMemoryStore.Remove"/>'s fallback directory scan matches candidate sidecar
    /// filenames with <see cref="StringComparison.OrdinalIgnoreCase"/> specifically so a
    /// case-mismatched name resolves after the exact-name probe misses. On a case-sensitive
    /// filesystem, an ordinal fallback comparison would leave "mixedcase" unresolved against a
    /// sidecar stored as "MixedCase.meta.json", and Remove would report false for an entry that
    /// exists.
    /// </summary>
    [SkippableFact]
    public void Remove_DeletesCaseMismatchedName()
    {
        Skip.If(_dir is null, "This host cannot provide a case-sensitive directory.");
        using var store = new FileMemoryStore(_dir!.Path);

        store.Upsert(new ArtifactEntry("MixedCase", "uri", 1, 1, DateTimeOffset.UtcNow));

        bool removed = store.Remove("mixedcase");

        removed.Should().BeTrue();
        store.LoadIndex().Should().NotContain(e => e.Name == "MixedCase");
    }

    /// <summary>
    /// <see cref="FileMemoryStore.DeleteArtifact"/>'s fallback directory scan is the exact
    /// counterpart of Remove's, and was the half left case-sensitive while Remove was fixed: before
    /// this fallback matched case-insensitively, <c>forget("mixedcase")</c> against a memory stored
    /// as "MixedCase" removed the index entry (Remove succeeded) but reported the artifact file
    /// undeleted, orphaning "MixedCase.nmp2" on disk — unreachable through list() or search(), and
    /// invisible to a repeat forget(). This test writes the artifact directly (bypassing the index)
    /// so it exercises DeleteArtifact's fallback scan in isolation.
    /// </summary>
    [SkippableFact]
    public async Task DeleteArtifact_DeletesCaseMismatchedName()
    {
        Skip.If(_dir is null, "This host cannot provide a case-sensitive directory.");
        using var store = new FileMemoryStore(_dir!.Path);

        string artifact = Nmp2ChunkedEncoder.Encode("orphan-proof content");
        await store.WriteArtifactAsync("MixedCase", "local", artifact);

        bool deleted = store.DeleteArtifact("mixedcase", "local");

        deleted.Should().BeTrue();
        File.Exists(store.ArtifactPath("MixedCase", "local")).Should().BeFalse();
    }

    private static async Task SeedAsync(FileMemoryStore store, string name, string body)
    {
        string artifact = Nmp2ChunkedEncoder.Encode(body);
        await store.WriteArtifactAsync(name, "local", artifact);
        store.Upsert(new ArtifactEntry(
            name, store.ArtifactUri(name, "local"), body.Length, 1, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// <see cref="FileMemoryStore.CopyMemory"/>'s DESTINATION existence check asked the filesystem
    /// (<c>File.Exists(ArtifactPath(...))</c>) instead of resolving the destination through the index,
    /// the way the SOURCE side above it already did. On an ordinary NTFS volume that reads
    /// case-insensitively by accident and behaves correctly; on a case-sensitive filesystem, copying
    /// onto "Archive" while "archive" exists finds nothing, so the collision goes unreported and a
    /// SECOND memory is created.
    /// <para>This was the last open site of the case-sensitivity class. It was held back as a
    /// semantic question — are "archive" and "Archive" one memory or two? — and settled as ONE,
    /// matching every other name lookup in the store.</para>
    /// </summary>
    [SkippableFact]
    public async Task CopyMemory_ReportsACaseMismatchedDestinationAsAlreadyExisting()
    {
        Skip.If(_dir is null, "This host cannot provide a case-sensitive directory.");
        using var store = new FileMemoryStore(_dir!.Path);

        await SeedAsync(store, "archive", "the destination body");
        await SeedAsync(store, "notes", "the source body");

        bool copied = store.CopyMemory("notes", "Archive", overwrite: false, out string message);

        copied.Should().BeFalse(
            because: "'Archive' and 'archive' name the same memory, so this is a collision and must " +
                     "be reported as one rather than silently creating a second memory");
        message.Should().Contain("already exists");
        store.LoadIndex()
            .Where(e => e.Name.Equals("archive", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle(
                because: "a refused copy must leave exactly the one memory that was already there");
    }

    /// <summary>
    /// The overwrite half of the same fix, and the reason it is not merely a comparison change: the
    /// resolved destination name also decides WHERE the bytes land and what the new entry is called.
    /// Fixing only the existence check would still write "Archive.nmp2" beside "archive.nmp2" on a
    /// case-sensitive filesystem — a twin rather than a replacement. The existing memory keeps its
    /// own casing, matching the convention this codebase adopted for every other resolved name.
    /// </summary>
    [SkippableFact]
    public async Task CopyMemory_WithOverwrite_ReplacesTheCaseMismatchedDestinationRatherThanTwinningIt()
    {
        Skip.If(_dir is null, "This host cannot provide a case-sensitive directory.");
        using var store = new FileMemoryStore(_dir!.Path);

        await SeedAsync(store, "archive", "the destination body");
        await SeedAsync(store, "notes", "the source body");

        bool copied = store.CopyMemory("notes", "Archive", overwrite: true, out _);

        copied.Should().BeTrue();

        var matches = store.LoadIndex()
            .Where(e => e.Name.Equals("archive", StringComparison.OrdinalIgnoreCase)).ToList();
        matches.Should().ContainSingle(because: "the copy must replace the existing memory, not twin it");
        matches[0].Name.Should().Be("archive",
            because: "the memory that already existed keeps its own casing, as every other resolved " +
                     "name in this codebase now does");

        File.ReadAllText(store.ArtifactPath("archive", "local"))
            .Should().Be(File.ReadAllText(store.ArtifactPath("notes", "local")),
                because: "the destination's bytes must be the source's after an overwriting copy");
        File.Exists(store.ArtifactPath("Archive", "local")).Should().BeFalse(
            because: "no second artifact may be created under the caller's casing");
    }
}
