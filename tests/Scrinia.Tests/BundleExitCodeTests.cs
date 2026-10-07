using FluentAssertions;
using System.IO.Compression;
using System.Text.Json;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Commands;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// <c>scri bundle export</c> and <c>scri bundle import</c> guarded their exit code with
/// <c>result.StartsWith("Error:", ...)</c> against <c>response.ToYaml()</c> — a shape the
/// renderer has never produced, since every envelope opens with <c>status: </c>. Both commands
/// therefore exited 0 on a genuine failure. These tests pin the fix (typed <see cref="McpResponse.IsError"/>)
/// on both sides: failure now exits 1. The pre-existing success path still exits 0, but the
/// warning path does not — an empty or fully-filtered import is also treated as a failure now
/// (see <see cref="Import_OnEmptyBundle_ExitsNonZero"/> below and <see cref="McpResponse.IsWarning"/>).
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public class BundleExitCodeTests
{
    [Theory]
    [InlineData("skill", "show")]
    [InlineData("agent", "show")]
    [InlineData("skill", "forget")]
    [InlineData("agent", "forget")]
    [InlineData("skill", "store")]
    [InlineData("agent", "store")]
    [InlineData("skill", "append")]
    [InlineData("agent", "append")]
    [InlineData("skill", "compact")]
    [InlineData("agent", "compact")]
    public async Task ReservedCliErrorResponsesPreserveTheirStableCode(string kind, string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json"), "invalid");
        string input = Path.Combine(scope.WorkspaceDir, "input.txt");
        File.WriteAllText(input, "content");
        string address = "/" + kind + "/profile";
        var commands = new ScriniaCommands();
        var (exit, output) = await CaptureJsonAsync(() => operation switch
        {
            "show" => commands.Show(address, workspaceRoot: scope.WorkspaceDir, json: true),
            "forget" => commands.Forget(address, workspaceRoot: scope.WorkspaceDir, json: true),
            "store" => commands.Store(address, input, workspaceRoot: scope.WorkspaceDir, json: true),
            "append" => commands.Append(address, input, workspaceRoot: scope.WorkspaceDir, json: true),
            _ => commands.Compact(address, workspaceRoot: scope.WorkspaceDir, json: true)
        });
        using (output)
        {
            exit.Should().Be(1);
            // Compact is forbidden for file entities before any persistent read is attempted.
            string expected = operation == "compact" ? ErrorCodes.ReservedPathOperationForbidden : ErrorCodes.BundleRecoveryRequired;
            output.RootElement.GetProperty("code").GetString().Should().Be(expected);
        }
    }

    [Fact]
    public async Task GuideRemainsAvailableWithRetainedBundleEvidence()
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json"), "invalid");
        var response = ResponseParser.Parse(await new ScriniaMcpTools().Guide());
        response.Content.Should().Be(EmbeddedPrompts.LoadGuide()!.TrimEnd());
        response.ErrorCode.Should().Be(ErrorCodes.BundleRecoveryRequired);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("search")]
    public async Task PersistentCliOperationMapsRetainedBundleEvidence(string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json"), "invalid");
        var commands = new ScriniaCommands();
        var (exit, output) = await CaptureJsonAsync(() => operation == "list"
            ? commands.List(workspaceRoot: scope.WorkspaceDir, json: true)
            : commands.Search("note", workspaceRoot: scope.WorkspaceDir, json: true));
        using (output)
        {
            exit.Should().Be(1);
            output.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.BundleRecoveryRequired);
        }
    }

    [Theory]
    [InlineData("list")]
    [InlineData("facts")]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task PersistentMcpOperationMapsRetainedBundleEvidence(string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json"), "invalid");
        var tools = new ScriniaMcpTools();
        string response = operation switch
        {
            "list" => await tools.MemoryList(),
            "facts" => await tools.Facts("entities"),
            "skill" => await tools.MemoryRecall("/skill/planner"),
            _ => await tools.MemoryRecall("/agent/profile")
        };
        ResponseParser.Parse(response).ErrorCode.Should().Be(ErrorCodes.BundleRecoveryRequired);
    }

    private static async Task<(int Exit, JsonDocument Output)> CaptureJsonAsync(Func<Task<int>> command)
    {
        var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await command();
            return (exit, JsonDocument.Parse(output.ToString()));
        }
        finally
        {
            Console.SetOut(prior);
        }
    }

    [Fact]
    public async Task EmptyCliListAndSearch_KeepStructuredPayloadsButExitOne()
    {
        using var scope = new TestHelpers.StoreScope();

        var (listExit, list) = await CaptureJsonAsync(() =>
            new ScriniaCommands().List(workspaceRoot: scope.WorkspaceDir, json: true));
        using (list)
        {
            listExit.Should().Be(1);
            list.RootElement.GetProperty("memories").GetArrayLength().Should().Be(0);
            list.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        }

        var (searchExit, search) = await CaptureJsonAsync(() =>
            new ScriniaCommands().Search("absent", workspaceRoot: scope.WorkspaceDir, json: true));
        using (search)
        {
            searchExit.Should().Be(1);
            search.RootElement.GetProperty("results").GetArrayLength().Should().Be(0);
            search.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        }
    }
    [Fact]
    public async Task Export_OnGenuineFailure_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();

        // Nonexistent topic -> ResponseBuilder.Error(..., ErrorCodes.NotFound, ...), reached
        // because no entries exist for the topic (MemoryTools.Bundling.cs:66-69).
        int exit = await new ScriniaCommands().Export("nonexistent-topic", workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(1, "a failed export must not report success to a shell or CI step");
    }

    [Fact]
    public async Task Import_OnGenuineFailure_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();

        // A bundle path that does not exist -> "Bundle file not found" error envelope
        // (MemoryTools.Bundling.cs:109-113).
        int exit = await new ScriniaCommands().Import(
            "does-not-exist.scrinia-bundle", workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(1);
    }

    [Fact]
    public async Task Import_WithMissingFileEntity_ExitsOneAndPreservesInvalidBundleEnvelopeInJson()
    {
        using var scope = new TestHelpers.StoreScope();
        string bundlePath = CreateFileEntityBundle(scope.WorkspaceDir, "missing-file", includeFile: false);

        var (exit, output) = await CaptureJsonAsync(() =>
            new ScriniaCommands().Import(bundlePath, workspaceRoot: scope.WorkspaceDir, json: true));
        using (output)
        {
            exit.Should().Be(1);
            ResponseParser.Parse(output.RootElement.GetProperty("message").GetString()!).ErrorCode
                .Should().Be(ErrorCodes.InvalidBundle);
        }
    }

    [Fact]
    public async Task Import_WithMalformedManifest_ExitsOneAndPreservesActionableInvalidBundleEnvelopeInJson()
    {
        using var scope = new TestHelpers.StoreScope();
        string bundlePath = CreateMalformedManifestBundle(scope.WorkspaceDir);

        var (exit, output) = await CaptureJsonAsync(() =>
            new ScriniaCommands().Import(bundlePath, workspaceRoot: scope.WorkspaceDir, json: true));
        using (output)
        {
            exit.Should().Be(1);
            var response = ResponseParser.Parse(output.RootElement.GetProperty("message").GetString()!);
            response.ErrorCode.Should().Be(ErrorCodes.InvalidBundle);
            response.Error.Should().Contain(ErrorCodes.InvalidBundle).And.Contain("manifest.json").And.Contain("valid JSON");
        }
    }

    [Fact]
    public async Task Import_WithNonObjectFileEntities_ExitsOneAndPreservesInvalidBundleEnvelopeInJson()
    {
        using var scope = new TestHelpers.StoreScope();
        string bundlePath = CreateBundleWithRawFileEntities(scope.WorkspaceDir, "null");

        var (exit, output) = await CaptureJsonAsync(() =>
            new ScriniaCommands().Import(bundlePath, workspaceRoot: scope.WorkspaceDir, json: true));
        using (output)
        {
            exit.Should().Be(1);
            var response = ResponseParser.Parse(output.RootElement.GetProperty("message").GetString()!);
            response.ErrorCode.Should().Be(ErrorCodes.InvalidBundle);
            response.Error.Should().Contain("fileEntities").And.Contain("JSON object");
        }
    }

    [Fact]
    public async Task Import_WhenFileEntityMoveFails_ExitsOneAndPreservesDurableWriteEnvelopeInJson()
    {
        using var scope = new TestHelpers.StoreScope();
        string bundlePath = CreateFileEntityBundle(scope.WorkspaceDir, "durable-file", includeFile: true);
        string destination = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills", "durable-file.md");
        DurableFile.FailureInjector = (phase, path) =>
            phase == DurableWritePhase.BeforeMove && path == destination
                ? new UnauthorizedAccessException("injected before file-entity move")
                : null;
        try
        {
            var (exit, output) = await CaptureJsonAsync(() =>
                new ScriniaCommands().Import(bundlePath, workspaceRoot: scope.WorkspaceDir, json: true));
            using (output)
            {
                exit.Should().Be(1);
                ResponseParser.Parse(output.RootElement.GetProperty("message").GetString()!).ErrorCode
                    .Should().Be(ErrorCodes.BundleImportFailed);
            }
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }
    }

    [Theory]
    [MemberData(nameof(RecoveryBlockedImportJournals))]
    public async Task Import_WhenRecoveryIsBlocked_PreservesTheExactDirectMcpYamlAndJournalBytes(
        string name, byte[] journalBytes)
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string storeDirectory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(storeDirectory);
        string journalPath = Path.Combine(storeDirectory, ".restore-journal.json");
        await File.WriteAllBytesAsync(journalPath, journalBytes);
        string bundlePath = CreateTopicBundle(scope.WorkspaceDir, "notes");

        if (name == "replay")
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
                transition == RestoreJournalTransition.BeforeReplayBody
                    ? new IOException("Injected replay failure.")
                    : null;
        }

        try
        {
            McpResponse direct = await new ScriniaMcpTools().Import(bundlePath, overwrite: true);
            var (exit, output) = await CaptureJsonAsync(() =>
                new ScriniaCommands().Import(bundlePath, workspaceRoot: scope.WorkspaceDir, overwrite: true, json: true));
            using (output)
            {
                exit.Should().Be(1, $"{name} recovery journal blocks import");
                string cliYaml = output.RootElement.GetProperty("message").GetString()!;
                cliYaml.Should().Be(direct.ToYaml(),
                    $"CLI JSON must embed the complete direct MCP envelope for {name} recovery");
                ResponseParser.Parse(cliYaml).ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
            }

            File.ReadAllBytes(journalPath).Should().Equal(journalBytes,
                $"{name} recovery journal remains byte-for-byte available for replay");
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }
    }

    /// <summary>
    /// RULED 2026-08-10: an import that matched no topics is a failure, not a success.
    ///
    /// <para>This test previously asserted exit 0 and said, in as many words, that if someone
    /// later decided an empty import was a failure, this was the place to record it as a
    /// decision. That decision has now been taken: a caller that asked for topics and received
    /// none has had its request silently not-performed, and reporting exit 0 makes a CI step
    /// gating on it pass. The same ruling applies to every <c>warning</c> envelope — see
    /// <see cref="McpResponse.IsWarning"/>.</para>
    ///
    /// <para>Breaking for anything gating on exit 0; recorded in CHANGELOG.md.</para>
    /// </summary>
    [Fact]
    public async Task Import_OnEmptyBundle_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["auth flow doc"], MemoryAddress.Parse("/api/auth"));
        string exportContent = ResponseParser.Parse(
            (await new ScriniaMcpTools().Export(["api"])).ToYaml()).Content!;
        string bundlePath = exportContent[(exportContent.LastIndexOf(") to ", StringComparison.Ordinal) + 5)..];

        // Filter on a topic the bundle does not contain -> topicCount == 0 and
        // ResponseBuilder.Warning("No topics were imported ...").
        int exit = await new ScriniaCommands().Import(
            bundlePath, workspaceRoot: scope.WorkspaceDir, topics: "does-not-match-anything");

        exit.Should().Be(1, "an import that matched nothing must not report success to a CI step");
    }

    [Fact]
    public async Task Export_OnSuccess_ExitsZero()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["auth flow doc"], MemoryAddress.Parse("/api/auth"));

        int exit = await new ScriniaCommands().Export("api", workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(0);
    }

    [Fact]
    public void IsWarning_IsTrueOnlyForTheWarningStatus()
    {
        ResponseBuilder.Warning("x").IsWarning.Should().BeTrue();
        ResponseBuilder.Success("x").IsWarning.Should().BeFalse();
        ResponseBuilder.Error("x").IsWarning.Should().BeFalse();
        ResponseBuilder.Warning("x").IsError.Should().BeFalse(
            because: "a warning is not an error — the two predicates must not overlap");
    }

    [Fact]
    public async Task EmptyMcpExportPreservesExistingBackup()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "exports");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "backup.scrinia-bundle");
        byte[] prior = "prior backup"u8.ToArray();
        File.WriteAllBytes(destination, prior);
        (await new ScriniaMcpTools().Export(["missing"], "backup")).IsError.Should().BeTrue();
        File.ReadAllBytes(destination).Should().Equal(prior);
    }

    [Fact]
    public async Task FileOnlyImportReportsCommittedFilesInCliJson()
    {
        using var scope = new TestHelpers.StoreScope();
        string bundlePath = CreateFileEntityBundle(scope.WorkspaceDir, "new-skill", includeFile: true);
        var (exit, output) = await CaptureJsonAsync(() =>
            new ScriniaCommands().Import(bundlePath, workspaceRoot: scope.WorkspaceDir, json: true));
        using (output)
        {
            exit.Should().Be(0);
            var response = ResponseParser.Parse(output.RootElement.GetProperty("message").GetString()!);
            response.Status.Should().Be("success");
            response.Content.Should().Contain("1 file entity");
        }
        File.ReadAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", "skills", "new-skill.md"))
            .Should().Be("file entity content");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackFailurePreservesExistingDestination(bool afterMove)
    {
        using var scope = new TestHelpers.StoreScope();
        string input = Path.Combine(scope.WorkspaceDir, "input.txt");
        File.WriteAllText(input, "pack content");
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "exports");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "backup.scrinia-bundle");
        byte[] prior = "prior pack backup"u8.ToArray();
        File.WriteAllBytes(destination, prior);
        bool moved = false;
        bool fired = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeMove && path == destination)
            {
                moved = true;
                if (!afterMove) return new UnauthorizedAccessException("pack before move");
            }
            if (afterMove && moved && !fired && phase == DurableWritePhase.BeforeDirSync && path == directory)
            {
                fired = true;
                return new IOException("pack after move");
            }
            return null;
        };
        try
        {
            var (exit, output) = await CaptureJsonAsync(() => new ScriniaCommands().Bundle(
                "notes", input, workspaceRoot: scope.WorkspaceDir, output: "backup", json: true));
            using (output)
            {
                exit.Should().Be(1);
                output.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.DurableWriteFailed);
            }
        }
        finally { DurableFile.FailureInjector = null; }
        moved.Should().BeTrue();
        if (afterMove) fired.Should().BeTrue();
        File.ReadAllBytes(destination).Should().Equal(prior);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpExportFailurePreservesExistingBackup(bool afterMove)
    {
        using var scope = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "exports");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "backup.scrinia-bundle");
        byte[] previous = "prior backup"u8.ToArray();
        File.WriteAllBytes(destination, previous);
        bool moved = false;
        bool fired = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeMove && path == destination)
            {
                moved = true;
                if (!afterMove) return new UnauthorizedAccessException("before replacement");
            }
            if (afterMove && moved && !fired && phase == DurableWritePhase.BeforeDirSync && path == directory)
            {
                fired = true;
                return new IOException("after replacement");
            }
            return null;
        };
        try
        {
            var response = await new ScriniaMcpTools().Export(["api"], "backup");
            response.IsError.Should().BeTrue();
            response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        }
        finally { DurableFile.FailureInjector = null; }
        moved.Should().BeTrue();
        if (afterMove) fired.Should().BeTrue();
        File.ReadAllBytes(destination).Should().Equal(previous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileOnlyCollisionReportsNoChangesAndPreservesLogicalPair(bool metadataOnly)
    {
        using var scope = new TestHelpers.StoreScope();
        string bundlePath = CreateFileEntityBundle(scope.WorkspaceDir, "existing-skill", includeFile: true);
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "existing-skill.md");
        string metadata = Path.Combine(directory, "existing-skill.meta.json");
        byte[] priorMeta = "{}"u8.ToArray();
        File.WriteAllBytes(metadata, priorMeta);
        if (!metadataOnly) File.WriteAllText(body, "prior body");
        var (exit, output) = await CaptureJsonAsync(() =>
            new ScriniaCommands().Import(bundlePath, workspaceRoot: scope.WorkspaceDir, json: true));
        using (output)
        {
            exit.Should().Be(1);
            var response = ResponseParser.Parse(output.RootElement.GetProperty("message").GetString()!);
            response.Status.Should().Be("warning");
            response.Content.Should().Contain("No changes imported");
        }
        File.ReadAllBytes(metadata).Should().Equal(priorMeta);
        if (metadataOnly) File.Exists(body).Should().BeFalse();
        else File.ReadAllText(body).Should().Be("prior body");
        using var archive = ZipFile.OpenRead(bundlePath);
        var summary = await BundleFormatService.ImportTopicsWithResultAsync(archive, TestHelpers.Store, null, false);
        summary.Restore.EntryCount.Should().Be(0);
        summary.Restore.FileEntityCount.Should().Be(0);
    }

    [Fact]
    public async Task FileOnlySummaryCountsActualChangesAndOverwriteRemovesOldMetadata()
    {
        using var scope = new TestHelpers.StoreScope();
        string bundlePath = CreateFileEntityBundle(scope.WorkspaceDir, "existing-skill", includeFile: true);
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(directory);
        string body = Path.Combine(directory, "existing-skill.md");
        string metadata = Path.Combine(directory, "existing-skill.meta.json");
        File.WriteAllText(body, "prior body");
        File.WriteAllText(metadata, "{}");
        using var archive = ZipFile.OpenRead(bundlePath);
        var summary = await BundleFormatService.ImportTopicsWithResultAsync(archive, TestHelpers.Store, null, true);
        summary.Restore.TopicCount.Should().Be(0);
        summary.Restore.EntryCount.Should().Be(0);
        summary.Restore.FileEntityCount.Should().Be(1);
        summary.TopicNames.Should().BeEmpty();
        File.ReadAllText(body).Should().Be("file entity content");
        File.Exists(metadata).Should().BeFalse();
    }
    [Fact]
    public async Task PackSuccessfullyReplacesPriorWithImportableBundle()
    {
        using var scope = new TestHelpers.StoreScope();
        string input = Path.Combine(scope.WorkspaceDir, "input.txt");
        File.WriteAllText(input, "pack content");
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "exports");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "backup.scrinia-bundle");
        File.WriteAllText(destination, "prior pack backup");
        var (exit, output) = await CaptureJsonAsync(() => new ScriniaCommands().Bundle(
            "notes", input, workspaceRoot: scope.WorkspaceDir, output: "backup", json: true));
        using (output)
        {
            exit.Should().Be(0);
            output.RootElement.GetProperty("path").GetString().Should().Be(destination);
        }
        using var archive = ZipFile.OpenRead(destination);
        var (topics, entries, names) = BundleFormatService.ImportTopicsFromZip(archive, TestHelpers.Store, null, true);
        topics.Should().Be(1);
        entries.Should().Be(1);
        names.Should().Equal("notes");
        string artifact = await TestHelpers.Store.ReadArtifactAsync(MemoryAddress.Parse("/notes/input"));
        Scrinia.Core.Encoding.Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks.Should().Equal("pack content");
        Directory.GetFiles(directory, ".backup.scrinia-bundle.*").Should().BeEmpty();
    }
    private static string CreateFileEntityBundle(string workspaceRoot, string name, bool includeFile)
    {
        string bundlePath = Path.Combine(workspaceRoot, $"{name}.scrinia-bundle");
        using var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        if (includeFile)
        {
            var entry = zip.CreateEntry($"files/skills/{name}.md");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("file entity content");
        }

        var manifest = zip.CreateEntry("manifest.json");
        using var manifestWriter = new StreamWriter(manifest.Open());
        manifestWriter.Write(JsonSerializer.Serialize(new
        {
            version = 2,
            topics = Array.Empty<string>(),
            totalEntries = 0,
            fileEntities = new Dictionary<string, string[]> { ["skills"] = [$"{name}.md"] }
        }));
        return bundlePath;
    }

    private static string CreateMalformedManifestBundle(string workspaceRoot)
    {
        string bundlePath = Path.Combine(workspaceRoot, "malformed-manifest.scrinia-bundle");
        using var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var manifest = zip.CreateEntry("manifest.json");
        using var writer = new StreamWriter(manifest.Open());
        writer.Write("{ malformed");
        return bundlePath;
    }

    private static string CreateBundleWithRawFileEntities(string workspaceRoot, string fileEntitiesJson)
    {
        string bundlePath = Path.Combine(workspaceRoot, "invalid-file-entities.scrinia-bundle");
        using var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var manifest = zip.CreateEntry("manifest.json");
        using var writer = new StreamWriter(manifest.Open());
        writer.Write($$"""{"version":2,"topics":[],"totalEntries":0,"fileEntities":{{fileEntitiesJson}}}""");
        return bundlePath;
    }

    private static string CreateTopicBundle(string workspaceRoot, string topic)
    {
        string bundlePath = Path.Combine(workspaceRoot, "topic.scrinia-bundle");
        using var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = new ArtifactEntry("note", "file://bundle", 0, 1, DateTimeOffset.UtcNow);
        BundleFormatService.WriteTopic(zip, TestHelpers.Store, topic, [entry], [(entry.Name, "NMP/2 bundle")]);
        BundleFormatService.WriteManifest(zip, [topic], totalEntries: 1, fileEntities: null);
        return bundlePath;
    }

    public static IEnumerable<object[]> RecoveryBlockedImportJournals()
    {
        yield return ["malformed", "{]"u8.ToArray()];
        yield return ["newer", CreateNewerRestoreJournal()];
        yield return ["replay", CreatePreparedRestoreJournal()];
    }

    private static byte[] CreateNewerRestoreJournal() =>
        """{"format":"scrinia.restore-journal","version":2,"state":"prepared","transactionId":"0123456789abcdef0123456789abcdef","scope":"local:notes","entries":[{"name":"note","bodyFile":"note.nmp2","bodyBase64":"AQ==","bodySha256":"4bf5122f344554c53bde2ebb8cd2b7e3d1600ad631c385a5d7c6f8a7c94e0c62","sidecarFile":"note.meta.json","sidecarBase64":"Ag==","sidecarSha256":"dbc1b4c900ffe48d575b5da5c638040125f65db0fe3e24494b76ea986457d986"}]}"""u8.ToArray();

    private static byte[] CreatePreparedRestoreJournal()
    {
        var entry = new ArtifactEntry("note", "file://bundle", 1, 1, DateTimeOffset.UtcNow);
        RestoreJournal journal = RestoreJournalCodec.CreatePrepared("local:notes",
            [new RestoreJournalPayload(entry, [1], [2])]);
        return RestoreJournalCodec.Serialize(journal);
    }

    /// <summary>
    /// `scri store` had no IsError guard at all and returned 0 unconditionally — the same dead-guard
    /// defect already fixed at Show, Forget, Export and Import.
    /// </summary>
    [Fact]
    public async Task Store_OnGenuineFailure_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();
        string contentFile = Path.Combine(scope.TempDir, "content.txt");
        await File.WriteAllTextAsync(contentFile, "some content");

        // A space is not a valid FileEntityName character, so the /agent/ route rejects this with
        // ResponseBuilder.Error(..., INVALID_PARAMETER) at MemoryTools.Core.cs:273-274 — before the
        // IMemoryStore check below it, so the verdict does not depend on the store mode.
        int exit = await new ScriniaCommands().Store(
            "/agent/bad name", contentFile, workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(1);
    }
}
