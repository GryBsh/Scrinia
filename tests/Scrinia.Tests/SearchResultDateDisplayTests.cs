using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Task 3: search result blocks render a display-only <c>created:</c>/<c>updated:</c>
/// pseudo-attribute derived from <see cref="ArtifactEntry.CreatedAt"/>/<see cref="ArtifactEntry.UpdatedAt"/>
/// — never stored in <see cref="ArtifactEntry.Attributes"/>, never agent-writable, and never
/// tokenized into BM25/embeddings (it's rendered after search has already run).
/// </summary>
public sealed class SearchResultDateDisplayTests
{
    [Fact]
    public async Task Search_result_block_shows_created_pseudo_attribute()
    {
        using var storeScope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        store.Upsert(new ArtifactEntry(
            Name: "pottery-notes",
            Uri: "stub://pottery-notes",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: new DateTimeOffset(2023, 5, 3, 0, 0, 0, TimeSpan.Zero),
            ContentPreview: "notes about pottery glazing techniques"));

        var tools = new ScriniaMcpTools();
        string body = (await tools.Search(query: "pottery")).ToYaml();

        body.Should().MatchRegex(@"created: \d{4}-\d{2}-\d{2}");
        body.Should().NotContain("\"created\"", because: "the date is display-only, never a stored attribute key");
    }

    [Fact]
    public async Task Search_result_block_shows_updated_pseudo_attribute_when_present()
    {
        using var storeScope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        store.Upsert(new ArtifactEntry(
            Name: "kiln-notes",
            Uri: "stub://kiln-notes",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: new DateTimeOffset(2023, 5, 3, 0, 0, 0, TimeSpan.Zero),
            ContentPreview: "notes about kiln firing schedules",
            UpdatedAt: new DateTimeOffset(2024, 1, 9, 0, 0, 0, TimeSpan.Zero)));

        var tools = new ScriniaMcpTools();
        string body = (await tools.Search(query: "kiln")).ToYaml();

        body.Should().MatchRegex(@"created: 2023-05-03");
        body.Should().MatchRegex(@"updated: 2024-01-09");
    }

    [Fact]
    public async Task Search_result_block_omits_updated_line_when_UpdatedAt_is_null()
    {
        using var storeScope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        store.Upsert(new ArtifactEntry(
            Name: "glaze-notes",
            Uri: "stub://glaze-notes",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: new DateTimeOffset(2023, 5, 3, 0, 0, 0, TimeSpan.Zero),
            ContentPreview: "notes about glaze chemistry",
            UpdatedAt: null));

        var tools = new ScriniaMcpTools();
        string body = (await tools.Search(query: "glaze")).ToYaml();

        body.Should().Contain("created: 2023-05-03");
        body.Should().NotContain("updated:");
    }

    /// <summary>
    /// Index-leak guard: the created-date pseudo-attribute is rendered on the way OUT (after
    /// search has already run), never fed into BM25 tokenization or the embedding index. A memory
    /// whose text contains no date must not be found by searching for its own created-date string.
    /// </summary>
    [Fact]
    public async Task Search_by_created_date_string_does_not_match_undated_memory_text()
    {
        using var storeScope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        var createdAt = new DateTimeOffset(2023, 5, 3, 0, 0, 0, TimeSpan.Zero);
        store.Upsert(new ArtifactEntry(
            Name: "undated-memory",
            Uri: "stub://undated-memory",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: createdAt,
            ContentPreview: "a note with no date mentioned anywhere in its text"));

        var tools = new ScriniaMcpTools();
        string body = (await tools.Search(query: createdAt.ToString("yyyy-MM-dd"))).ToYaml();

        body.Should().Contain("No matching memories found.");
    }
}
