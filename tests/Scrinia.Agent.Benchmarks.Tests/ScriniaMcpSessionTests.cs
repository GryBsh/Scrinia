using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentTestsGroup;

[Collection("Process environment")]
public sealed class ScriniaMcpSessionTests
{
    [Theory]
    [InlineData("exit-on-eof")]
    [InlineData("stall-on-eof")]
    public async Task Disposal_signals_eof_drains_stderr_and_reaps_the_owned_process(string mode)
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string marker = Path.Combine(temp.Root, "shutdown-fixture.txt");
        string host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var settings = Payload() with { Executable = host, PrefixArguments = [typeof(ProcessFixture).Assembly.Location, "--mcp-fixture", mode, marker] };
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        Assert.Contains("fixture guide", await Call(session, "guide", [], timeout.Token));
        var watch = Stopwatch.StartNew();
        await session.DisposeAsync();
        Assert.True(File.Exists(marker + ".eof"), "Shutdown must close stdin before waiting for the process.");
        AssertProcessEnded(int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(mode == "exit-on-eof" ? 5 : 20),
            $"Shutdown took {watch.Elapsed}.");
        Assert.Contains(events.Items, e => e.Kind == "stderr" && e.Data.GetString() == "fixture shutdown tail");
        await session.DisposeAsync(); // Repeated disposal must remain harmless.
    }

    [Fact]
    public async Task Real_remember_rejects_missing_null_and_blank_description_without_writing()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(temp.Root, "description-validation");
        await using var journal = await RunJournal.OpenAsync(root, RunJournalTests.Manifest(), false, timeout.Token);
        var settings = Payload();
        var paths = new AttemptWorkspace().Create(root, 1, 1, settings);
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, journal.CreateEvents(paths), timeout.Token);
        foreach (string mode in new[] { "omitted", "null", "blank" })
        {
            var arguments = new Dictionary<string, object?> { ["path"] = "/validation/" + mode,
                ["content"] = new[] { "MUST_NOT_BE_STORED" } };
            if (mode != "omitted") arguments["description"] = mode == "null" ? null : "  ";
            var result = await session.Tools.Single(t => t.Name == "remember").CallAsync(arguments, cancellationToken: timeout.Token);
            string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            Assert.True(result.IsError == true || text.Contains("status: error"), text);
            Assert.DoesNotContain("MUST_NOT_BE_STORED",
                await Call(session, "recall", new() { ["path"] = "/validation/" + mode }, timeout.Token));
        }
    }

    [Fact]
    public async Task Process_exit_after_tool_reply_cannot_be_accepted_as_a_successful_episode()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string marker = Path.Combine(temp.Root, "exit-fixture.txt");
        string host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var settings = Payload() with { Executable = host, PrefixArguments = [typeof(ProcessFixture).Assembly.Location, "--mcp-fixture", "exit-after-guide", marker] };
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(ScriptedChatClient.Call("guide", "guide", []), ScriptedChatClient.Text("ordinary final text"));
        var error = await Assert.ThrowsAsync<BenchmarkFailureException>(async () =>
        {
            await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
            fake.OnRequest = () =>
            {
                if (fake.Requests.Count != 2) return;
                File.WriteAllText(marker + ".exit", "exit");
                Assert.True(session.Completion.Wait(TimeSpan.FromSeconds(10)), "The owned fixture must exit before final text is returned.");
            };
            await new AgentEpisode(_ => fake).RunAsync(new(EpisodePhase.Learn, "input"), session,
                ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, timeout.Token);
        });
        Assert.Equal(BenchmarkFailureCode.MCP_DISCONNECTED, error.Code);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Hostile_parent_settings_do_not_reach_the_workspace_server()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var hostile = Directory.CreateDirectory(Path.Combine(temp.Root, "personal-home")).FullName;
        var keys = new[] { "SCRINIA_HOME", "SCRINIA_WORKSPACE_TRACKING", "SCRINIA__WORKSPACE__TRACKING", "BENCH_AGENT_API_KEY" };
        var prior = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(keys[0], hostile);
            Environment.SetEnvironmentVariable(keys[1], "invalid-parent-tracking");
            Environment.SetEnvironmentVariable(keys[2], "invalid-parent-tracking");
            Environment.SetEnvironmentVariable(keys[3], "SENTINEL_CREDENTIAL_NOT_FOR_CHILD");
            var root = Path.Combine(temp.Root, "run");
            await using var journal = await RunJournal.OpenAsync(root, RunJournalTests.Manifest(), false, timeout.Token);
            var paths = new AttemptWorkspace().Create(root, 1, 1, Payload());
            await using (var session = await ScriniaMcpSession.OpenAsync(Payload(), paths, journal.CreateEvents(paths), timeout.Token))
                Assert.Contains("status: success", await Call(session, "remember", new() { ["path"] = "/isolation/proof", ["description"] = "Stored facts for this test.", ["content"] = (string[])["isolated"] }, timeout.Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(hostile));
            Assert.DoesNotContain("SENTINEL_CREDENTIAL_NOT_FOR_CHILD", File.ReadAllText(Path.Combine(paths.Evidence, "events.ndjson")));
            Assert.Equal(hostile, Environment.GetEnvironmentVariable("SCRINIA_HOME"));
        }
        finally { foreach (var (key, value) in prior) Environment.SetEnvironmentVariable(key, value); }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("hang")]
    public async Task Failed_handshake_and_cancelled_startup_finish_owned_child_cleanup(string mode)
    {
        using var temp = new ScratchDirectory();
        var root = Path.Combine(temp.Root, "run");
        await using var journal = await RunJournal.OpenAsync(root, RunJournalTests.Manifest(), false, CancellationToken.None);
        var marker = Path.Combine(temp.Root, "owned-process.txt");
        var host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var settings = Payload() with { Executable = host, PrefixArguments = [typeof(ProcessFixture).Assembly.Location, "--mcp-fixture", mode, marker] };
        var paths = new AttemptWorkspace().Create(root, 1, 1, settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var opening = ScriniaMcpSession.OpenAsync(settings, paths, journal.CreateEvents(paths), timeout.Token);
        for (int i = 0; i < 250 && !File.Exists(marker) && !opening.IsCompleted; i++) await Task.Delay(20);
        Assert.True(File.Exists(marker), "The readable fixture must start before cancellation is tested.");
        if (mode == "hang") timeout.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => opening);
        AssertProcessEnded(int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Requested_plugin_failure_refuses_the_products_lexical_fallback()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(temp.Root, "run");
        await using var journal = await RunJournal.OpenAsync(root, RunJournalTests.Manifest(), false, timeout.Token);
        var seed = Path.Combine(temp.Root, "seed");
        var pluginDir = Directory.CreateDirectory(Path.Combine(seed, "plugins", "scri-plugin-embeddings", "0.6.0", RuntimeInformation.RuntimeIdentifier)).FullName;
        // A real managed apphost with its managed payload intentionally absent: a normal loader failure.
        File.Copy(Payload().Executable, Path.Combine(pluginDir, OperatingSystem.IsWindows() ? "scri-plugin-embeddings.exe" : "scri-plugin-embeddings"));
        var settings = Payload() with { HomeSeedDirectory = seed, WorkspaceConfig = new() { ["plugins:embeddings"] = "scri-plugin-embeddings", ["plugins:embeddings:version"] = "0.6.0" } };
        var paths = new AttemptWorkspace().Create(root, 1, 1, settings);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ScriniaMcpSession.OpenAsync(settings, paths, journal.CreateEvents(paths), timeout.Token));
        Assert.Contains("plugin", error.Message);
    }

    [Fact]
    public async Task Failed_stderr_evidence_sink_propagates_and_cleans_up()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var settings = Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new FailingEvents();
        await Assert.ThrowsAsync<IOException>(() => ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token));
    }

    private sealed class FailingEvents : IRunEvents
    {
        public ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct)
            => throw new IOException("Injected evidence write failure.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static ScriniaSettings Payload()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "scrinia-payload");
        return new(directory, Path.Combine(directory, OperatingSystem.IsWindows() ? "scri.exe" : "scri"), [], [], null);
    }

    internal static async Task<string> Call(ScriniaMcpSession session, string name, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await session.Tools.Single(t => t.Name == name).CallAsync(arguments, cancellationToken: ct);
        Assert.False(result.IsError == true);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    [Fact]
    public async Task Real_process_restart_preserves_disk_memory_but_not_ephemeral_or_other_case_state()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(temp.Root, "run space \u03bb");
        await using var journal = await RunJournal.OpenAsync(root, RunJournalTests.Manifest() with { SelectedIds = ["first", "second"] }, false, timeout.Token);
        var settings = Payload();
        var paths = new AttemptWorkspace().Create(root, 1, 1, settings);
        var events = journal.CreateEvents(paths);
        var first = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        Assert.Contains(first.Tools, t => t.Name == "guide");
        Assert.Equal(64, first.CatalogHash.Length);
        Assert.Contains("status: success", await Call(first, "remember", new() { ["path"] = "/test/persistent", ["description"] = "Stored facts for this test.", ["content"] = (string[])["PERSISTED_CASE_ONE"] }, timeout.Token));
        Assert.Contains("status: success", await Call(first, "remember", new() { ["path"] = "/temp/ephemeral", ["description"] = "Stored facts for this test.", ["content"] = (string[])["EPHEMERAL_CASE_ONE"] }, timeout.Token));
        await first.DisposeAsync();
        AssertProcessEnded(first.CompletedProcessId);
        await using (var second = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token))
        {
            Assert.Equal(first.CatalogHash, second.CatalogHash);
            Assert.Contains("PERSISTED_CASE_ONE", await Call(second, "recall", new() { ["path"] = "/test/persistent" }, timeout.Token));
            Assert.DoesNotContain("EPHEMERAL_CASE_ONE", await Call(second, "recall", new() { ["path"] = "/temp/ephemeral" }, timeout.Token));
        }
        var otherPaths = new AttemptWorkspace().Create(root, 2, 1, settings);
        await using var other = await ScriniaMcpSession.OpenAsync(settings, otherPaths, journal.CreateEvents(otherPaths), timeout.Token);
        Assert.DoesNotContain("PERSISTED_CASE_ONE", await Call(other, "recall", new() { ["path"] = "/test/persistent" }, timeout.Token));
        Assert.DoesNotContain("EPHEMERAL_CASE_ONE", await Call(other, "recall", new() { ["path"] = "/temp/ephemeral" }, timeout.Token));
    }

    [Fact]
    public async Task Missing_executable_and_startup_cancellation_fail_explicitly()
    {
        using var temp = new ScratchDirectory();
        var root = Path.Combine(temp.Root, "run");
        await using var journal = await RunJournal.OpenAsync(root, RunJournalTests.Manifest(), false, CancellationToken.None);
        var settings = Payload();
        var paths = new AttemptWorkspace().Create(root, 1, 1, settings);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ScriniaMcpSession.OpenAsync(settings with { Executable = Path.Combine(temp.Root, "missing") }, paths, journal.CreateEvents(paths), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScriniaMcpSession.OpenAsync(settings, paths, journal.CreateEvents(paths), cancelled.Token));
    }

    private static void AssertProcessEnded(int? id)
    {
        Assert.NotNull(id);
        try { using var process = Process.GetProcessById(id.Value); Assert.True(process.HasExited); }
        catch (ArgumentException) { } // No process with that owned identity remains.
    }
}
