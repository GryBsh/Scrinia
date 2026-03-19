using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for the knowledge_add MCP tool:
/// KNOW-01: Storage and provenance keywords
/// KNOW-02: BM25 search scoped to bok
/// KNOW-03: Conflict detection advisory warning
/// KNOW-04: Standard search includes bok:* entries
/// ADOPT-02: Description context signals
/// </summary>
public sealed class KnowledgeToolTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public KnowledgeToolTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    private async Task InitProject()
    {
        await _tools.ProjectInit("Goals: test knowledge management", CancellationToken.None);
    }

    // ── KNOW-01 tests (storage, keywords, provenance) ─────────────────────────

    [Fact]
    public async Task KnowledgeAdd_StoresBokMemory()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "How to create MCP tools in .NET",
            "agent", "high",
            CancellationToken.None);

        // Assert — a bok:* entry must exist in bok scope index
        var store = MemoryStoreContext.Current!;
        var (bokScope, _) = store.ParseQualifiedName("bok:placeholder");
        var entries = store.LoadIndex(bokScope);
        entries.Should().Contain(e => e.Name.Contains("dotnet") || e.Name.Contains("mcp"),
            "knowledge_add should store a bok:* entry in the bok scope");
        entries.Should().HaveCountGreaterOrEqualTo(1,
            "knowledge_add should create at least one bok scope entry");
    }

    [Fact]
    public async Task KnowledgeAdd_StoresProvenanceKeywords()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "How to create MCP tools in .NET",
            "agent", "high",
            CancellationToken.None);

        // Assert — entry must have provenance keywords
        var store = MemoryStoreContext.Current!;
        var (bokScope, _) = store.ParseQualifiedName("bok:placeholder");
        var entries = store.LoadIndex(bokScope);
        entries.Should().Contain(e =>
            e.Keywords != null &&
            e.Keywords.Contains("source_type:agent", StringComparer.OrdinalIgnoreCase),
            "bok entry must have source_type:agent keyword");
        entries.Should().Contain(e =>
            e.Keywords != null &&
            e.Keywords.Contains("confidence:high", StringComparer.OrdinalIgnoreCase),
            "bok entry must have confidence:high keyword");
        entries.Should().Contain(e =>
            e.Keywords != null &&
            e.Keywords.Contains("domain:dotnet", StringComparer.OrdinalIgnoreCase),
            "bok entry must have domain:dotnet keyword");
    }

    [Fact]
    public async Task KnowledgeAdd_ContentIncludesProvenance()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "How to create MCP tools in .NET",
            "agent", "high",
            CancellationToken.None);

        // Assert — content must contain provenance header fields
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "bok:dotnet-mcp-tools");
        content.Should().ContainEquivalentOf("Domain: dotnet",
            "bok entry content must include 'Domain: dotnet'");
        content.Should().ContainEquivalentOf("Source: agent",
            "bok entry content must include 'Source: agent'");
        content.Should().ContainEquivalentOf("Confidence: high",
            "bok entry content must include 'Confidence: high'");
    }

    [Fact]
    public async Task KnowledgeAdd_ResponseConfirmsStorage()
    {
        // Arrange
        await InitProject();

        // Act
        string result = await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "How to create MCP tools in .NET",
            "agent", "high",
            CancellationToken.None);

        // Assert — response should start with "Stored as bok:dotnet-mcp-tools"
        result.Should().StartWith("Stored as bok:dotnet-mcp-tools",
            "knowledge_add response must confirm storage with the qualified name");
    }

    [Fact]
    public async Task KnowledgeAdd_ArchivesExistingVersion()
    {
        // Arrange — write the same bok entry twice
        await InitProject();
        await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "First version of MCP tools guide",
            "agent", "high",
            CancellationToken.None);

        // Get the versions dir path for bok scope
        var store = MemoryStoreContext.Current!;
        var (bokScope, bokSubject) = store.ParseQualifiedName("bok:dotnet-mcp-tools");
        string storeDir = store.GetStoreDirForScope(bokScope);
        // ArchiveVersion stores copies in {storeDir}/versions/
        string versionsDir = Path.Combine(storeDir, "versions");

        // Act — write again (same qualified name, archiveExisting: true)
        await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "Updated MCP tools guide",
            "agent", "high",
            CancellationToken.None);

        // Assert — a version archive file must exist for the subject
        bool versionsExist = Directory.Exists(versionsDir) &&
            Directory.GetFiles(versionsDir, $"{bokSubject}*").Length > 0;
        versionsExist.Should().BeTrue(
            "knowledge_add with archiveExisting:true should create a version archive on second write");
    }

    [Fact]
    public async Task KnowledgeAdd_RequiresProjectInit()
    {
        // Act — no project_init called
        string result = await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "Knowledge without project",
            "agent", "high",
            CancellationToken.None);

        // Assert
        result.Should().StartWith("Error:",
            "knowledge_add without project:context must return Error:");
    }

    [Fact]
    public async Task KnowledgeAdd_UpdatesProjectState()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "MCP tools guide",
            "agent", "high",
            CancellationToken.None);

        // Assert — project:state "Last action:" must contain "Knowledge added"
        var store = MemoryStoreContext.Current!;
        string stateContent = await ReadMemoryText(store, "project:state");
        stateContent.Should().ContainEquivalentOf("Knowledge added",
            "knowledge_add must update project:state Last action field");
    }

    // ── KNOW-02 tests (bok-scoped search) ─────────────────────────────────────

    [Fact]
    public async Task Search_WithBokScope_ReturnsBokEntries()
    {
        // Arrange
        await InitProject();
        await _tools.KnowledgeAdd(
            "dotnet", "mcp-tools",
            "How to create MCP tools in .NET using ModelContextProtocol library",
            "agent", "high",
            CancellationToken.None);

        // Act — search scoped to bok
        var store = MemoryStoreContext.Current!;
        var results = store.SearchAll("mcp tools", scopes: "bok", limit: 5);

        // Assert — results must contain the bok:* entry
        results.Should().NotBeEmpty("SearchAll with scopes='bok' should return bok:* entries");
        results.Should().ContainItemsAssignableTo<EntryResult>(
            "search results for bok scope should be EntryResult instances");
        results.OfType<EntryResult>().Should().Contain(
            r => r.Item.Scope.Contains("bok", StringComparison.OrdinalIgnoreCase),
            "search results must include bok-scoped entries");
    }

    // ── KNOW-03 tests (conflict detection advisory warning) ───────────────────

    [Fact]
    public async Task KnowledgeAdd_WarnsOnConflict()
    {
        // Arrange — store an initial bok entry for "mcp"
        await InitProject();
        await _tools.KnowledgeAdd(
            "dotnet", "mcp",
            "MCP protocol overview for .NET agents, model context protocol tools implementation",
            "agent", "high",
            CancellationToken.None);

        // Act — add a very similar entry with slug "mcp" in same domain
        string result = await _tools.KnowledgeAdd(
            "dotnet", "mcp",
            "Another MCP guide for .NET model context protocol tools",
            "research", "medium",
            CancellationToken.None);

        // Assert — response should contain "Warning:" substring
        result.Should().Contain("Warning:",
            "knowledge_add should warn when an existing bok entry covers the same topic");
    }

    [Fact]
    public async Task KnowledgeAdd_NoWarningForDistinctTopics()
    {
        // Arrange — store a bok entry for auth
        await InitProject();
        await _tools.KnowledgeAdd(
            "dotnet", "auth",
            "JWT authentication patterns for .NET applications",
            "agent", "high",
            CancellationToken.None);

        // Act — add a completely different bok entry
        string result = await _tools.KnowledgeAdd(
            "python", "deployment",
            "Docker deployment patterns for Python web services",
            "research", "medium",
            CancellationToken.None);

        // Assert — response must NOT contain "Warning:"
        result.Should().NotContain("Warning:",
            "knowledge_add should NOT warn when topics are clearly distinct");
    }

    [Fact]
    public async Task KnowledgeAdd_ConflictWarningIsAdvisory()
    {
        // Arrange — store an initial bok entry
        await InitProject();
        await _tools.KnowledgeAdd(
            "dotnet", "mcp",
            "MCP protocol overview for .NET agents model context protocol",
            "agent", "high",
            CancellationToken.None);

        // Act — add similar entry (conflict detected)
        await _tools.KnowledgeAdd(
            "dotnet", "mcp",
            "Additional MCP patterns for .NET agents model context protocol",
            "research", "medium",
            CancellationToken.None);

        // Assert — entry IS stored even when conflict is detected (warning is advisory only)
        var store = MemoryStoreContext.Current!;
        var (bokScope, _) = store.ParseQualifiedName("bok:placeholder");
        var entries = store.LoadIndex(bokScope);
        entries.Should().HaveCountGreaterOrEqualTo(1,
            "even when a conflict warning is issued, the bok entry must still be stored");
    }

    // ── KNOW-04 tests (standard search includes bok:* entries) ────────────────

    [Fact]
    public async Task StandardSearch_IncludesBokEntries()
    {
        // Arrange
        await InitProject();
        await _tools.KnowledgeAdd(
            "dotnet", "mcp",
            "Model context protocol implementation guide for .NET",
            "agent", "high",
            CancellationToken.None);

        // Act — SearchAll with no scopes filter
        var store = MemoryStoreContext.Current!;
        var results = store.SearchAll("model context protocol", scopes: null, limit: 20);

        // Assert — bok:* entries should appear in standard unfiltered search
        var entryResults = results.OfType<EntryResult>().ToList();
        entryResults.Should().NotBeEmpty(
            "standard SearchAll (no scopes filter) should return at least one EntryResult");

        bool bokFound = entryResults.Any(r =>
            r.Item.Scope.Contains("bok", StringComparison.OrdinalIgnoreCase));
        bokFound.Should().BeTrue(
            "bok:* entries must appear in standard SearchAll with no scopes filter (KNOW-04)");
    }

    [Fact]
    public async Task StandardSearch_BokVisibleWithoutOptIn()
    {
        // Arrange — add a bok entry and also a local entry
        await InitProject();
        await _tools.KnowledgeAdd(
            "dotnet", "mcp",
            "MCP protocol for .NET model context implementation",
            "agent", "high",
            CancellationToken.None);

        // Act — SearchAll with scopes: null (no explicit opt-in to bok)
        var store = MemoryStoreContext.Current!;
        var results = store.SearchAll("mcp protocol", scopes: null, limit: 20);

        // Assert — bok scope entries appear without explicit opt-in
        bool bokVisible = results.OfType<EntryResult>().Any(r =>
            r.Item.Scope.Contains("bok", StringComparison.OrdinalIgnoreCase));
        bokVisible.Should().BeTrue(
            "bok:* entries must be visible in SearchAll with scopes: null — no opt-in required (KNOW-04)");
    }

    // ── ADOPT-02 tests (description context signals) ──────────────────────────

    [Fact]
    public void KnowledgeAdd_DescriptionContainsContextSignals()
    {
        // Reflection test — [Description] on KnowledgeAdd must reference "search" and "bok"
        var method = typeof(ScriniaProjectTools).GetMethod("KnowledgeAdd");
        method.Should().NotBeNull("KnowledgeAdd method must exist");

        var descAttr = method!.GetCustomAttributes(
                typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("KnowledgeAdd must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().ContainEquivalentOf("search",
            "KnowledgeAdd description must reference 'search' so agents know how to query bok:*");
        descText.Should().ContainEquivalentOf("bok",
            "KnowledgeAdd description must reference 'bok' so agents know the topic convention");
    }
}
