using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class RecoveryBlockedFailureClassCoverageTests
{
    private const string Scope = "local:notes";

    [Theory]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    [InlineData("replay-io")]
    public async Task BlockedJournalFailureClasses_KeepTheirJournalBytesAndReturnRecoveryBlockedAcrossImportSurfaces(string failureClass)
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string journalPath = WriteBlockedJournal(store, failureClass);
        byte[] originalJournal = File.ReadAllBytes(journalPath);
        string bundlePath = CreateTopicBundle(scope.WorkspaceDir);
        var tools = new ScriniaMcpTools();

        if (failureClass == "replay-io")
            FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
                transition == RestoreJournalTransition.BeforeReplayBody
                    ? new IOException("Injected replay I/O failure.")
                    : null;

        try
        {
            McpResponse mcpImport = await tools.Import(bundlePath, overwrite: true);
            AssertRecoveryBlocked(mcpImport, failureClass, journalPath);
            AssertJournalUnchanged(journalPath, originalJournal, "direct MCP import");

            McpResponseAssert recall = ResponseParser.Parse(await tools.MemoryRecall("/notes/note"));
            recall.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
            AssertRecoveryGuidance(recall.Error, failureClass, journalPath);
            AssertJournalUnchanged(journalPath, originalJournal, "MCP recall route");

            var (exit, json) = await CaptureJsonAsync(() =>
                new ScriniaCommands().Import(bundlePath, workspaceRoot: scope.WorkspaceDir, json: true));
            exit.Should().Be(1);
            using var document = JsonDocument.Parse(json);
            string cliEmbeddedYaml = document.RootElement.GetProperty("message").GetString()!;
            cliEmbeddedYaml.Should().Be(mcpImport.ToYaml(),
                because: "the CLI must embed the complete MCP recovery envelope, including status and actionNeeded");
            McpResponseAssert cli = ResponseParser.Parse(cliEmbeddedYaml);
            cli.Status.Should().Be(mcpImport.Status);
            cli.ActionNeeded.Should().BeEquivalentTo(mcpImport.ActionNeeded);
            cli.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
            AssertRecoveryGuidance(cli.Error, failureClass, journalPath);
            AssertJournalUnchanged(journalPath, originalJournal, "JSON CLI import");
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }
    }

    [Fact]
    public async Task LegacyBundleExport_MapsOnlyRestoreRecoveryFailuresToRecoveryBlocked()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string directory = store.GetStoreDirForScope(Scope);
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        byte[] journal = Encoding.UTF8.GetBytes("{]");
        File.WriteAllBytes(journalPath, journal);

        string yaml = await new ScriniaMcpTools().Bundle("export", ["notes"]);

        McpResponseAssert response = ResponseParser.Parse(yaml);
        response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
        response.Error.Should().Contain(Scope).And.Contain(journalPath);
        File.ReadAllBytes(journalPath).Should().Equal(journal);
    }

    private static void AssertRecoveryBlocked(McpResponse response, string failureClass, string journalPath)
    {
        response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
        AssertRecoveryGuidance(response.Error, failureClass, journalPath);
    }

    private static void AssertRecoveryGuidance(string? error, string failureClass, string journalPath)
    {
        error.Should().NotBeNull();
        error.Should().Contain(Scope).And.Contain(journalPath).And.NotContain(ErrorCodes.InvalidBundle);

        switch (failureClass)
        {
            case "malformed":
                error.Should().Contain("No files were changed and the journal was retained");
                return;
            case "unsupported":
                error.Should().Contain("version 2 is newer").And.Contain("Upgrade scri, then retry");
                return;
            case "replay-io":
                error.Should().Contain("Check permissions and free disk space, then retry");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(failureClass), failureClass, "Unknown recovery failure class.");
        }
    }

    private static void AssertJournalUnchanged(string journalPath, byte[] originalJournal, string access)
    {
        File.ReadAllBytes(journalPath).Should().Equal(originalJournal,
            because: $"{access} must retain blocked recovery evidence byte-for-byte");
    }

    private static string WriteBlockedJournal(FileMemoryStore store, string failureClass)
    {
        string directory = store.GetStoreDirForScope(Scope);
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        byte[] journal = failureClass switch
        {
            "malformed" => Encoding.UTF8.GetBytes("{]"),
            "unsupported" => CreateUnsupportedVersionJournal(),
            "replay-io" => CreatePreparedJournal(),
            _ => throw new ArgumentOutOfRangeException(nameof(failureClass), failureClass, "Unknown recovery failure class.")
        };
        File.WriteAllBytes(journalPath, journal);
        return journalPath;
    }

    private static byte[] CreateUnsupportedVersionJournal() => Encoding.UTF8.GetBytes(
        """{"format":"scrinia.restore-journal","version":2,"state":"prepared","transactionId":"0123456789abcdef0123456789abcdef","scope":"local:notes","entries":[{"name":"note","bodyFile":"note.nmp2","bodyBase64":"AQ==","bodySha256":"4bf5122f344554c53bde2ebb8cd2b7e3d1600ad631c385a5d7c6f8a7c94e0c62","sidecarFile":"note.meta.json","sidecarBase64":"Ag==","sidecarSha256":"dbc1b4c900ffe48d575b5da5c638040125f65db0fe3e24494b76ea986457d986"}]}""");

    private static byte[] CreatePreparedJournal()
    {
        var entry = new ArtifactEntry("note", "file://bundle", 1, 1, DateTimeOffset.UtcNow);
        RestoreJournal journal = RestoreJournalCodec.CreatePrepared(Scope,
            [new RestoreJournalPayload(entry, [1], [2])]);
        return RestoreJournalCodec.Serialize(journal);
    }

    private static string CreateTopicBundle(string workspaceRoot)
    {
        string bundlePath = Path.Combine(workspaceRoot, "recovery-blocked.scrinia-bundle");
        using var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write);
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
        var entry = new ArtifactEntry("note", "file://bundle", 0, 1, DateTimeOffset.UtcNow);
        BundleFormatService.WriteTopic(zip, TestHelpers.Store, "notes", [entry], [(entry.Name, "NMP/2 bundle")]);
        BundleFormatService.WriteManifest(zip, ["notes"], totalEntries: 1, fileEntities: null);
        return bundlePath;
    }

    private static async Task<(int Exit, string Json)> CaptureJsonAsync(Func<Task<int>> command)
    {
        var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await command();
            return (exit, output.ToString());
        }
        finally
        {
            Console.SetOut(prior);
        }
    }
}
