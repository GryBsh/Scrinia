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
    /// A case-sensitive filesystem can hold BOTH "Note.meta.json" and "note.meta.json" — the state
    /// <c>ResolveSidecarPath</c>'s ordinal tie-break exists to decide between. <c>Remove</c> deletes
    /// exactly ONE of them, so exactly one entry may leave the index cache.
    /// <para>Its copy-on-write loop originally <c>continue</c>d on every case-insensitive match and
    /// so evicted both twins. Before the write path stopped re-reading the scope that was invisible
    /// (the next write reloaded the survivor from disk); now nothing does, so the survivor's sidecar
    /// sits on disk unreachable through <c>LoadIndex</c>/search/list for the rest of the process's
    /// life. The assertion that matters is the WARM one — a cold store re-reads the directory and
    /// would pass either way.</para>
    /// </summary>
    [SkippableFact]
    public void Remove_WithACaseTwinPresent_DropsOnlyTheDeletedEntryFromTheCache()
    {
        Skip.If(_dir is null, "This host cannot provide a case-sensitive directory.");
        using var store = new FileMemoryStore(_dir!.Path);

        store.Upsert(new ArtifactEntry("Note", "uri-upper", 1, 1, DateTimeOffset.UtcNow));
        store.Upsert(new ArtifactEntry("note", "uri-lower", 1, 1, DateTimeOffset.UtcNow));

        // Warm the cache with BOTH twins. Writes against an uncached scope leave it uncached, so
        // without this read the Remove below would take the leave-absent branch and the eviction
        // loop under test would never run.
        store.LoadIndex().Should().HaveCount(2,
            because: "the setup only reproduces the scenario if the filesystem really kept two "
                   + "separately-cased sidecars");

        // Neither casing exactly: forces ResolveSidecarPath's fallback scan, which sorts the
        // candidates ordinally and takes the first — uppercase, so "Note.meta.json".
        store.Remove("NOTE").Should().BeTrue();

        store.LoadIndex().Should().ContainSingle(
            because: "one sidecar was deleted, so exactly one entry may leave the cache — "
                   + "evicting both twins strands the survivor's sidecar on disk, invisible to "
                   + "every reader for the process lifetime");

        using var cold = new FileMemoryStore(_dir!.Path);
        cold.LoadIndex().Should().ContainSingle(
            because: "disk and the warm cache must agree on how many memories survived");
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
}
