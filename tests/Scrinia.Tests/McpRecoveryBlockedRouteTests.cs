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
        string scopeDirectory = store.GetStoreDirForScope(BlockedScope);
        using (FileLock.AcquireShared(Path.Combine(scopeDirectory, ".lock")))
        {
        }
        byte[] originalJournal = File.ReadAllBytes(journalPath);
        ScopeSnapshot originalScope = SnapshotScope(scopeDirectory);
        var tools = new ScriniaMcpTools();

        var routes = new (string Name, Func<Task<string>> Invoke)[]
        {
            ("remember", () => tools.MemoryRemember("/notes/note", ["content"])),
            ("replace", () => tools.MemoryRemember("/notes/note", ["content"], replace: true)),
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

            response.Status.Should().Be("error", route.Name);
            response.ErrorCode.Should().Be(RecoveryBlocked, route.Name);
            response.Scope.Should().Be(BlockedScope, route.Name);
            response.JournalPath.Should().Be(journalPath, route.Name);
            response.Error.Should().NotContain("An error occurred invoking", route.Name);
            response.ActionNeeded.Should().ContainSingle()
                .Which.Should().Contain("Resolve the retained restore journal");
            if (route.Name is "append" or "remember")
                response.OperationId.Should().NotBeNullOrWhiteSpace(route.Name);
            else
                response.OperationId.Should().BeNull(route.Name);

            File.ReadAllBytes(journalPath).Should().Equal(originalJournal, route.Name);
            SnapshotScope(scopeDirectory).Should().BeEquivalentTo(originalScope, route.Name);
        }
    }

    [Fact]
    public async Task EphemeralMcpRoute_RemainsAvailable_WhenAPersistentScopeIsRecoveryBlocked()
    {
        using var scope = new TestHelpers.StoreScope();
        string journalPath = WriteMalformedJournal((FileMemoryStore)TestHelpers.Store);
        byte[] originalJournal = File.ReadAllBytes(journalPath);
        var tools = new ScriniaMcpTools();

        McpResponseAssert response = ResponseParser.Parse(await tools.MemoryRemember("~scratch", ["available"]));

        response.Status.Should().Be("success");
        File.ReadAllBytes(journalPath).Should().Equal(originalJournal);
    }

    [Fact]
    public async Task AppendAdmission_ReturnsTypedRecoveryBlockedEnvelope_ForRetainedAppendJournal()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string versions = Path.Combine(store.GetStoreDirForScope(BlockedScope), "versions");
        Directory.CreateDirectory(versions);
        string journalPath = Path.Combine(versions, ".append-journal.json");
        byte[] originalJournal = Encoding.UTF8.GetBytes("{]");
        File.WriteAllBytes(journalPath, originalJournal);

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryAppend("/notes/note", "content"));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(RecoveryBlocked);
        response.Scope.Should().Be(BlockedScope);
        response.JournalPath.Should().Be(journalPath);
        response.OperationId.Should().NotBeNullOrWhiteSpace();
        response.Error.Should().NotContain("An error occurred invoking");
        response.ActionNeeded.Should().ContainSingle()
            .Which.Should().Contain("Resolve the retained append journal");
        File.ReadAllBytes(journalPath).Should().Equal(originalJournal);
    }

    [Fact]
    public async Task UnsupportedRestoreVersion_UsesTypedVersion_NotRecoveryMessageParsing()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string directory = store.GetStoreDirForScope(BlockedScope);
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        File.WriteAllText(journalPath,
            "{\"format\":\"scrinia.restore-journal\",\"version\":99,\"state\":\"prepared\",\"transactionId\":\"0123456789abcdef0123456789abcdef\",\"scope\":\"local:notes\",\"entries\":[{\"name\":\"note\",\"bodyFile\":\"note.nmp2\",\"bodyBase64\":\"AQ==\",\"bodySha256\":\"4bf5122f344554c53bde2ebb8cd2b7e3d1600ad631c385a5d7c6f8a7c94e0c62\",\"sidecarFile\":\"note.meta.json\",\"sidecarBase64\":\"Ag==\",\"sidecarSha256\":\"dbc1b4c900ffe48d575b5da5c638040125f65db0fe3e24494b76ea986457d986\"}]}");

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryRecall("/notes/note"));

        response.Error.Should().Contain("restore journal version 99");
        response.ErrorCode.Should().Be(RecoveryBlocked);
    }

    private static string WriteMalformedJournal(FileMemoryStore store)
    {
        string directory = store.GetStoreDirForScope(BlockedScope);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "note.nmp2"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(directory, "note.meta.json"), "{\"name\":\"note\"}");
        File.WriteAllText(Path.Combine(directory, "index.json"), "{\"entries\":[]}");
        File.WriteAllText(Path.Combine(directory, "metadata.json"), "{\"scope\":\"local:notes\"}");
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        File.WriteAllBytes(journalPath, Encoding.UTF8.GetBytes("{]"));
        return journalPath;
    }

    private static ScopeSnapshot SnapshotScope(string directory)
    {
        string[] files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var fileBytes = files.ToDictionary(
            path => Path.GetRelativePath(directory, path),
            File.ReadAllBytes,
            StringComparer.Ordinal);
        string[] directories = Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new ScopeSnapshot(fileBytes, directories);
    }

    private sealed record ScopeSnapshot(
        Dictionary<string, byte[]> Files,
        string[] Directories);
}
