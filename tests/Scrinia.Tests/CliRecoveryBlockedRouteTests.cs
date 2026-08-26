using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class CliRecoveryBlockedRouteTests
{
    [Fact]
    public async Task PersistentCliRoutes_ReturnRecoveryBlocked_WhenTheirScopeHasARetainedInvalidJournal()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string journalPath = WriteMalformedJournal(store);
        string contentPath = Path.Combine(scope.WorkspaceDir, "content.txt");
        await File.WriteAllTextAsync(contentPath, "content");

        var routes = new (string Name, Func<Task<int>> Invoke)[]
        {
            ("search", () => new ScriniaCommands().Search("note", scope.WorkspaceDir, scopes: "notes", json: true)),
            ("store", () => new ScriniaCommands().Store("/notes/note", contentPath, scope.WorkspaceDir, json: true)),
            ("show", () => new ScriniaCommands().Show("/notes/note", scope.WorkspaceDir, json: true)),
            ("forget", () => new ScriniaCommands().Forget("/notes/note", scope.WorkspaceDir, json: true)),
            ("append", () => new ScriniaCommands().Append("/notes/note", contentPath, scope.WorkspaceDir, json: true)),
            ("compact", () => new ScriniaCommands().Compact("/notes/note", workspaceRoot: scope.WorkspaceDir, json: true)),
            ("link", () => new ScriniaCommands().Link("/notes/note", "/notes/other", workspaceRoot: scope.WorkspaceDir, json: true)),
        };

        foreach (var route in routes)
        {
            var (exit, output) = await CaptureJsonAsync(route.Invoke);

            exit.Should().Be(1, route.Name);
            using var document = JsonDocument.Parse(output);
            McpResponseAssert response = ResponseParser.Parse(document.RootElement.GetProperty("yaml").GetString()!);
            response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked, route.Name);
            response.Error.Should().Contain("local:notes").And.Contain(journalPath);
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    [InlineData("replay-io")]
    public async Task ListFactsAndExport_ReturnTheMcpRecoveryEnvelopeAndRetainBlockedJournalBytes(string failureClass)
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string journalPath = WriteBlockedJournal(store, failureClass);
        byte[] originalJournal = File.ReadAllBytes(journalPath);

        if (failureClass == "replay-io")
            FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
                transition == RestoreJournalTransition.BeforeReplayBody
                    ? new IOException("Injected replay I/O failure.")
                    : null;

        try
        {
            McpResponseAssert expected = ResponseParser.Parse(
                await new ScriniaMcpTools().MemoryRecall("/notes/note"));
            expected.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);

            var routes = new (string Name, Func<Task<int>> Invoke)[]
            {
                ("list", () => new ScriniaCommands().List(scope.WorkspaceDir, scopes: "notes", json: true)),
                ("facts", () => new ScriniaCommands().Facts("entities", scopes: "notes", workspaceRoot: scope.WorkspaceDir, json: true)),
                ("export", () => new ScriniaCommands().Export("notes", workspaceRoot: scope.WorkspaceDir, json: true)),
            };

            foreach (var route in routes)
            {
                var (exit, output) = await CaptureJsonAsync(route.Invoke);

                exit.Should().Be(1, route.Name);
                using var document = JsonDocument.Parse(output);
                McpResponseAssert response = ResponseParser.Parse(document.RootElement.GetProperty("yaml").GetString()!);
                response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked, route.Name);
                response.Error.Should().Be(expected.Error, $"{route.Name} must preserve the MCP recovery envelope");
                File.ReadAllBytes(journalPath).Should().Equal(originalJournal,
                    because: $"{route.Name} must retain {failureClass} recovery evidence byte-for-byte");
            }
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }
    }

    private static string WriteMalformedJournal(FileMemoryStore store)
    {
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        File.WriteAllBytes(journalPath, Encoding.UTF8.GetBytes("{]"));
        return journalPath;
    }

    private static string WriteBlockedJournal(FileMemoryStore store, string failureClass)
    {
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        byte[] journal = failureClass switch
        {
            "malformed" => Encoding.UTF8.GetBytes("{]"),
            "unsupported" => Encoding.UTF8.GetBytes(
                """{"format":"scrinia.restore-journal","version":2,"state":"prepared","transactionId":"0123456789abcdef0123456789abcdef","scope":"local:notes","entries":[{"name":"note","bodyFile":"note.nmp2","bodyBase64":"AQ==","bodySha256":"4bf5122f344554c53bde2ebb8cd2b7e3d1600ad631c385a5d7c6f8a7c94e0c62","sidecarFile":"note.meta.json","sidecarBase64":"Ag==","sidecarSha256":"dbc1b4c900ffe48d575b5da5c638040125f65db0fe3e24494b76ea986457d986"}]}"""),
            "replay-io" => CreatePreparedJournal(),
            _ => throw new ArgumentOutOfRangeException(nameof(failureClass), failureClass, "Unknown recovery failure class.")
        };
        File.WriteAllBytes(journalPath, journal);
        return journalPath;
    }

    private static byte[] CreatePreparedJournal()
    {
        var entry = new ArtifactEntry("note", "file://bundle", 1, 1, DateTimeOffset.UtcNow);
        RestoreJournal journal = RestoreJournalCodec.CreatePrepared("local:notes",
            [new RestoreJournalPayload(entry, [1], [2])]);
        return RestoreJournalCodec.Serialize(journal);
    }

    private static async Task<(int Exit, string Output)> CaptureJsonAsync(Func<Task<int>> command)
    {
        var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            return (await command(), output.ToString());
        }
        finally
        {
            Console.SetOut(prior);
        }
    }
}
