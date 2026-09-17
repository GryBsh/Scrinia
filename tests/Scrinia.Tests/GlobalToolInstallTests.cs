using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Process;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// Phase 5 Task 5 — isolated global-tool lifecycle and both server handshakes.
///
/// <para>These tests prove the immutable <c>scri</c> NuGet global tool installs, updates, and uninstalls
/// from a local feed into an empty tool path that sits outside the source checkout, that
/// <c>scri --version</c> reports cleanly, that both <c>scri serve</c> and <c>scri shared serve</c> complete
/// the MCP <c>initialize</c>/<c>initialized</c> handshake over stdio and exit zero, that the packaged tool
/// payload is never mutated at runtime, and that every mutable asset lives below an isolated
/// <c>SCRINIA_HOME</c>. They pack the Release configuration twice (two distinct versions) into a temporary
/// feed, install the first, exercise the full lifecycle, then uninstall.</para>
///
/// <para>The tests only invoke process helpers — <c>dotnet pack</c>, <c>dotnet tool install/update/uninstall
/// --tool-path</c>, and both server stdio handshakes — with verified exit codes. They never launch Git and
/// never write into the source checkout. The <c>SCRINIA_HOME</c> environment variable is process-wide (the
/// shared <see cref="ProcessRunner"/> has no per-call environment map), so it is redirected to an isolated
/// directory for the whole class and restored on dispose, and every server is launched with an explicit
/// <c>--workspace-root</c> so nothing is written into the repository working directory.</para>
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class GlobalToolInstallTests : IDisposable
{
    private const string ScriniaHomeVar = "SCRINIA_HOME";
    private const string ToolPackageId = "scrinia";
    private const string FirstVersion = "0.7.0";
    private const string SecondVersion = "0.8.0";
    private static readonly object FixtureGate = new();

    private static readonly string InitializeRequest = JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new object(),
            clientInfo = new { name = "scrinia-test", version = "0.0.0" }
        }
    });

    private static readonly string InitializedNotification = JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        method = "notifications/initialized"
    });

    private readonly string _feed;
    private readonly string _toolPath;
    private readonly string _home;
    private readonly string _workspace;
    private readonly string _sharedBase;
    private readonly string _scriExe;
    private readonly Dictionary<string, string> _baselinePackageTree;
    private readonly List<string> _stages;

    public GlobalToolInstallTests()
    {
        FixtureResources fixture = CreateFixture();
        _feed = fixture.Feed;
        _toolPath = fixture.ToolPath;
        _home = fixture.Home;
        _workspace = fixture.Workspace;
        _sharedBase = fixture.SharedBase;
        _scriExe = fixture.ScriExe;
        _baselinePackageTree = fixture.BaselinePackageTree;
        _stages = fixture.Stages;
    }

    private static FixtureResources CreateFixture()
    {
        string feed = Path.Combine(Path.GetTempPath(), $"scrinia-feed-{Guid.NewGuid():N}");
        string toolPath = Path.Combine(Path.GetTempPath(), $"scrinia-tool-{Guid.NewGuid():N}");
        string home = Path.Combine(Path.GetTempPath(), $"scrinia-home-{Guid.NewGuid():N}");
        string workspace = Path.Combine(Path.GetTempPath(), $"scrinia-ws-{Guid.NewGuid():N}");
        string sharedBase = Path.Combine(Path.GetTempPath(), $"scrinia-base-{Guid.NewGuid():N}");
        string[] ownedDirectories = [feed, toolPath, home, workspace, sharedBase];
        List<string> stages = [];

        try
        {
            foreach (string directory in ownedDirectories)
                Directory.CreateDirectory(directory);

            Directory.CreateDirectory(Path.Combine(sharedBase, ".git"));
            Directory.CreateDirectory(Path.Combine(workspace, ".scrinia"));
            WriteJson(Path.Combine(home, "config.json"), new
            {
                schemaVersion = 1,
                sharedBases = new Dictionary<string, object>
                {
                    ["team"] = new { root = sharedBase }
                }
            });
            WriteJson(Path.Combine(workspace, ".scrinia", "config.json"), new Dictionary<string, string>
            {
                ["Scrinia:Shared:Enabled"] = "team"
            });

            ProcessRunner runner = new();
            string dotnet = runner.ResolveExecutable("dotnet")
                ?? throw new InvalidOperationException("the dotnet SDK is required to pack and install the global tool");
            lock (FixtureGate)
            {
                stages.Add($"pack:{FirstVersion}");
                PackToFeed(dotnet, feed, FirstVersion);
                stages.Add($"pack:{SecondVersion}");
                PackToFeed(dotnet, feed, SecondVersion);
                stages.Add($"install:{FirstVersion}");
                InstallFromFeed(runner, feed, toolPath, FirstVersion).Should().Be(0,
                    "the first global tool version must install into the isolated tool path");
            }

            string scriExe = Directory.EnumerateFiles(toolPath, "scri.exe", SearchOption.AllDirectories)
                .SingleOrDefault()
                ?? throw new InvalidOperationException("the first global tool version did not install scri.exe");
            return new FixtureResources(
                feed, toolPath, home, workspace, sharedBase, scriExe,
                CapturePackageTree(toolPath), stages);
        }
        catch
        {
            List<Exception> cleanupFailures = [];
            foreach (string directory in ownedDirectories)
            {
                try { DeleteDirectoryWithRetry(directory); }
                catch (Exception cleanupFailure) { cleanupFailures.Add(cleanupFailure); }
            }

            if (cleanupFailures.Count > 0)
                throw new AggregateException("Global tool lifecycle setup failed and cleanup was incomplete.", cleanupFailures);
            throw;
        }
    }

    private sealed record FixtureResources(
        string Feed,
        string ToolPath,
        string Home,
        string Workspace,
        string SharedBase,
        string ScriExe,
        Dictionary<string, string> BaselinePackageTree,
        List<string> Stages);

    public void Dispose()
    {
        using ProcessEnvironmentLock environmentLock = ProcessEnvironmentLock.Acquire();
        List<Exception> cleanupFailures = [];
        foreach (string dir in new[] { _feed, _toolPath, _home, _workspace, _sharedBase })
        {
            try
            {
                DeleteDirectoryWithRetry(dir);
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(new IOException($"Failed to clean lifecycle fixture '{dir}'.", ex));
            }
        }

        if (cleanupFailures.Count > 0)
            throw new AggregateException("Global tool lifecycle fixture cleanup failed.", cleanupFailures);
    }

    // ── Phase 5 Task 5 — isolated install/update/uninstall and both server handshakes ─────────────

    [Fact]
    public async Task IsolatedGlobalToolLifecycle_PassesEveryStage()
    {
        using ProcessEnvironmentLock environmentLock = ProcessEnvironmentLock.Acquire();
        using EnvironmentVariableScope homeScope = new(ScriniaHomeVar, _home);
        ProcessRunner runner = new();
        string dotnet = runner.ResolveExecutable("dotnet")!;
        int timeoutSeconds = 120;

        // ── install: the first package into an empty tool path outside the source checkout ──────────
        MarkStage("install:asserted");
        _toolPath.Should().NotBeNullOrEmpty("the isolated tool path must be supplied");
        File.Exists(_scriExe).Should().BeTrue("the first tool version must install into the isolated tool path");

        // ── scri --version: reports the freshly installed version and exits zero ────────────────────
        MarkStage("version:first");
        ProcessResult version = Run(_scriExe, ["--version"], null);
        version.ExitCode.Should().Be(0, because: "scri --version must report the installed tool version and exit cleanly");
        version.Stdout.Trim().Should().Be(FirstVersion, $"the freshly installed global tool must report {FirstVersion}");

        // ── handshake scri serve (local MCP server) over stdio ──────────────────────────────────────
        MarkStage("serve:first");
        (int serveExit, string? serveResponse) = await HandshakeServeAsync(
            [ "serve", "--no-auto-setup", "--workspace-root", _workspace ]);
        serveExit.Should().Be(0, because: "scri serve must complete the MCP initialize/initialized handshake and exit cleanly");
        ExtractServerInfo(serveResponse).Should().Be("scri",
            "the local serve server must advertise the scri command as its MCP server identity");

        // ── handshake scri shared serve (shared MCP server) over stdio ──────────────────────────────
        MarkStage("shared-serve:first");
        (int sharedExit, string? sharedResponse) = await HandshakeServeAsync(
            [ "shared", "serve", "--workspace-root", _workspace ]);
        sharedExit.Should().Be(0, because: "scri shared serve must complete the MCP initialize/initialized handshake and exit cleanly");
        ExtractServerInfo(sharedResponse).Should().Be("scri",
            "the shared serve server must advertise the scri command as its MCP server identity");

        // ── immutable package: server use wrote nothing into the tool payload ───────────────────────
        AssertPackageTreeUnchanged(CapturePackageTree(_toolPath), _baselinePackageTree,
            "the global tool package is immutable; running the command never writes into it");

        // ── runtime assets stay under the isolated SCRINIA_HOME, never inside the package ───────────
        string modelDir = Path.Combine(_home, "models", "m2v-MiniLM-L6-v2");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "model.safetensors"), "model2vec model.safetensors");
        File.WriteAllText(Path.Combine(modelDir, "vocab.txt"), "model2vec vocab");

        MarkStage("setup");
        ProcessResult setup = Run(_scriExe, ["setup", "--no-ollama", "--workspace-root", _workspace], null);
        setup.ExitCode.Should().Be(0, because: "setup with the built-in model present completes cleanly");

        AssertPackageTreeUnchanged(CapturePackageTree(_toolPath), _baselinePackageTree,
            "setup must not create or mutate any file in the complete tool root, including .stage");
        Model2VecModelManager.IsModelAvailable(modelDir).Should().BeTrue(
            "the built-in model resolves under SCRINIA_HOME with verified assets, never AppContext.BaseDirectory/models");

        // ── update from the second package, then restart both servers against the updated payload ───
        int update = runner.RunAsync(
            dotnet,
            ["tool", "update", "--tool-path", _toolPath, "--add-source", _feed, ToolPackageId, "--version", SecondVersion],
            null,
            Timeout(timeoutSeconds)).GetAwaiter().GetResult().ExitCode;
        MarkStage("update");
        update.Should().Be(0, $".dotnet tool update must install {SecondVersion} from the local feed");

        MarkStage("version:updated");
        Run(_scriExe, ["--version"], null).Stdout.Trim().Should().Be(SecondVersion,
            $"the updated global tool must report {SecondVersion}");
        Dictionary<string, string> updatedPackageTree = CapturePackageTree(_toolPath);

        MarkStage("serve:updated");
        (int restartServeExit, string? restartServeResponse) = await HandshakeServeAsync(
            [ "serve", "--no-auto-setup", "--workspace-root", _workspace ]);
        restartServeExit.Should().Be(0, "the updated scri serve must restart and re-complete its MCP handshake");
        ExtractServerInfo(restartServeResponse).Should().Be("scri");

        MarkStage("shared-serve:updated");
        (int restartSharedExit, string? restartSharedResponse) = await HandshakeServeAsync(
            [ "shared", "serve", "--workspace-root", _workspace ]);
        restartSharedExit.Should().Be(0, "the updated scri shared serve must restart and re-complete its MCP handshake");
        ExtractServerInfo(restartSharedResponse).Should().Be("scri");
        AssertPackageTreeUnchanged(CapturePackageTree(_toolPath), updatedPackageTree,
            "restarting either server must not mutate the updated immutable package payload");

        // ── uninstall cleanly: the scri executable is gone from the isolated tool path ──────────────
        int uninstall = runner.RunAsync(
            dotnet,
            ["tool", "uninstall", "--tool-path", _toolPath, ToolPackageId],
            null,
            Timeout(timeoutSeconds)).GetAwaiter().GetResult().ExitCode;
        MarkStage("uninstall");
        uninstall.Should().Be(0, "dotnet tool uninstall must remove the global tool from the isolated tool path");

        Directory
            .EnumerateFiles(_toolPath, "scri.exe", SearchOption.AllDirectories)
            .Should().BeEmpty("uninstall must remove the scri executable from the isolated tool path");

        _stages.Should().ContainInOrder(
            "pack:0.7.0", "pack:0.8.0", "install:0.7.0", "install:asserted", "version:first",
            "serve:first", "shared-serve:first", "setup", "update", "version:updated",
            "serve:updated", "shared-serve:updated", "uninstall");
    }

    [Fact]
    public async Task IsolatedGlobalToolLifecycle_SecondIndependentCycle_PassesEveryStage()
    {
        ProcessRunner runner = new();
        string dotnet = runner.ResolveExecutable("dotnet")!;
        string cycleFeed = Path.Combine(Path.GetTempPath(), $"scrinia-feed-cycle2-{Guid.NewGuid():N}");
        string cycleToolPath = Path.Combine(Path.GetTempPath(), $"scrinia-tool-cycle2-{Guid.NewGuid():N}");
        string cycleHome = Path.Combine(Path.GetTempPath(), $"scrinia-home-cycle2-{Guid.NewGuid():N}");
        string cycleWorkspace = Path.Combine(Path.GetTempPath(), $"scrinia-ws-cycle2-{Guid.NewGuid():N}");
        string cycleSharedBase = Path.Combine(Path.GetTempPath(), $"scrinia-base-cycle2-{Guid.NewGuid():N}");
        List<string> stages = [];
        void MarkCycleStage(string stage)
        {
            stages.Add(stage);
            File.WriteAllText(Path.Combine(cycleHome, "active-stage.txt"), stage);
        }

        ProcessEnvironmentLock environmentLock = ProcessEnvironmentLock.Acquire();
        EnvironmentVariableScope? homeScope = null;
        try
        {
            foreach (string dir in new[] { cycleFeed, cycleToolPath, cycleHome, cycleWorkspace, cycleSharedBase })
                Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(cycleSharedBase, ".git"));
            Directory.CreateDirectory(Path.Combine(cycleWorkspace, ".scrinia"));
            WriteJson(Path.Combine(cycleHome, "config.json"), new
            {
                schemaVersion = 1,
                sharedBases = new Dictionary<string, object> { ["team"] = new { root = cycleSharedBase } }
            });
            WriteJson(Path.Combine(cycleWorkspace, ".scrinia", "config.json"), new Dictionary<string, string>
            {
                ["Scrinia:Shared:Enabled"] = "team"
            });

            lock (FixtureGate)
            {
                MarkCycleStage("pack:0.7.0");
                PackToFeed(dotnet, cycleFeed, FirstVersion);
                MarkCycleStage("pack:0.8.0");
                PackToFeed(dotnet, cycleFeed, SecondVersion);
            }
            MarkCycleStage("install:0.7.0");
            InstallFromFeed(runner, cycleFeed, cycleToolPath, FirstVersion).Should().Be(0);
            string cycleExe = Directory.EnumerateFiles(cycleToolPath, "scri.exe", SearchOption.AllDirectories).Single();
            Dictionary<string, string> firstSnapshot = CapturePackageTree(cycleToolPath);

            homeScope = new EnvironmentVariableScope(ScriniaHomeVar, cycleHome);
            MarkCycleStage("version:first");
            Run(cycleExe, ["--version"], null).Stdout.Trim().Should().Be(FirstVersion);
            MarkCycleStage("serve:first");
            (await HandshakeServeAsync(cycleExe, ["serve", "--no-auto-setup", "--workspace-root", cycleWorkspace])).ExitCode.Should().Be(0);
            MarkCycleStage("shared-serve:first");
            (await HandshakeServeAsync(cycleExe, ["shared", "serve", "--workspace-root", cycleWorkspace])).ExitCode.Should().Be(0);
            AssertPackageTreeUnchanged(CapturePackageTree(cycleToolPath), firstSnapshot,
                "the first independent cycle must not mutate its complete tool tree");

            string modelDir = Path.Combine(cycleHome, "models", "m2v-MiniLM-L6-v2");
            Directory.CreateDirectory(modelDir);
            File.WriteAllText(Path.Combine(modelDir, "model.safetensors"), "cycle two model");
            File.WriteAllText(Path.Combine(modelDir, "vocab.txt"), "cycle two vocab");
            MarkCycleStage("setup");
            Run(cycleExe, ["setup", "--no-ollama", "--workspace-root", cycleWorkspace], null).ExitCode.Should().Be(0);
            AssertPackageTreeUnchanged(CapturePackageTree(cycleToolPath), firstSnapshot,
                "setup must not mutate the first independent cycle tool tree");

            MarkCycleStage("update");
            runner.RunAsync(dotnet,
                ["tool", "update", "--tool-path", cycleToolPath, "--add-source", cycleFeed, ToolPackageId, "--version", SecondVersion],
                null, Timeout(120)).GetAwaiter().GetResult().ExitCode.Should().Be(0);
            MarkCycleStage("version:updated");
            Run(cycleExe, ["--version"], null).Stdout.Trim().Should().Be(SecondVersion);
            Dictionary<string, string> updatedSnapshot = CapturePackageTree(cycleToolPath);
            MarkCycleStage("serve:updated");
            (await HandshakeServeAsync(cycleExe, ["serve", "--no-auto-setup", "--workspace-root", cycleWorkspace])).ExitCode.Should().Be(0);
            MarkCycleStage("shared-serve:updated");
            (await HandshakeServeAsync(cycleExe, ["shared", "serve", "--workspace-root", cycleWorkspace])).ExitCode.Should().Be(0);
            AssertPackageTreeUnchanged(CapturePackageTree(cycleToolPath), updatedSnapshot,
                "the second independent cycle restart must not mutate its complete tool tree");

            MarkCycleStage("uninstall");
            runner.RunAsync(dotnet, ["tool", "uninstall", "--tool-path", cycleToolPath, ToolPackageId], null,
                Timeout(120)).GetAwaiter().GetResult().ExitCode.Should().Be(0);
            Directory.EnumerateFiles(cycleToolPath, "scri.exe", SearchOption.AllDirectories).Should().BeEmpty();
            stages.Should().ContainInOrder(
                "pack:0.7.0", "pack:0.8.0", "install:0.7.0", "version:first", "serve:first", "shared-serve:first",
                "setup", "update", "version:updated", "serve:updated", "shared-serve:updated", "uninstall");
        }
        finally
        {
            homeScope?.Dispose();
            List<Exception> failures = [];
            foreach (string dir in new[] { cycleFeed, cycleToolPath, cycleHome, cycleWorkspace, cycleSharedBase })
            {
                try
                {
                    DeleteDirectoryWithRetry(dir);
                }
                catch (Exception ex)
                {
                    failures.Add(new IOException($"Failed to clean second lifecycle fixture '{dir}'.", ex));
                }
            }
            environmentLock.Dispose();
            if (failures.Count > 0)
                throw new AggregateException("Second global tool lifecycle fixture cleanup failed.", failures);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    // The MCP stdio server replies to `initialize` on the next stdout line, so the handshake must be
    // interactive (send initialize, read the response, send `initialized`, then EOF) — the one-shot
    // ProcessRunner closes stdin before the response flushes and would capture an empty stdout.
    private async Task<(int ExitCode, string? InitializeResponse)> HandshakeServeAsync(IReadOnlyList<string> args)
        => await HandshakeServeAsync(_scriExe!, args);

    private async Task<(int ExitCode, string? InitializeResponse)> HandshakeServeAsync(
        string executable, IReadOnlyList<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        System.Diagnostics.Process? process = null;
        Task<string?>? responseTask = null;
        Task<string>? stderrTask = null;
        bool closeCompleted = false;
        try
        {
            process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("failed to launch scri serve");

            // Drain stderr so its buffer can never fill and deadlock the stdout read.
            stderrTask = process.StandardError.ReadToEndAsync();

            // 1) initialize — the server replies with the JSON-RPC initialize response on the next stdout line.
            await process.StandardInput.WriteAsync(InitializeRequest + "\n").ConfigureAwait(false);
            responseTask = ReadLineAsync(process.StandardOutput, 90);
            string? response = await responseTask.ConfigureAwait(false);
            if (response is null)
            {
                int exitNull = await CloseAndExitAsync(process, responseTask, stderrTask, 30).ConfigureAwait(false);
                closeCompleted = true;
                return (exitNull, null);
            }

            // 2) `initialized` notification, then EOF. shared serve has no shutdown method (-32601), so EOF is
            //    the only way to end the session cleanly.
            await process.StandardInput.WriteAsync(InitializedNotification + "\n").ConfigureAwait(false);
            int exit = await CloseAndExitAsync(process, responseTask, stderrTask, 30).ConfigureAwait(false);
            closeCompleted = true;
            return (exit, response);
        }
        finally
        {
            if (process is not null)
            {
                if (!closeCompleted)
                    await CleanupFailedProbeAsync(process, responseTask, stderrTask).ConfigureAwait(false);

                process.Dispose();
            }
        }
    }

    private static async Task<string?> ReadLineAsync(TextReader reader, int timeoutSeconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<int> CloseAndExitAsync(
        System.Diagnostics.Process process,
        Task<string?> firstOutputRead,
        Task<string> stderrRead,
        int timeoutSeconds)
    {
        try { process.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* stdin already gone */ }

        return await CloseAndExitAsync(process, firstOutputRead, stderrRead, TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
    }

    private static async Task<int> CloseAndExitAsync(
        System.Diagnostics.Process process,
        Task<string?> firstOutputRead,
        Task<string> stderrRead,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        bool exited = true;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            exited = false;
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between HasExited and Kill; the awaited exit below
                    // still establishes the same disposal guarantee.
                }
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        // The first read may still be waiting for EOF after a handshake timeout. Once the
        // process tree is dead, await it and drain the remaining redirected stdout as well as stderr.
        await firstOutputRead.ConfigureAwait(false);
        Task<string> stdoutRemainder = process.StandardOutput.ReadToEndAsync();
        await Task.WhenAll(stdoutRemainder, stderrRead).ConfigureAwait(false);
        return exited ? process.ExitCode : -1;
    }

    private static async Task CleanupFailedProbeAsync(
        System.Diagnostics.Process process,
        Task<string?>? firstOutputRead,
        Task<string>? stderrRead)
    {
        List<Exception> failures = [];
        using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(30));
        try
        {
            try { process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between the liveness check and Kill; the bounded wait below
                    // still establishes the same disposal guarantee.
                }
            }

            await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);

            if (firstOutputRead is not null)
                await firstOutputRead.WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);

            Task<string> stdoutRemainder = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrRemainder = stderrRead ?? process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutRemainder, stderrRemainder).WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(new IOException("Failed to terminate and drain a failed scri serve probe within 30 seconds.", ex));
        }
        finally
        {
            try { process.StandardInput.Dispose(); }
            catch (Exception ex) { failures.Add(new IOException("Failed to close failed probe stdin.", ex)); }
            try { process.StandardOutput.Dispose(); }
            catch (Exception ex) { failures.Add(new IOException("Failed to close failed probe stdout.", ex)); }
            try { process.StandardError.Dispose(); }
            catch (Exception ex) { failures.Add(new IOException("Failed to close failed probe stderr.", ex)); }
        }

        if (failures.Count > 0)
            throw new AggregateException("Failed scri serve probe cleanup was incomplete; marker cleanup is unsafe.", failures);
    }

    [Fact]
    public async Task CloseAndExitAsync_KillsAndAwaitsTimedOutProcessTree()
    {
        string marker = Path.Combine(Path.GetTempPath(), $"scrinia-child-marker-{Guid.NewGuid():N}.txt");
        string eventName = $"scrinia-child-liveness-{Guid.NewGuid():N}";
        string parentScript = Path.Combine(Path.GetTempPath(), $"scrinia-parent-{Guid.NewGuid():N}.cmd");
        string childScript = Path.Combine(Path.GetTempPath(), $"scrinia-child-{Guid.NewGuid():N}.cmd");
        string childCommand =
            $"$event = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, '{eventName}'); " +
            $"Set-Content -LiteralPath '{marker}' -Value 'child-started'; " +
            "Start-Sleep -Seconds 30; " +
            $"Set-Content -LiteralPath '{marker}' -Value 'child-finished'";
        File.WriteAllText(childScript,
            "@echo off" + Environment.NewLine +
            "echo child-stdout" + Environment.NewLine +
            "echo child-stderr 1>&2" + Environment.NewLine +
            $"powershell.exe -NoProfile -NonInteractive -Command \"{childCommand}\"" + Environment.NewLine);
        File.WriteAllText(parentScript,
            "@echo off" + Environment.NewLine +
            "echo parent-stdout" + Environment.NewLine +
            "echo parent-stderr 1>&2" + Environment.NewLine +
            $"start \"\" /b cmd /c call \"{childScript}\"" + Environment.NewLine +
            // Keep the parent alive after stdin is closed. `set /p` exits on EOF, which bypasses
            // the timeout/kill path and leaves the detached child holding redirected pipe handles
            // open while CloseAndExitAsync awaits EOF.
            "powershell.exe -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"" + Environment.NewLine);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("call");
        psi.ArgumentList.Add(parentScript);
        System.Diagnostics.Process? process = null;
        Task<string?>? output = null;
        Task<string>? error = null;
        try
        {
            process = System.Diagnostics.Process.Start(psi)!;
            output = process.StandardOutput.ReadLineAsync();
            error = process.StandardError.ReadToEndAsync();

            SpinWait.SpinUntil(() => File.Exists(marker), TimeSpan.FromSeconds(5))
                .Should().BeTrue("the parent must have launched the child before timeout cleanup");
            (await CloseAndExitAsync(process, output, error, TimeSpan.FromMilliseconds(100))).Should().Be(-1);
            process.HasExited.Should().BeTrue("timeout cleanup must await parent termination");
            output.IsCompleted.Should().BeTrue("stdout must be drained after the process tree is killed");
            error.IsCompleted.Should().BeTrue("stderr must be drained after the process tree is killed");
            (await output).Should().Contain("parent-stdout");
            (await error).Should().Contain("parent-stderr");

            EventWaitHandle? liveEvent = null;
            bool childStillOwnsLivenessEvent = EventWaitHandle.TryOpenExisting(eventName, out liveEvent);
            liveEvent?.Dispose();
            childStillOwnsLivenessEvent.Should().BeFalse(
                "the child-created liveness event must be gone after the process tree is terminated");
            File.ReadAllText(marker).Should().NotContain("child-finished",
                "the child must be terminated before it can report completion");
        }
        finally
        {
            List<Exception> failures = [];
            if (process is not null)
            {
                try { await CleanupFailedProbeAsync(process, output, error).ConfigureAwait(false); }
                catch (Exception ex) { failures.Add(ex); }
            }
            process?.Dispose();

            foreach (string path in new[] { marker, parentScript, childScript })
            {
                try { File.Delete(path); }
                catch (Exception ex) { failures.Add(new IOException($"Failed to delete probe artifact '{path}'.", ex)); }
            }

            if (failures.Count > 0)
                throw new AggregateException("Timed-out process probe cleanup was incomplete.", failures);
        }
    }

    private static ProcessResult Run(string executable, IReadOnlyList<string> arguments, string? stdin)
    {
        ProcessRunner runner = new();
        return runner.RunAsync(
            runner.ResolveExecutable(executable) ?? executable,
            arguments,
            stdin,
            Timeout(120)).GetAwaiter().GetResult();
    }

    private static void PackToFeed(string dotnet, string feed, string version)
    {
        string publishDirectory = Path.Combine(feed, $"publish-{version}");
        ProcessResult result = Run(dotnet,
        [
            "pack", RepoPaths.Combine("src", "Scrinia", "Scrinia.csproj"),
            "-c", "Release", "-o", feed, "--disable-build-servers", "--no-restore",
            $"-p:Version={version}", $"-p:PublishDir={publishDirectory}{Path.DirectorySeparatorChar}"
        ], null);

        result.ExitCode.Should().Be(0, $".dotnet pack -p:Version={version} must succeed:\n{result.Stderr}");

        string[] nupkgs = Directory.GetFiles(feed, $"scrinia.{version}.nupkg");
        nupkgs.Should().HaveCount(1,
            $"exactly one scrinia.{version} package must land in the local feed");
        File.Exists(nupkgs[0]).Should().BeTrue("the packed global tool package must be discoverable in the local feed");
        DeleteDirectoryWithRetry(publishDirectory);
    }

    private static void DeleteDirectoryWithRetry(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        IOException? lastFailure = null;
        for (int attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException ex)
            {
                lastFailure = ex;
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
            }
            catch (UnauthorizedAccessException ex)
            {
                lastFailure = new IOException($"Access denied while deleting '{directory}'.", ex);
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
            }
        }

        throw lastFailure ?? new IOException($"Failed to delete lifecycle directory '{directory}'.");
    }

    private static int InstallFromFeed(ProcessRunner runner, string feed, string toolPath, string version)
    {
        string dotnet = runner.ResolveExecutable("dotnet")!;
        ProcessResult result = runner.RunAsync(dotnet,
        [
            "tool", "install", "--tool-path", toolPath,
            "--add-source", feed, ToolPackageId, "--version", version
        ], null, Timeout(180)).GetAwaiter().GetResult();

        result.ExitCode.Should().Be(0, $".dotnet tool install {ToolPackageId}@{version} must succeed:\n{result.Stderr}");
        return result.ExitCode;
    }

    private void MarkStage(string stage) => _stages.Add(stage);

    private static Dictionary<string, string> CapturePackageTree(string toolPath)
    {
        // Snapshot the complete tool root: the launcher, .store package payload, .stage bookkeeping,
        // and every other installed subtree are part of the lifecycle isolation boundary.
        return Directory
            .EnumerateFileSystemEntries(toolPath, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToDictionary(
                 p => p.Substring(toolPath.Length).TrimStart(Path.DirectorySeparatorChar, '/'),
                 p => Directory.Exists(p)
                     ? "<directory>"
                     : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    }

    private static void AssertPackageTreeUnchanged(
        Dictionary<string, string> actual, Dictionary<string, string> expected, string because)
    {
        actual.Count.Should().Be(expected.Count, because);
        actual.Keys.Order(StringComparer.Ordinal).Should().Equal(expected.Keys.Order(StringComparer.Ordinal), because);
        foreach ((string path, string hash) in expected)
            actual[path].Should().Be(hash, $"{because}; path '{path}' must retain its content hash");
    }

    private static string? ExtractServerInfo(string? initializeResponse)
    {
        // The MCP initialize response is the single JSON-RPC line on stdout; navigate to result.serverInfo.name.
        if (initializeResponse is null) return null;
        using JsonDocument document = JsonDocument.Parse(initializeResponse);
        JsonElement result = document.RootElement.GetProperty("result");
        return result.TryGetProperty("serverInfo", out JsonElement serverInfo)
            && serverInfo.TryGetProperty("name", out JsonElement name)
            ? name.GetString()
            : null;
    }

    private static void WriteJson(string path, object value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static CancellationToken Timeout(int seconds) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
