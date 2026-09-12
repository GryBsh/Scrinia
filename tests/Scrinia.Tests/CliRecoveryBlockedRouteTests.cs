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
        string scopeDirectory = store.GetStoreDirForScope("local:notes");
        using (FileLock.AcquireShared(Path.Combine(scopeDirectory, ".lock")))
        {
        }
        ScopeSnapshot originalScope = SnapshotScope(scopeDirectory);
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
            document.RootElement.TryGetProperty("yaml", out JsonElement yaml).Should().BeTrue($"{route.Name}: {output}");
            McpResponseAssert response = ResponseParser.Parse(yaml.GetString()!);
            response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked, route.Name);
            response.Status.Should().Be("error", route.Name);
            response.Scope.Should().Be("local:notes", route.Name);
            response.JournalPath.Should().Be(journalPath, route.Name);
            response.Reason.Should().Be("malformed", route.Name);
            response.ActionNeeded.Should().ContainSingle()
                .Which.Should().Contain("Resolve the retained restore journal", route.Name);
            if (route.Name == "append")
            {
                response.OperationId.Should().MatchRegex("^[0-9a-f]{32}$", route.Name);
            }
            else
                response.OperationId.Should().BeNull(route.Name);
            response.Error.Should().Contain("local:notes").And.Contain(journalPath);
            File.ReadAllBytes(journalPath).Should().Equal(Encoding.UTF8.GetBytes("{]"), route.Name);
            SnapshotScope(scopeDirectory).Should().BeEquivalentTo(originalScope, route.Name);
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
                response.Status.Should().Be("error", route.Name);
                response.Scope.Should().Be("local:notes", route.Name);
                response.JournalPath.Should().Be(journalPath, route.Name);
                response.Reason.Should().Be(failureClass switch
                {
                    "malformed" => "malformed",
                    "unsupported" => "unsupportedVersion",
                    "replay-io" => "replayIoFailed",
                    _ => throw new ArgumentOutOfRangeException(nameof(failureClass), failureClass)
                }, route.Name);
                response.ActionNeeded.Should().ContainSingle()
                    .Which.Should().Contain("Resolve the retained restore journal", route.Name);
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

    [Fact]
    public async Task Append_UnrelatedInternalFailure_IsNotPromotedByAnUnrelatedRestoreJournalFile()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string directory = store.GetStoreDirForScope("local:notes");
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        string contentPath = Path.Combine(scope.WorkspaceDir, "content.txt");
        await File.WriteAllTextAsync(contentPath, "content");

        FileMemoryStore.AppendFaultInjector = (point, _, _) =>
        {
            if (point != AppendFaultPoint.RawArtifactRead)
                return null;

            Directory.CreateDirectory(directory);
            File.WriteAllBytes(journalPath, Encoding.UTF8.GetBytes("unrelated evidence"));
            FileMemoryStore.AppendFaultInjector = null;
            return new IOException("Injected unrelated append failure.");
        };

        var (exit, output) = await CaptureJsonAsync(
            () => new ScriniaCommands().Append("/notes/note", contentPath, scope.WorkspaceDir, json: true));

        exit.Should().Be(1);
        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("error").GetString()
            .Should().Be("Append failed after establishing its operation boundary.");
        document.RootElement.TryGetProperty("yaml", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Append_ReplayIoRecoveryWithoutDiscoveryBlock_PreservesTypedReason()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().MemoryRemember("/notes/note", ["existing content"]);
        string contentPath = Path.Combine(scope.WorkspaceDir, "append-content.txt");
        await File.WriteAllTextAsync(contentPath, "new content");
        FileMemoryStore.AppendFaultInjector = (point, _, _) =>
            point == AppendFaultPoint.ArchiveArtifactWrite
                ? new IOException("Injected archive replay I/O failure.")
                : null;

        try
        {
            var (exit, output) = await CaptureJsonAsync(() =>
                new ScriniaCommands().Append("/notes/note", contentPath, scope.WorkspaceDir, json: true));

            exit.Should().Be(1);
            using var document = JsonDocument.Parse(output);
            McpResponseAssert response = ResponseParser.Parse(document.RootElement.GetProperty("yaml").GetString()!);
            response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
            response.Reason.Should().Be("replayIoFailed");
            response.OperationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            FileMemoryStore.AppendFaultInjector = null;
        }
    }

    private static string WriteMalformedJournal(FileMemoryStore store)
    {
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "note.nmp2"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(directory, "note.meta.json"), "{\"name\":\"note\"}");
        File.WriteAllText(Path.Combine(directory, "index.json"), "{\"entries\":[]}");
        File.WriteAllText(Path.Combine(directory, "metadata.json"), "{\"scope\":\"local:notes\"}");
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
