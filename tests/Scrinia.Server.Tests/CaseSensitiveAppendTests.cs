using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Server.Models;
using Scrinia.Server.Services;
using Scrinia.TestDoubles;

namespace Scrinia.Server.Tests;

/// <summary>
/// The end-to-end OUTCOME guard for case-mismatched appends over the REST path: the index lookup
/// resolves identity before the artifact is read, and the read is given the entry's own casing.
/// The resolution itself now lives in <c>FileMemoryStore.AppendAsync</c> —
/// <see cref="MemoryOrchestrator.AppendAsync"/> hands it the write and shapes the response — and
/// <c>Scrinia.Tests.FileMemoryStoreWriteSurfaceTests.AppendAsync_ResolvesAMismatchedCasingRatherThanReplacingTheMemory</c>
/// pins it there directly. This test is what proves the whole server path still behaves, on a
/// filesystem where the defect is reproducible at all.
///
/// <para>On an ordinary NTFS volume the outcome is NOT observable, because <c>File.Exists</c> folds
/// case there. <see cref="CaseSensitiveDirectory"/> makes it observable: without the resolution,
/// <c>append("mixedcase", …)</c> against a memory stored as "MixedCase" missed the read, fell
/// through to the create-new path, and overwrote the memory with the appended chunk alone —
/// append() silently behaving as replace(). If the host cannot provide a case-sensitive directory,
/// this test SKIPS rather than silently passing.</para>
/// </summary>
public sealed class CaseSensitiveAppendTests : IDisposable
{
    private readonly CaseSensitiveDirectory? _dir;

    public CaseSensitiveAppendTests()
    {
        _dir = CaseSensitiveDirectory.TryCreate("scrinia-case-append");
    }

    public void Dispose() => _dir?.Dispose();

    [SkippableFact]
    public async Task AppendUnderADifferentCasing_AppendsRatherThanReplacesTheContent()
    {
        Skip.If(_dir is null, "This host cannot provide a case-sensitive directory.");
        using var store = new FileMemoryStore(_dir!.Path);

        await MemoryOrchestrator.StoreAsync(store,
            new StoreRequest(["the original body"], "MixedCase"));

        await MemoryOrchestrator.AppendAsync(store, "mixedcase", new AppendRequest("the appended body"));

        // The discriminator: with the bug present, the append falls through to the create-new path
        // and REPLACES the content with "the appended body" alone — the original is gone. Assert
        // content first, before anything else, because a CreatedAt-only assertion would pass with
        // the bug fully present (see below).
        string artifact = await store.ReadArtifactAsync("MixedCase", "local");
        byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
        string fullText = System.Text.Encoding.UTF8.GetString(bytes);
        fullText.Should().Contain("the original body",
            because: "a real append must preserve the original content, not replace it");
        fullText.Should().Contain("the appended body",
            because: "a real append must add the new content alongside the original");

        var entry = store.LoadIndex("local").Single(e => e.Name == "MixedCase");
        entry.ChunkCount.Should().Be(2,
            because: "the real append path ran — the fall-through replace path would have written a single chunk");

        // Supplementary only — NOT the discriminator. The fall-through replace path also
        // preserves CreatedAt (it looks up the existing entry before overwriting), so this
        // assertion is satisfied whether or not the bug is present. It is included only as an
        // additional sanity check, never as evidence the fix works.
        entry.CreatedAt.Should().NotBe(default);

        // No orphaned "mixedcase"-cased sidecar was created alongside "MixedCase" — the
        // create-new fall-through path would resolve to the same file on a case-insensitive
        // lookup, but on this case-sensitive filesystem a literal-name write would land on a
        // distinct path.
        File.Exists(store.ArtifactPath("mixedcase", "local")).Should().BeFalse();
        File.Exists(store.MetaPath("mixedcase", "local")).Should().BeFalse();
    }
}
