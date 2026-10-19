using System.Text.Json;
using Scrinia.Commands;
using WorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class TrackingAdmissionTests
{
    [Theory]
    [InlineData("set")]
    [InlineData("instance-set")]
    [InlineData("save")]
    [InlineData("instance-save")]
    [InlineData("unset")]
    [InlineData("instance-unset")]
    [InlineData("enabled")]
    public void OrdinaryConfigMutationDoesNotConsumeTrackingEvidence(string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        WorkspaceConfig.SetValue(scope.WorkspaceDir, "before", "kept");
        var snapshot = WorkspaceConfig.Load(scope.WorkspaceDir);
        byte[] before = File.ReadAllBytes(ConfigPath(scope.WorkspaceDir));
        string journal = JournalPath(scope.WorkspaceDir);
        File.WriteAllText(journal, "complete\n");
        Assert.Throws<TrackingRecoveryRequiredException>(() =>
        {
            switch (operation)
            {
                case "set": WorkspaceConfig.SetValue(scope.WorkspaceDir, "after", "blocked"); break;
                case "instance-set": snapshot.SetValue("after", "blocked"); break;
                case "save": WorkspaceConfig.Save(scope.WorkspaceDir, new() { ["after"] = "blocked" }); break;
                case "instance-save": snapshot.Save(); break;
                case "unset": WorkspaceConfig.UnsetValue(scope.WorkspaceDir, "before"); break;
                case "instance-unset": snapshot.UnsetValue("before"); break;
                case "enabled": snapshot.UpdateEnabledBases(_ => ["team"]); break;
            }
        });
        Assert.Equal("complete\n", File.ReadAllText(journal));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath(scope.WorkspaceDir)));
        Assert.Equal("kept", snapshot.GetValue("before"));
        Assert.Null(snapshot.GetValue("after"));
        var current = WorkspaceConfig.Load(scope.WorkspaceDir);
        Assert.Equal("kept", current.GetValue("before"));
        Assert.Null(current.GetValue("after"));
        Assert.Empty(current.GetEnabledBases());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliConfigPreservesBytesAndCacheAndReturnsRecoveryCode(bool unset)
    {
        using var scope = new TestHelpers.StoreScope();
        WorkspaceConfig.SetValue(scope.WorkspaceDir, "before", "kept");
        var snapshot = WorkspaceConfig.Load(scope.WorkspaceDir);
        byte[] before = File.ReadAllBytes(ConfigPath(scope.WorkspaceDir));
        File.WriteAllText(JournalPath(scope.WorkspaceDir), "complete\n");
        using var output = new StringWriter();
        TextWriter original = Console.Out;
        int exit;
        try
        {
            Console.SetOut(output);
            exit = await new ScriniaCommands().Config("before", unset ? null : "blocked", unset,
                scope.WorkspaceDir, json: true);
        }
        finally { Console.SetOut(original); }
        Assert.Equal(4, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("CONFIG_RECOVERY_REQUIRED", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(ConfigPath(scope.WorkspaceDir)));
        Assert.Equal("complete\n", File.ReadAllText(JournalPath(scope.WorkspaceDir)));
        Assert.Equal("kept", snapshot.GetValue("before"));
        Assert.Equal("kept", WorkspaceConfig.Load(scope.WorkspaceDir).GetValue("before"));
    }

    public static IEnumerable<object[]> PersistentCases()
    {
        foreach (string operation in new[] { "store", "append", "read", "artifact", "list", "search", "index", "save-index", "export", "import", "recover" })
        foreach (bool unreadable in new[] { false, true })
            yield return [operation, unreadable];
    }

    [Theory]
    [MemberData(nameof(PersistentCases))]
    public async Task WarmPersistentOperationsRefuseWithoutMutation(string operation, bool unreadable)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var address = MemoryAddress.Parse("/note");
        var write = new MemoryWrite(address, ["before"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []);
        await store.StoreAsync(write);
        store.ListScoped();
        store.LoadIndex();
        await store.ReadAsync(address);
        var before = Snapshot(scope.WorkspaceDir);
        if (unreadable) Directory.CreateDirectory(JournalPath(scope.WorkspaceDir));
        else File.WriteAllText(JournalPath(scope.WorkspaceDir), "complete\n");
        var exception = await Assert.ThrowsAsync<RestoreRecoveryException>(async () =>
        {
            switch (operation)
            {
                case "store": await store.StoreAsync(write); break;
                case "append": await store.AppendAsync(write); break;
                case "read": await store.ReadAsync(address); break;
                case "artifact": await store.ReadArtifactAsync(address); break;
                case "list": store.ListScoped(); break;
                case "search": store.SearchAll(new SearchQuery("before")); break;
                case "index": store.LoadIndex(); break;
                case "save-index": store.SaveIndex([]); break;
                case "export": BundleFormatService.ExportTopicsToZip(Path.Combine(scope.WorkspaceDir, "out.zip"), store, ["memory"]); break;
                case "import": await store.RestoreBundleAsync(new([], [], true)); break;
                case "recover": await store.RecoverAppendAsync("local"); break;
            }
        });
        Assert.IsType<TrackingRecoveryRequiredException>(exception.InnerException);
        Assert.Equal(JournalPath(scope.WorkspaceDir), exception.JournalPath);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        if (unreadable) Assert.True(Directory.Exists(JournalPath(scope.WorkspaceDir)));
        else Assert.Equal("complete\n", File.ReadAllText(JournalPath(scope.WorkspaceDir)));
        Assert.False(File.Exists(Path.Combine(scope.WorkspaceDir, "out.zip")));
    }

    internal static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.GetFiles(Path.Combine(root, ".scrinia"), "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllBytes);
    internal static string JournalPath(string root) => Path.Combine(root, ".scrinia", ".tracking-recovery.json");
    private static string ConfigPath(string root) => Path.Combine(root, ".scrinia", "config.json");
    [Theory]
    [InlineData("skill", "read")]
    [InlineData("skill", "write")]
    [InlineData("skill", "append")]
    [InlineData("skill", "forget")]
    [InlineData("agent", "read")]
    [InlineData("agent", "write")]
    [InlineData("agent", "append")]
    [InlineData("agent", "forget")]
    [InlineData("ordinary", "read")]
    [InlineData("ordinary", "write")]
    [InlineData("ordinary", "append")]
    [InlineData("ordinary", "forget")]
    public async Task McpSurfacesTrackingRefusalAsRecoveryBlocked(string kind, string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string path = kind == "ordinary" ? "/note" : "/" + kind + "/note";
        await tools.MemoryRemember(path, ["before"], description: "before");
        await tools.MemoryRecall(path);
        var before = Snapshot(scope.WorkspaceDir);
        File.WriteAllText(JournalPath(scope.WorkspaceDir), "complete\n");
        string result = operation switch
        {
            "read" => await tools.MemoryRecall(path),
            "write" => await tools.MemoryRemember(path, ["blocked"], description: "blocked"),
            "append" => await tools.MemoryAppend(path, "blocked"),
            _ => await tools.MemoryForget(path)
        };
        Assert.Contains("RECOVERY_BLOCKED", result);
        Assert.Contains(".tracking-recovery.json", result);
        Assert.DoesNotContain("MALFORMED_NMP", result);
        Assert.DoesNotContain("NOT_FOUND", result);
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal("complete\n", File.ReadAllText(JournalPath(scope.WorkspaceDir)));
    }

    [Fact]
    public async Task CliMemoryMapsTrackingEvidenceToRecoveryBlocked()
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(JournalPath(scope.WorkspaceDir), "complete\n");
        using var output = new StringWriter();
        TextWriter original = Console.Out;
        int exit;
        try
        {
            Console.SetOut(output);
            exit = await new ScriniaCommands().Show("/missing", scope.WorkspaceDir, json: true);
        }
        finally { Console.SetOut(original); }
        Assert.Equal(1, exit);
        Assert.Contains("RECOVERY_BLOCKED", output.ToString());
        Assert.Contains(".tracking-recovery.json", output.ToString());
        Assert.DoesNotContain("NOT_FOUND", output.ToString());
    }

    [Fact]
    public async Task EphemeralOperationsRemainIndependent()
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(JournalPath(scope.WorkspaceDir), "complete\n");
        var tools = new ScriniaMcpTools();
        Assert.DoesNotContain("RECOVERY_BLOCKED", await tools.MemoryRemember("/temp/note", ["scratch"], description: "scratch"));
        Assert.Contains("scratch", await tools.MemoryRecall("/temp/note"));
        Assert.DoesNotContain("RECOVERY_BLOCKED", await tools.MemoryAppend("/temp/note", "more"));
        IMemoryStore store = TestHelpers.Store;
        Assert.Single(store.ListScoped(["ephemeral"]));
        Assert.DoesNotContain("RECOVERY_BLOCKED", await tools.MemoryForget("/temp/note"));
        Assert.Empty(store.ListScoped(["ephemeral"]));
        Assert.Equal("complete\n", File.ReadAllText(JournalPath(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceRetainsPendingAndUnreadableEvidence(bool unreadable)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        if (unreadable) Directory.CreateDirectory(JournalPath(scope.WorkspaceDir));
        else File.WriteAllText(JournalPath(scope.WorkspaceDir), "complete\n");
        var before = Snapshot(scope.WorkspaceDir);
        var result = await store.ExecuteMaintenanceScopeAsync("local",
            new MaintenancePolicy(10, 30, 50, 5, true), 50, DateTimeOffset.UtcNow.AddSeconds(10), default);
        Assert.True(result.RecoveryRetained);
        Assert.Equal(0, result.JournalConsumed);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(unreadable ? Directory.Exists(JournalPath(scope.WorkspaceDir)) : File.Exists(JournalPath(scope.WorkspaceDir)));
    }

    [Fact]
    public void PendingTrackingEvidenceRefusesRequiredIndexProjection()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        string memories = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories");
        Directory.CreateDirectory(memories);
        File.WriteAllText(Path.Combine(memories, "note.nmp2"),
            new Scrinia.Core.Encoding.Nmp2Strategy().Encode("before"u8, new()).Artifact);
        File.WriteAllText(Path.Combine(memories, "note.meta.json"),
            """{"name":"note","uri":"file:///note.nmp2","originalBytes":6,"chunkCount":1,"createdAt":"2026-01-01T00:00:00+00:00","keywords":["person:maria"]}""");
        byte[] original = File.ReadAllBytes(Path.Combine(memories, "note.meta.json"));
        File.WriteAllText(JournalPath(scope.WorkspaceDir), "complete\n");
        Assert.Throws<RestoreRecoveryException>(() => store.LoadIndex());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(memories, "note.meta.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedEnableCliMapsTrackingEvidence(bool enable)
    {
        using var scope = new TestHelpers.StoreScope();
        using var target = new TestHelpers.StoreScope();
        Directory.CreateDirectory(Path.Combine(target.WorkspaceDir, ".git"));
        string home = Path.Combine(scope.WorkspaceDir, "home");
        var registry = new SharedBaseRegistry(home, scope.WorkspaceDir);
        Assert.True(registry.Add("team", target.WorkspaceDir).Succeeded);
        WorkspaceConfig.SetValue(scope.WorkspaceDir, "before", "kept");
        var snapshot = WorkspaceConfig.Load(scope.WorkspaceDir);
        byte[] config = File.ReadAllBytes(ConfigPath(scope.WorkspaceDir));
        byte[] registryBytes = File.ReadAllBytes(Path.Combine(home, "config.json"));
        File.WriteAllText(JournalPath(scope.WorkspaceDir), "complete\n");
        string current = Environment.CurrentDirectory;
        string? previousHome = Environment.GetEnvironmentVariable("SCRINIA_HOME");
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        int exit;
        try
        {
            Environment.CurrentDirectory = scope.WorkspaceDir;
            Environment.SetEnvironmentVariable("SCRINIA_HOME", home);
            Console.SetOut(output);
            var commands = new SharedEnableCommands();
            exit = enable ? commands.Enable("team", json: true) : commands.Disable("team", json: true);
        }
        finally
        {
            Console.SetOut(original);
            Environment.SetEnvironmentVariable("SCRINIA_HOME", previousHome);
            Environment.CurrentDirectory = current;
        }
        Assert.Equal(4, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("CONFIG_RECOVERY_REQUIRED", json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(config, File.ReadAllBytes(ConfigPath(scope.WorkspaceDir)));
        Assert.Equal(registryBytes, File.ReadAllBytes(Path.Combine(home, "config.json")));
        Assert.Equal("kept", snapshot.GetValue("before"));
        Assert.Equal("kept", WorkspaceConfig.Load(scope.WorkspaceDir).GetValue("before"));
        Assert.Equal("complete\n", File.ReadAllText(JournalPath(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData("tracking", "complete\n")]
    [InlineData("tracking", "malformed")]
    [InlineData("tracking", "directory")]
    [InlineData("bundle", "prepared")]
    [InlineData("bundle", "malformed")]
    [InlineData("bundle", "directory")]
    public void SharedBaseAddRefusesEvidenceBeforeChangingIgnoreOrRegistry(string kind, string content)
    {
        using var scope = new TestHelpers.StoreScope();
        using var target = new TestHelpers.StoreScope();
        Directory.CreateDirectory(Path.Combine(target.WorkspaceDir, ".git"));
        string home = Path.Combine(scope.WorkspaceDir, "home");
        string ignore = Path.Combine(target.WorkspaceDir, ".scrinia", ".gitignore");
        File.WriteAllText(ignore, "# user bytes\n");
        var registry = new SharedBaseRegistry(home, scope.WorkspaceDir);
        // Preserve an existing home document, including its exact serialization.
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.json"), "{\"schemaVersion\":1,\"sharedBases\":{}}");
        byte[] beforeHome = File.ReadAllBytes(Path.Combine(home, "config.json"));
        string evidence = kind == "tracking" ? JournalPath(target.WorkspaceDir)
            : Path.Combine(target.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json");
        if (content == "directory") Directory.CreateDirectory(evidence);
        else File.WriteAllText(evidence, content);
        var result = new SharedBaseOperations(registry, home, scope.WorkspaceDir).Add("team", target.WorkspaceDir);
        Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, result.ErrorCode);
        Assert.Equal(ConfigurationExitClass.WriteFailure, result.ExitClass);
        Assert.Contains(evidence, result.Exception!.ToString());
        Assert.Equal("# user bytes\n", File.ReadAllText(ignore));
        Assert.Equal(beforeHome, File.ReadAllBytes(Path.Combine(home, "config.json")));
        if (content == "directory") Assert.True(Directory.Exists(evidence));
        else Assert.Equal(content, File.ReadAllText(evidence));
    }

}
