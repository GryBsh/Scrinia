using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class KaToolTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task Ka_EmptyStore_ReturnsNoMemoriesMessage()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Ka();

        result.Should().Be("No persistent memories found.");
    }

    [Fact]
    public async Task Ka_ReturnsInventoryWithAllMemoryNames()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Content about auth flow."], "api:auth-flow",
            description: "Auth flow details");
        await tools.Store(["Content about encoding."], "arch:encoding",
            description: "NMP/2 encoding details");
        await tools.Store(["Local notes."], "my-notes",
            description: "General notes");

        string result = await tools.Ka();

        result.Should().Contain("3 memories");
        result.Should().Contain("api:auth-flow");
        result.Should().Contain("arch:encoding");
        result.Should().Contain("my-notes");
    }

    [Fact]
    public async Task Ka_GroupsByTopic()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["API content."], "api:auth",
            description: "API auth");
        await tools.Store(["Arch content."], "arch:decisions",
            description: "Architecture decisions");

        string result = await tools.Ka();

        result.Should().Contain("Topic: api");
        result.Should().Contain("Topic: arch");
        result.Should().Contain("2 topics");
    }

    [Fact]
    public async Task Ka_ShowsSizeAndChunkCount()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Some content here."], "notes",
            description: "Simple notes");

        string result = await tools.Ka();

        result.Should().Contain("1 chunk");
        result.Should().MatchRegex(@"\d+(\.\d+)? (B|KB|MB)");
    }

    [Fact]
    public async Task Ka_ExcludesEphemeralMemories()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Persistent content."], "my-notes",
            description: "Persistent notes");
        await tools.Store(["Ephemeral scratch content."], "~scratch",
            description: "Scratch pad");

        string result = await tools.Ka();

        result.Should().Contain("1 memory");
        result.Should().Contain("my-notes");
        result.Should().NotContain("~scratch");
    }

    [Fact]
    public async Task Ka_WithScopeFilter_OnlyIncludesFilteredScope()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["API auth content."], "api:auth",
            description: "API auth");
        await tools.Store(["Arch decisions content."], "arch:decisions",
            description: "Architecture decisions");
        await tools.Store(["Local notes content."], "local-notes",
            description: "Local notes");

        string result = await tools.Ka(scopes: "api");

        result.Should().Contain("1 memory");
        result.Should().Contain("api:auth");
        result.Should().NotContain("arch:decisions");
        result.Should().NotContain("local-notes");
    }

    [Fact]
    public async Task Ka_ContainsAnalysisPlaybook()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Some content."], "notes", description: "Notes");

        string result = await tools.Ka();

        result.Should().Contain("Playbook");
        result.Should().Contain("show(");
        result.Should().Contain("Step 1");
        result.Should().Contain("Gap analysis");
        result.Should().Contain("Report to the user");
        result.Should().Contain("Quality checklist");
    }

    [Fact]
    public async Task Ka_DoesNotCreateEphemeralMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Some content."], "notes", description: "Notes");

        await tools.Ka();

        ScriniaArtifactStore.GetEphemeral("handoff").Should().BeNull();
        ScriniaArtifactStore.GetEphemeral("ka").Should().BeNull();
    }

    [Fact]
    public async Task Ka_IncludesDescriptionSnippets()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Content."], "api:auth-flow",
            description: "OAuth2 authorization code flow with PKCE");

        string result = await tools.Ka();

        result.Should().Contain("OAuth2 authorization code flow with PKCE");
    }

    [Fact]
    public async Task Ka_IncludesCreatedDate()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Content."], "notes", description: "Notes");

        string result = await tools.Ka();

        result.Should().MatchRegex(@"created \d{4}-\d{2}-\d{2}");
    }

    [Fact]
    public async Task Ka_IncludesTagsWhenPresent()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Content."], "api:auth",
            description: "Auth", tags: ["oauth", "security"]);

        string result = await tools.Ka();

        result.Should().Contain("[tags: oauth, security]");
    }

    [Fact]
    public async Task Ka_IncludesReviewMarkers()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Content."], "api:auth",
            description: "Auth", reviewWhen: "when auth system changes");

        string result = await tools.Ka();

        result.Should().Contain("[REVIEW:");
        result.Should().Contain("when auth system changes");
    }
}

public sealed class KtToolTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task Kt_EmptyStore_ReturnsNoMemoriesMessage()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Kt();

        result.Should().Be("No persistent memories found.");
    }

    [Fact]
    public async Task Kt_ListsDocumentsToProduceByTopic()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["API content."], "api:auth",
            description: "API auth");
        await tools.Store(["Arch content."], "arch:decisions",
            description: "Architecture decisions");
        await tools.Store(["Local notes."], "my-notes",
            description: "General notes");

        string result = await tools.Kt();

        result.Should().Contain("Documents to produce");
        result.Should().Contain("Topic: api");
        result.Should().Contain("Topic: arch");
        result.Should().Contain("Local Memories");
        result.Should().Contain("api:auth");
        result.Should().Contain("arch:decisions");
        result.Should().Contain("my-notes");
    }

    [Fact]
    public async Task Kt_InstructsToCallKaFirst()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Content."], "notes", description: "Notes");

        string result = await tools.Kt();

        result.Should().Contain("ka()");
        result.Should().Contain("Phase 1");
        result.Should().Contain("Analysis");
    }

    [Fact]
    public async Task Kt_InstructsMultipleDocuments()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Content."], "notes", description: "Notes");

        string result = await tools.Kt();

        result.Should().Contain("one KT document per topic");
        result.Should().Contain("transfer index");
        result.Should().Contain("self-contained");
    }

    [Fact]
    public async Task Kt_ExcludesEphemeralMemories()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Persistent content."], "my-notes",
            description: "Persistent notes");
        await tools.Store(["Ephemeral content."], "~scratch",
            description: "Scratch");

        string result = await tools.Kt();

        result.Should().Contain("1 memory");
        result.Should().Contain("my-notes");
        result.Should().NotContain("~scratch");
    }

    [Fact]
    public async Task Kt_WithScopeFilter_OnlyIncludesFilteredScope()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["API content."], "api:auth",
            description: "API auth");
        await tools.Store(["Local notes."], "local-notes",
            description: "Local notes");

        string result = await tools.Kt(scopes: "api");

        result.Should().Contain("1 memory");
        result.Should().Contain("api:auth");
        result.Should().NotContain("local-notes");
    }

    [Fact]
    public async Task Kt_ContainsQualityChecklist()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();

        await tools.Store(["Content."], "notes", description: "Notes");

        string result = await tools.Kt();

        result.Should().Contain("Quality checklist");
        result.Should().Contain("ka() was called");
    }
}
