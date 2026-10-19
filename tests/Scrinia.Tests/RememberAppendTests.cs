using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.Tests.Shared;

namespace Scrinia.Tests;

public sealed class RememberAppendTests
{
    [Theory]
    [InlineData("/people/user/profile")]
    [InlineData("~profile")]
    [InlineData("/agent/profile")]
    [InlineData("/skill/custom-memory")]
    public async Task Remember_preserves_existing_facts_when_learning_additional_chunks(string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path, ["The apartment move took five hours.", "Prefers independent coffee shops."], description: "The apartment move took five hours.");
        string result = await tools.MemoryRemember(path, ["Takes the train to work.", "Enjoys audiobooks."], description: "Takes the train to work.");
        Assert.DoesNotContain("status: error", result);
        string recalled = await tools.MemoryRecall(path);
        Assert.Contains("five hours", recalled);
        Assert.Contains("independent coffee", recalled);
        Assert.Contains("train to work", recalled);
        Assert.Contains("audiobooks", recalled);
    }

    [Fact]
    public async Task Remember_keeps_chunks_independently_retrievable_and_merges_metadata()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/people/user/profile", ["The move took five hours."], description: "The move took five hours.",
            new() { ["person"] = ["alice"], ["tag"] = ["moving"] }, ["moving-duration"]);
        await tools.MemoryRemember("/people/user/profile",
            ["person: bob\nTakes the train.", "place: Paris\nEnjoys audiobooks."], description: "person: bob\nTakes the train.",
            new() { ["tag"] = ["commuting"] }, ["train-travel"]);
        Assert.Contains("five hours", await tools.MemoryRecall("/people/user/profile", chunk: 1));
        Assert.Contains("Takes the train", await tools.MemoryRecall("/people/user/profile", chunk: 2));
        Assert.Contains("Enjoys audiobooks", await tools.MemoryRecall("/people/user/profile", chunk: 3));
        var entry = TestHelpers.Store.LoadIndex(TestHelpers.Store.ResolveAddress(MemoryAddress.Parse("/people/user/profile")).Scope).Single();
        Assert.Contains("alice", entry.Attributes!["person"]);
        Assert.Contains("bob", entry.Attributes["person"]);
        Assert.Contains("Paris", entry.Attributes["place"]);
        Assert.Contains("moving", entry.Attributes["tag"]);
        Assert.Contains("commuting", entry.Attributes["tag"]);
        Assert.Contains("moving-duration", entry.Keywords!);
        Assert.Contains("train-travel", entry.Keywords!);
    }

    [Fact]
    public async Task Store_append_accepts_multiple_chunks_as_one_operation()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        var address = MemoryAddress.Parse("/batch");
        await store.StoreAsync(MemoryWrite.Create(address, ["old"], new Dictionary<string, IReadOnlyList<string>>(), []));
        await store.AppendAsync(MemoryWrite.Create(address, ["first", "second"], new Dictionary<string, IReadOnlyList<string>>(), []));
        string artifact = (await store.ReadArtifactAsync(address))!;
        Assert.Equal(3, Nmp2ChunkedEncoder.GetChunkCount(artifact));
        Assert.Equal("oldfirstsecond", System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(artifact)));
    }

    [Theory]
    [InlineData("/people/user/profile")]
    [InlineData("~profile")]
    [InlineData("/agent/profile")]
    [InlineData("/skill/custom-memory")]
    public async Task Replacement_requires_explicit_opt_in(string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path, ["obsolete fact"], description: "obsolete fact");
        await tools.MemoryRemember(path, ["new fact"], description: "new fact", replace: true);
        string recalled = await tools.MemoryRecall(path);
        Assert.Contains("new fact", recalled);
        Assert.DoesNotContain("obsolete fact", recalled);
    }

    [Fact]
    public async Task Batch_append_retains_tags_and_anchors_from_every_incoming_chunk()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/batch-tags", ["initial"], description: "initial");
        await tools.MemoryRemember("/batch-tags", ["tag: first\nanchor: first question", "tag: second\nanchor: second question"], description: "tag: first\nanchor: first question");
        var entry = TestHelpers.Store.LoadIndex("local").Single();
        Assert.Contains("first", entry.Attributes!["tag"]);
        Assert.Contains("second", entry.Attributes["tag"]);
        Assert.Contains("first question", entry.Attributes["anchor"]);
        Assert.Contains("second question", entry.Attributes["anchor"]);
    }

    [Theory]
    [InlineData("/batch-invalid")]
    [InlineData("/agent/invalid")]
    [InlineData("/skill/invalid")]
    public async Task Invalid_later_chunk_cannot_partially_append_earlier_chunks(string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember(path, ["original"], description: "original");
        string result = await tools.MemoryRemember(path, ["new partial content", null!], description: "new partial content");
        Assert.Contains("status: error", result);
        string recalled = await tools.MemoryRecall(path);
        Assert.Contains("original", recalled);
        Assert.DoesNotContain("new partial content", recalled);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("skill")]
    public async Task Missing_reserved_body_preserves_surviving_sidecar(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string path = $"/{kind}/orphan";
        await tools.MemoryRemember(path, ["original"], description: "original");
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", kind == "skill" ? "skills" : "agent");
        string bodyPath = Path.Combine(directory, "orphan.md");
        string metaPath = Path.Combine(directory, "orphan.meta.json");
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(metaPath))!;
        string createdAt = metadata["createdAt"]!.GetValue<string>();
        metadata["customField"] = "preserve me";
        await File.WriteAllTextAsync(metaPath, metadata.ToJsonString());
        File.Delete(bodyPath);

        string result = await tools.MemoryRemember(path, ["new body"], description: "new body");
        Assert.DoesNotContain("status: error", result);
        var updated = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(metaPath))!;
        Assert.Equal(createdAt, updated["createdAt"]!.GetValue<string>());
        Assert.Equal("preserve me", updated["customField"]!.GetValue<string>());
    }

    [Fact]
    public async Task Shared_replacement_requires_explicit_opt_in()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["obsolete fact"], description: "obsolete fact");
        await fixture.Tools.Remember("team", "/note", ["new fact"], description: "new fact", replace: true);
        string recall = await fixture.Restart().Recall("team", "/note");
        Assert.Contains("new fact", recall);
        Assert.DoesNotContain("obsolete fact", recall);
    }

    [Fact]
    public async Task Shared_remember_preserves_prior_content_across_restart()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["Move took five hours."], description: "Move took five hours.");
        string result = await fixture.Tools.Remember("team", "/note", ["Train commute.", "Audiobooks."], description: "Train commute.");
        Assert.Contains("mutation: applied", result);
        var restarted = fixture.Restart();
        string recall = await restarted.Recall("team", "/note");
        Assert.Contains("five hours", recall);
        Assert.Contains("Train commute", recall);
        Assert.Contains("Audiobooks", recall);
    }
}
