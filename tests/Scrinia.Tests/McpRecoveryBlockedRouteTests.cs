using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class McpRecoveryBlockedRouteTests
{
    private const string BlockedScope = "local:notes";
    private const string RecoveryBlocked = "RECOVERY_BLOCKED";

    [Fact]
    public async Task PersistentMcpRoutes_ReturnRecoveryBlocked_WhenTheirScopeHasARetainedInvalidJournal()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string journalPath = WriteMalformedJournal(store);
        var tools = new ScriniaMcpTools();

        var routes = new (string Name, Func<Task<string>> Invoke)[]
        {
            ("remember", () => tools.MemoryRemember("/notes/note", ["content"])),
            ("recall", () => tools.MemoryRecall("/notes/note")),
            ("search", () => tools.MemorySearch("note", scopes: ["notes"])),
            ("list", () => tools.MemoryList(scopes: ["notes"])),
            ("append", () => tools.MemoryAppend("/notes/note", "content")),
            ("forget", () => tools.MemoryForget("/notes/note")),
            ("compact", () => tools.MemoryCompact("/notes/note")),
            ("link", () => tools.MemoryLink("/notes/note", "/notes/other")),
            ("facts", () => tools.Facts("entities", scopes: ["notes"])),
        };

        foreach (var route in routes)
        {
            McpResponseAssert response = ResponseParser.Parse(await route.Invoke());

            response.ErrorCode.Should().Be(RecoveryBlocked, route.Name);
            response.Error.Should().Contain(BlockedScope).And.Contain(journalPath);
            response.ActionNeeded.Should().ContainSingle()
                .Which.Should().Contain("Resolve the retained restore journal");
        }
    }

    [Fact]
    public async Task EphemeralMcpRoute_RemainsAvailable_WhenAPersistentScopeIsRecoveryBlocked()
    {
        using var scope = new TestHelpers.StoreScope();
        WriteMalformedJournal((FileMemoryStore)TestHelpers.Store);
        var tools = new ScriniaMcpTools();

        McpResponseAssert response = ResponseParser.Parse(await tools.MemoryRemember("~scratch", ["available"]));

        response.Status.Should().Be("success");
    }

    private static string WriteMalformedJournal(FileMemoryStore store)
    {
        string directory = store.GetStoreDirForScope(BlockedScope);
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        File.WriteAllBytes(journalPath, Encoding.UTF8.GetBytes("{]"));
        return journalPath;
    }
}
