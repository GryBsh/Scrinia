using System.Runtime.CompilerServices;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests.Golden;

/// <summary>
/// Characterization tests. These assert nothing about whether the output is GOOD — only that
/// it is UNCHANGED. They exist to make the ToYaml-at-the-boundary refactor provably
/// behaviour-preserving.
///
/// To re-baseline after an INTENTIONAL output change, set SCRINIA_UPDATE_GOLDEN=1 and re-run.
/// During the response-shape refactor, needing to re-baseline means the refactor changed
/// behaviour — investigate, do not re-baseline.
///
/// `guide` and `restore` are deliberately excluded: both fold live workspace state
/// (available skills, today's session log) into the body, so they are pinned by their own
/// structural tests instead.
/// </summary>
public class ResponseGoldenTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Seeds both the index sidecar (via <see cref="IMemoryStore.Upsert"/>, read by list/search/
    /// facts/link/forget) AND the backing NMP/2 artifact file (via <see
    /// cref="IMemoryStore.WriteArtifactAsync"/>, read by recall/append/compact). A sidecar-only
    /// seed leaves recall/append/compact hitting their not-found/fallback-create paths instead of
    /// their real success paths — Upsert never writes chunk content, only metadata — which would
    /// pin the wrong behavior for those three golden cases.
    /// </summary>
    private static async Task Seed(IMemoryStore store)
    {
        store.Upsert(new ArtifactEntry("auth-flow", "uri", 2048, 1, Fixed,
            ContentPreview: "How session rotation works.",
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["topic"] = ["auth", "security"],
                ["person"] = ["jolene"],
            }), "local:findings");
        await store.WriteArtifactAsync("auth-flow", "local:findings",
            Nmp2ChunkedEncoder.Encode("How session rotation works."));

        store.Upsert(new ArtifactEntry("retry", "uri", 1024, 1, Fixed,
            ContentPreview: "Exponential backoff with jitter.",
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["topic"] = ["resilience"],
            }), "local:patterns");
        await store.WriteArtifactAsync("retry", "local:patterns",
            Nmp2ChunkedEncoder.Encode("Exponential backoff with jitter."));
    }

    /// <summary>
    /// Extra fixture entry used only by the search / search-filtered golden cases (and the
    /// divergence assertion below). "session rotation" — the query both cases run — otherwise
    /// only relevance-matches <c>/findings/auth-flow</c>, so an unfiltered search and a
    /// pathPrefix: '/findings/' filtered search returned the identical single hit: the filter had
    /// nothing to remove, so a refactor that dropped pathPrefix handling entirely would still
    /// pass both goldens. This entry sits outside /findings/ but shares the query's terms, so
    /// unfiltered search returns 2 hits and the filtered one narrows to 1 — the two goldens can
    /// now actually diverge. Kept out of the shared <see cref="Seed"/> so the other 12 cases keep
    /// pinning exactly the 2-entry fixture they always have.
    /// </summary>
    private static async Task SeedSearchDivergenceCase(IMemoryStore store)
    {
        store.Upsert(new ArtifactEntry("log-rotation", "uri", 512, 1, Fixed,
            ContentPreview: "Session rotation logs are archived nightly.",
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["topic"] = ["ops"],
            }), "local:ops");
        await store.WriteArtifactAsync("log-rotation", "local:ops",
            Nmp2ChunkedEncoder.Encode("Session rotation logs are archived nightly."));
    }

    /// <summary>
    /// Seeds an entry whose content exceeds ResponseBuilder's ~7992-char truncation cap, so
    /// recall-truncated exercises the <c>ContentTruncated</c> followUp entry that <c>ToYaml</c>
    /// appends — no existing fixture's content is anywhere near the cap.
    /// </summary>
    private static async Task SeedTruncationCase(IMemoryStore store)
    {
        string longText = string.Concat(Enumerable.Repeat(
            "This paragraph repeats to push recalled content past the response truncation cap. ", 120));
        store.Upsert(new ArtifactEntry("long-note", "uri", longText.Length, 1, Fixed,
            ContentPreview: "A very long note.",
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["topic"] = ["verbose"],
            }), "local:findings");
        await store.WriteArtifactAsync("long-note", "local:findings",
            Nmp2ChunkedEncoder.Encode(longText));
    }

    /// <summary>
    /// Seeds a genuinely multi-chunk NMP/2 artifact (via <see cref="Nmp2ChunkedEncoder.EncodeChunks"/>)
    /// so compact-multichunk exercises the merge-all branch instead of the "already a single
    /// chunk, nothing to compact" no-op that <c>compact.yaml</c> pins.
    /// </summary>
    private static async Task SeedMultiChunkCase(IMemoryStore store)
    {
        string[] chunks =
        [
            "First chunk of the multi-part note.",
            "Second chunk of the multi-part note.",
            "Third chunk of the multi-part note.",
        ];
        store.Upsert(new ArtifactEntry("multi-note", "uri", chunks.Sum(c => c.Length), chunks.Length, Fixed,
            ContentPreview: "A multi-chunk note.",
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["topic"] = ["multipart"],
            }), "local:findings");
        await store.WriteArtifactAsync("multi-note", "local:findings",
            Nmp2ChunkedEncoder.EncodeChunks(chunks));
    }

    public static TheoryData<string> Cases() =>
    [
        "remember", "recall", "search", "search-filtered",
        "list-summary", "list-full", "append", "forget",
        "compact", "link", "reconcile-scan", "facts-entities",
        "error-missing-path", "error-empty-content",
        "update-warning", "recall-truncated", "compact-multichunk", "link-missing-side",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Response_MatchesGolden(string name)
    {
        using var scope = new TestHelpers.StoreScope();
        await Seed(MemoryStoreContext.Current!);
        if (name is "search" or "search-filtered")
            await SeedSearchDivergenceCase(MemoryStoreContext.Current!);
        if (name == "recall-truncated")
            await SeedTruncationCase(MemoryStoreContext.Current!);
        if (name == "compact-multichunk")
            await SeedMultiChunkCase(MemoryStoreContext.Current!);
        var tools = new ScriniaMcpTools();

        string yaml = name switch
        {
            "remember"        => await tools.MemoryRemember("/findings/new-note", ["first chunk", "second chunk"]),
            "recall"          => await tools.MemoryRecall("/findings/auth-flow"),
            "search"          => await tools.MemorySearch("session rotation"),
            "search-filtered" => await tools.MemorySearch("session rotation", pathPrefix: "/findings/"),
            "list-summary"    => await tools.MemoryList(),
            "list-full"       => await tools.MemoryList(mode: "full"),
            "append"          => await tools.MemoryAppend("/findings/auth-flow", "an appended chunk"),
            "forget"          => await tools.MemoryForget("/patterns/retry"),
            "compact"         => await tools.MemoryCompact("/findings/auth-flow"),
            "link"            => await tools.MemoryLink("/findings/auth-flow", "/patterns/retry"),
            "reconcile-scan"  => await tools.MemoryReconcile(),
            "facts-entities"  => await tools.Facts("entities"),
            "error-missing-path" => await tools.MemoryRecall(""),
            "error-empty-content" => await tools.MemoryRemember("/findings/x", []),
            // No changes specified: UpdateMeta's changes.Count == 0 branch (MemoryTools.Core.cs)
            // is the cleanest of the three warning producers to drive — it needs no new seed
            // state (auth-flow already exists via Seed()) and no filesystem/formatter setup,
            // unlike an empty bundle import or a facts() entity lookup that matches nothing.
            "update-warning"  => (await tools.UpdateMeta(MemoryAddress.Parse("/findings/auth-flow"))).ToYaml(),
            "recall-truncated" => await tools.MemoryRecall("/findings/long-note"),
            "compact-multichunk" => await tools.MemoryCompact("/findings/multi-note"),
            // link's refusal envelope for a side that does not exist — no seed helper needed, an
            // unresolvable `from` is enough.
            //
            // This case was named "link-partial-failure" and pinned the OPPOSITE property: link
            // used to run both metadata writes unconditionally, so this same call reported
            // "Partial link failure" WHILE the golden recorded "/patterns/retry: 1 attribute(s)
            // merged" — the dangling one-sided ref that link's own comment claimed was prevented.
            // Both sides are checked before either is written now, so the answer is a clean
            // NOT_FOUND and nothing is written; the old name would have described neither the call
            // nor the response.
            "link-missing-side" => await tools.MemoryLink("/findings/does-not-exist", "/patterns/retry"),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown golden case"),
        };

        string actual = GoldenNormalizer.Normalize(yaml);
        string path = GoldenPath(name);

        if (Environment.GetEnvironmentVariable("SCRINIA_UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        string expected = GoldenNormalizer.Normalize(File.ReadAllText(path));
        actual.Should().Be(expected,
            $"golden '{name}' must be byte-identical; set SCRINIA_UPDATE_GOLDEN=1 only for an intentional change");
    }

    /// <summary>
    /// A golden pair whose whole purpose is to differ (search vs. search-filtered) should fail
    /// loudly, not just silently stop being useful, if a refactor ever makes pathPrefix filtering
    /// a no-op. This does not replace the two golden files above — it is the guard that keeps
    /// them meaningful, independent of whatever their pinned content happens to be.
    /// </summary>
    [Fact]
    public async Task SearchAndSearchFiltered_ProduceDifferentOutput()
    {
        using var scope = new TestHelpers.StoreScope();
        await Seed(MemoryStoreContext.Current!);
        await SeedSearchDivergenceCase(MemoryStoreContext.Current!);
        var tools = new ScriniaMcpTools();

        string search = GoldenNormalizer.Normalize(await tools.MemorySearch("session rotation"));
        string searchFiltered = GoldenNormalizer.Normalize(
            await tools.MemorySearch("session rotation", pathPrefix: "/findings/"));

        search.Should().NotBe(searchFiltered,
            "pathPrefix must actually narrow the result set — if these two ever match, " +
            "the filter silently stopped being applied and both golden files would still pass");
    }

    /// <summary>Golden files live next to the source, not in bin/ — they are committed artifacts.</summary>
    private static string GoldenPath(string name, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "__snapshots__", $"{name}.yaml");
}
