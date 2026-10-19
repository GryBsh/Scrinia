using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Mcp;
using Scrinia.Mcp.SharedContracts;
using Scrinia.Tests.Shared;

namespace Scrinia.Tests;

public sealed class RememberDescriptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Public_schema_requires_description(bool shared)
    {
        object target = shared ? new SharedTools() : new ScriniaMcpTools();
        var method = target.GetType().GetMethod(shared ? "Remember" : "MemoryRemember")!;
        var schema = McpServerTool.Create(method, target,
            new() { SerializerOptions = ScriniaMcpTools.CreateToolSerializerOptions() }).ProtocolTool.InputSchema;
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), value => value.GetString() == "description");
    }

    [Theory]
    [InlineData("/notes/probe")]
    [InlineData("~probe")]
    [InlineData("/agent/probe")]
    [InlineData("/skill/probe")]
    public async Task Blank_description_is_rejected_before_mutation(string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        foreach (string? description in new string?[] { null, "", " \t\n" })
        {
            string result = await Write(tools, path, ["must not be stored"], description);
            Assert.Contains("INVALID_PARAMETER", result);
            Assert.Contains("description", result);
        }
        Assert.DoesNotContain("must not be stored", await tools.MemoryRecall(path));
    }

    [Theory]
    [InlineData("/notes/probe")]
    [InlineData("~probe")]
    [InlineData("/agent/probe")]
    [InlineData("/skill/probe")]
    public async Task Description_is_saved_on_create_append_and_replace(string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        Assert.DoesNotContain("status: error", await Write(tools, path, ["first evidence"], "Initial scope."));
        Assert.Equal("Initial scope.", await Description(scope, path));
        Assert.DoesNotContain("status: error", await Write(tools, path, ["second evidence"], "Expanded scope."));
        Assert.Equal("Expanded scope.", await Description(scope, path));
        string content = await tools.MemoryRecall(path);
        Assert.Contains("first evidence", content);
        Assert.Contains("second evidence", content);
        Assert.DoesNotContain("status: error", await Write(tools, path, ["replacement evidence"], "Replacement scope.", true));
        Assert.Equal("Replacement scope.", await Description(scope, path));
        content = await tools.MemoryRecall(path);
        Assert.Contains("replacement evidence", content);
        Assert.DoesNotContain("first evidence", content);
    }

    [Theory]
    [InlineData("/notes/probe")]
    [InlineData("/agent/probe")]
    [InlineData("/skill/probe")]
    public async Task Failed_append_preserves_previous_description_and_content(string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await Write(tools, path, ["Original evidence."], "Original scope.");
        if (path.StartsWith("/notes/"))
            FileMemoryStore.AppendFaultInjector = (point, _, _) => point == AppendFaultPoint.LiveSidecarWrite
                ? new InvalidOperationException("Injected sidecar publication failure.") : null;
        else
            new FileEntityTransactionCoordinator(scope.WorkspaceDir).FailOnceAt(FileEntityFaultPoint.MetaWrite);

        string result;
        try
        {
            result = await Write(tools, path, ["Uncommitted evidence."], "Uncommitted scope.");
        }
        finally
        {
            FileMemoryStore.AppendFaultInjector = null;
        }

        Assert.Contains("status: error", result);
        Assert.Equal("Original scope.", await Description(scope, path));
        string content = await tools.MemoryRecall(path);
        Assert.Contains("Original evidence.", content);
        Assert.DoesNotContain("Uncommitted evidence.", content);
    }

    [Theory]
    [InlineData("/note")]
    [InlineData("/skill/probe")]
    [InlineData("/agent/probe")]
    public async Task Shared_description_is_required_and_survives_restart(string path)
    {
        using var fixture = new SharedOperationFixture();
        Assert.Contains("description", await Write(fixture.Tools, path, ["invalid"], ""));
        Assert.DoesNotContain("invalid", await fixture.Tools.Recall("team", path));
        await Write(fixture.Tools, path, ["first evidence"], "Initial scope.");
        await Write(fixture.Tools, path, ["second evidence"], "Expanded scope.");
        var restarted = fixture.Restart();
        Assert.Contains("Expanded scope.", await restarted.List("team", mode: "full"));
        Assert.Contains("first evidence", await restarted.Recall("team", path));
        Assert.Contains("second evidence", await restarted.Recall("team", path));
        await Write(restarted, "/other", ["other evidence"], "Other scope.");
        await restarted.Link("team", path, "/other");
        Assert.Contains("Expanded scope.", await restarted.List("team", mode: "full"));
        await Write(restarted, path, ["replacement evidence"], "Replacement scope.", true);
        Assert.Contains("Replacement scope.", await restarted.List("team", mode: "full"));
        Assert.DoesNotContain("first evidence", await restarted.Recall("team", path));
    }

    [Fact]
    public async Task Chunk_search_shows_memory_description_and_retrievable_evidence()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await Write(tools, "/notes/trips",
            ["First trip was by train.", new string('x', 1000) + " A quartz specimen cost 35 dollars on the second trip."],
            "Trip activities, purchases, and costs.");
        string result = await tools.MemorySearch("quartz");
        Assert.Contains("/notes/trips", result);
        Assert.Contains("Trip activities, purchases, and costs.", result);
        Assert.Contains("excerpt:", result);
        Assert.Contains("Recall", result);
        Assert.Contains("35 dollars", await tools.MemoryRecall("/notes/trips"));
    }

    [Fact]
    public async Task Shared_chunk_search_exposes_description()
    {
        using var fixture = new SharedOperationFixture();
        await Write(fixture.Tools, "/trips", ["Train trip.", "A quartz specimen cost 35 dollars."],
            "Trip activities, purchases, and costs.");
        string result = await fixture.Tools.Search("quartz", "team");
        Assert.Contains("Trip activities, purchases, and costs.", result);
        Assert.Contains("35 dollars", result);
    }

    [Theory]
    [InlineData("/agent/probe", "/agent")]
    [InlineData("/skill/probe", "/skill")]
    public async Task Reserved_listing_exposes_description(string path, string listing)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await Write(tools, path, ["Detailed instructions."], "Review practices and scope.");
        Assert.Contains("Review practices and scope.", await tools.MemoryRecall(listing));
    }

    private static async Task<string> Write(object tools, string path, string[] content, string? description, bool replace = false)
    {
        var method = tools.GetType().GetMethod(tools is SharedTools ? "Remember" : "MemoryRemember")!;
        Assert.Contains(method.GetParameters(), parameter => parameter.Name == "description");
        var args = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "base" => (object)"team", "path" => path, "content" => content,
            "description" => description!, "replace" => replace,
            "cancellationToken" => CancellationToken.None, _ => parameter.DefaultValue
        }).ToArray();
        return await (Task<string>)method.Invoke(tools, args)!;
    }

    private static async Task<string?> Description(TestHelpers.StoreScope scope, string path)
    {
        if (path.StartsWith("/agent/") || path.StartsWith("/skill/"))
        {
            string directory = path.StartsWith("/skill/") ? "skills" : "agent";
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(scope.WorkspaceDir, ".scrinia", directory, "probe.meta.json")));
            return json.RootElement.GetProperty("description").GetString();
        }
        return (await TestHelpers.Store.ReadAsync(MemoryAddress.Parse(path)))!.ContentPreview;
    }
}
