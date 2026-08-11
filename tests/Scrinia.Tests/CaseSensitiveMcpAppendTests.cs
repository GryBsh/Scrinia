using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// The MCP-side counterpart to <see cref="Scrinia.Server.Tests.CaseSensitiveAppendTests"/>: pins
/// the OUTCOME of <see cref="ScriniaMcpTools.MemoryAppend"/> under a case-mismatched name, using a
/// <see cref="FileMemoryStore"/> rooted in a <see cref="CaseSensitiveDirectory"/> rather than
/// <see cref="TestHelpers.StoreScope"/>'s ordinary temp directory (which is case-insensitive on
/// Windows, where the defect this guards cannot occur). Complements — does not replace —
/// <see cref="RememberWriteMetadataTests.Append_UnderADifferentCasing_ReadsTheArtifactUnderTheEntrysOwnCasing"/>,
/// which pins the ORDER of the index-lookup-before-read hoist and is the only guard that fires on
/// an ordinary (case-insensitive) host; this test pins the OUTCOME, observable only here. If the
/// host cannot provide a case-sensitive directory, this test SKIPS rather than silently passing.
/// <see cref="MemoryStoreContext.Current"/> is an <c>AsyncLocal&lt;IMemoryStore?&gt;</c>, so pointing
/// it at this store is parallel-safe alongside every other test in the suite.
/// </summary>
public sealed class CaseSensitiveMcpAppendTests : IDisposable
{
    private readonly CaseSensitiveDirectory? _dir;

    public CaseSensitiveMcpAppendTests()
    {
        _dir = CaseSensitiveDirectory.TryCreate("scrinia-case-mcp-append");
    }

    public void Dispose()
    {
        _dir?.Dispose();
        MemoryStoreContext.Current = null;
    }

    [SkippableFact]
    public async Task Append_UnderADifferentCasing_AppendsRatherThanReplacesTheContent()
    {
        Skip.If(_dir is null, "This host cannot provide a case-sensitive directory.");
        using var store = new FileMemoryStore(_dir!.Path);
        MemoryStoreContext.Current = store;

        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path: "MixedCase", content: ["the original body"]);

        await tools.MemoryAppend(path: "mixedcase", content: "the appended body");

        // The discriminator: with the bug present, the append falls through to the Remember()
        // create path and REPLACES the content with "the appended body" alone — the original is
        // gone. Assert content first, before anything else, because a CreatedAt-only assertion
        // would pass with the bug fully present (StoreAsync/Remember preserve CreatedAt on the
        // fall-through path too).
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

        // Supplementary only — NOT the discriminator. See the class doc comment.
        entry.CreatedAt.Should().NotBe(default);

        // No orphaned "mixedcase"-cased sidecar was created alongside "MixedCase".
        File.Exists(store.ArtifactPath("mixedcase", "local")).Should().BeFalse();
        File.Exists(store.MetaPath("mixedcase", "local")).Should().BeFalse();
    }
}
