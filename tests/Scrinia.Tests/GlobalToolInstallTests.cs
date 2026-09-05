using System.Text;
using System.Text.Json;
using FluentAssertions;
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
    private readonly Dictionary<string, byte[]> _baselinePackageTree;
    private string? _previousHome;

    public GlobalToolInstallTests()
    {
        ProcessRunner runner = new();
        string? dotnet = runner.ResolveExecutable("dotnet");
        dotnet.Should().NotBeNullOrEmpty("the dotnet SDK is required to pack and install the global tool");

        _feed = Path.Combine(Path.GetTempPath(), $"scrinia-feed-{Guid.NewGuid():N}");
        _toolPath = Path.Combine(Path.GetTempPath(), $"scrinia-tool-{Guid.NewGuid():N}");
        _home = Path.Combine(Path.GetTempPath(), $"scrinia-home-{Guid.NewGuid():N}");
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia-ws-{Guid.NewGuid():N}");
        _sharedBase = Path.Combine(Path.GetTempPath(), $"scrinia-base-{Guid.NewGuid():N}");

        foreach (string dir in new[] { _feed, _toolPath, _home, _workspace, _sharedBase })
            Directory.CreateDirectory(dir);

        // The shared base is a Git repository by filesystem evidence only — a single-line `gitdir:`
        // marker pointing at an existing directory — so the shared server can open it without launching Git.
        Directory.CreateDirectory(Path.Combine(_sharedBase, ".git"));
        Directory.CreateDirectory(Path.Combine(_workspace, ".scrinia"));
        WriteJson(Path.Combine(_home, "config.json"), new
        {
            schemaVersion = 1,
            sharedBases = new Dictionary<string, object>
            {
                ["team"] = new { root = _sharedBase }
            }
        });
        WriteJson(Path.Combine(_workspace, ".scrinia", "config.json"), new Dictionary<string, string>
        {
            ["Scrinia:Shared:Enabled"] = "team"
        });

        // Two distinct, non-default versions so the packaged id stays `scrinia` and update has higher ground.
        Pack(dotnet!, FirstVersion);
        Pack(dotnet!, SecondVersion);

        // Install the first version into an empty tool path outside the source checkout.
        Install(_toolPath, FirstVersion).Should().Be(0, "the first global tool version must install from the local feed");

        string[] scriExes = Directory
            .EnumerateFiles(_toolPath, "scri.exe", SearchOption.AllDirectories).ToArray();
        scriExes.Should().NotBeEmpty("the first tool version must install into the isolated tool path");
        _scriExe = scriExes[0];

        // Isolate SCRINIA_HOME process-wide: every child scri/run inherits it and writes runtime data here.
        _previousHome = Environment.GetEnvironmentVariable(ScriniaHomeVar);
        Environment.SetEnvironmentVariable(ScriniaHomeVar, _home);

        _baselinePackageTree = CapturePackageTree(_toolPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ScriniaHomeVar, _previousHome);
        foreach (string dir in new[] { _feed, _toolPath, _home, _workspace, _sharedBase })
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── Phase 5 Task 5 — isolated install/update/uninstall and both server handshakes ─────────────

    [Fact]
    public async Task IsolatedGlobalToolLifecycle_PassesEveryStage()
    {
        ProcessRunner runner = new();
        string dotnet = runner.ResolveExecutable("dotnet")!;
        int timeoutSeconds = 120;

        // ── install: the first package into an empty tool path outside the source checkout ──────────
        _toolPath.Should().NotBeNullOrEmpty("the isolated tool path must be supplied");
        File.Exists(_scriExe).Should().BeTrue("the first tool version must install into the isolated tool path");

        // ── scri --version: reports the freshly installed version and exits zero ────────────────────
        ProcessResult version = Run(_scriExe, ["--version"], null);
        version.ExitCode.Should().Be(0, because: "scri --version must report the installed tool version and exit cleanly");
        version.Stdout.Trim().Should().Be(FirstVersion, $"the freshly installed global tool must report {FirstVersion}");

        // ── handshake scri serve (local MCP server) over stdio ──────────────────────────────────────
        (int serveExit, string? serveResponse) = await HandshakeServeAsync(
            [ "serve", "--no-auto-setup", "--workspace-root", _workspace ]);
        serveExit.Should().Be(0, because: "scri serve must complete the MCP initialize/initialized handshake and exit cleanly");
        ExtractServerInfo(serveResponse).Should().Be("scri",
            "the local serve server must advertise the scri command as its MCP server identity");

        // ── handshake scri shared serve (shared MCP server) over stdio ──────────────────────────────
        (int sharedExit, string? sharedResponse) = await HandshakeServeAsync(
            [ "shared", "serve", "--workspace-root", _workspace ]);
        sharedExit.Should().Be(0, because: "scri shared serve must complete the MCP initialize/initialized handshake and exit cleanly");
        ExtractServerInfo(sharedResponse).Should().Be("scri",
            "the shared serve server must advertise the scri command as its MCP server identity");

        // ── immutable package: server use wrote nothing into the tool payload ───────────────────────
        CapturePackageTree(_toolPath).Should().BeEquivalentTo(
            _baselinePackageTree,
            because: "the global tool package is immutable; running the command never writes into it");

        // ── runtime assets stay under the isolated SCRINIA_HOME, never inside the package ───────────
        string modelDir = Path.Combine(_home, "models", "m2v-MiniLM-L6-v2");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "model.safetensors"), "model2vec model.safetensors");
        File.WriteAllText(Path.Combine(modelDir, "vocab.txt"), "model2vec vocab");

        ProcessResult setup = Run(_scriExe, ["setup", "--no-ollama", "--workspace-root", _workspace], null);
        setup.ExitCode.Should().Be(0, because: "setup with the built-in model present completes cleanly");

        Directory
            .EnumerateFiles(_toolPath, "*", SearchOption.AllDirectories)
            .Where(f => f.StartsWith(Path.Combine(_toolPath, "scrinia"), StringComparison.Ordinal))
            .Should().BeEmpty("setup must not create any file inside the immutable global tool package");
        File.Exists(Path.Combine(_home, "models", "m2v-MiniLM-L6-v2", "model.safetensors")).Should().BeTrue(
            "the built-in model resolves under SCRINIA_HOME, never AppContext.BaseDirectory/models");

        // ── update from the second package, then restart both servers against the updated payload ───
        int update = runner.RunAsync(
            dotnet,
            ["tool", "update", "--tool-path", _toolPath, "--add-source", _feed, ToolPackageId, "--version", SecondVersion],
            null,
            Timeout(timeoutSeconds)).GetAwaiter().GetResult().ExitCode;
        update.Should().Be(0, $".dotnet tool update must install {SecondVersion} from the local feed");

        Run(_scriExe, ["--version"], null).Stdout.Trim().Should().Be(SecondVersion,
            $"the updated global tool must report {SecondVersion}");

        (int restartServeExit, string? restartServeResponse) = await HandshakeServeAsync(
            [ "serve", "--no-auto-setup", "--workspace-root", _workspace ]);
        restartServeExit.Should().Be(0, "the updated scri serve must restart and re-complete its MCP handshake");
        ExtractServerInfo(restartServeResponse).Should().Be("scri");

        (int restartSharedExit, string? restartSharedResponse) = await HandshakeServeAsync(
            [ "shared", "serve", "--workspace-root", _workspace ]);
        restartSharedExit.Should().Be(0, "the updated scri shared serve must restart and re-complete its MCP handshake");
        ExtractServerInfo(restartSharedResponse).Should().Be("scri");

        // ── uninstall cleanly: the scri executable is gone from the isolated tool path ──────────────
        int uninstall = runner.RunAsync(
            dotnet,
            ["tool", "uninstall", "--tool-path", _toolPath, ToolPackageId],
            null,
            Timeout(timeoutSeconds)).GetAwaiter().GetResult().ExitCode;
        uninstall.Should().Be(0, "dotnet tool uninstall must remove the global tool from the isolated tool path");

        Directory
            .EnumerateFiles(_toolPath, "scri.exe", SearchOption.AllDirectories)
            .Should().BeEmpty("uninstall must remove the scri executable from the isolated tool path");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    // The MCP stdio server replies to `initialize` on the next stdout line, so the handshake must be
    // interactive (send initialize, read the response, send `initialized`, then EOF) — the one-shot
    // ProcessRunner closes stdin before the response flushes and would capture an empty stdout.
    private async Task<(int ExitCode, string? InitializeResponse)> HandshakeServeAsync(IReadOnlyList<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(_scriExe!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to launch scri serve");

        // Drain stderr so its buffer can never fill and deadlock the stdout read.
        Task<string?> stderrTask = Task.Run(() => process.StandardError.ReadToEndAsync());

        // 1) initialize — the server replies with the JSON-RPC initialize response on the next stdout line.
        await process.StandardInput.WriteAsync(InitializeRequest + "\n").ConfigureAwait(false);
        string? response = await ReadLineAsync(process.StandardOutput, 90).ConfigureAwait(false);
        if (response is null)
        {
            int exitNull = await CloseAndExitAsync(process, 30).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return (exitNull, null);
        }

        // 2) `initialized` notification, then EOF. shared serve has no shutdown method (-32601), so EOF is
        //    the only way to end the session cleanly.
        await process.StandardInput.WriteAsync(InitializedNotification + "\n").ConfigureAwait(false);
        int exit = await CloseAndExitAsync(process, 30).ConfigureAwait(false);

        await stderrTask.ConfigureAwait(false);
        return (exit, response);
    }

    private static async Task<string?> ReadLineAsync(TextReader reader, int timeoutSeconds)
    {
        Task<string?> readTask = reader.ReadLineAsync();
        Task completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);
        return completed == readTask ? await readTask.ConfigureAwait(false) : null;
    }

    private static async Task<int> CloseAndExitAsync(System.Diagnostics.Process process, int timeoutSeconds)
    {
        try { process.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* stdin already gone */ }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        bool exited = false;
        try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); exited = true; }
        catch (OperationCanceledException) { exited = false; }
        return exited ? process.ExitCode : -1;
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

    private void Pack(string dotnet, string version)
    {
        ProcessResult result = Run(dotnet,
        [
            "pack", RepoPaths.Combine("src", "Scrinia", "Scrinia.csproj"),
            "-c", "Release", "-o", _feed, $"-p:Version={version}"
        ], null);

        result.ExitCode.Should().Be(0, $".dotnet pack -p:Version={version} must succeed:\n{result.Stderr}");

        string[] nupkgs = Directory.GetFiles(_feed, $"scrinia.{version}.nupkg");
        nupkgs.Should().HaveCount(1,
            $"exactly one scrinia.{version} package must land in the local feed");
        File.Exists(nupkgs[0]).Should().BeTrue("the packed global tool package must be discoverable in the local feed");
    }

    private int Install(string toolPath, string version)
    {
        ProcessRunner runner = new();
        string dotnet = runner.ResolveExecutable("dotnet")!;

        ProcessResult result = runner.RunAsync(dotnet,
        [
            "tool", "install", "--tool-path", toolPath,
            "--add-source", _feed, ToolPackageId, "--version", version
        ], null, Timeout(180)).GetAwaiter().GetResult();

        result.ExitCode.Should().Be(0, $".dotnet tool install {ToolPackageId}@{version} must succeed:\n{result.Stderr}");
        return result.ExitCode;
    }

    private static Dictionary<string, byte[]> CapturePackageTree(string toolPath)
    {
        // dotnet keeps the shipped payload under <tool-path>/.store/scrinia/<version>/tools/; the launcher
        // shim sits at the tool-path root. .stage/ is dotnet's transient bookkeeping and is excluded so the
        // immutability assertion targets only the installed package payload and its launcher shim.
        string payloadRoot = Directory.Exists(Path.Combine(toolPath, ".store", "scrinia"))
            ? Path.Combine(toolPath, ".store", "scrinia")
            : toolPath;

        return Directory
            .EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Where(p => !ContainsStageSegment(p, toolPath))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToDictionary(
                p => p.Substring(payloadRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/'),
                p => File.ReadAllBytes(p));

        static bool ContainsStageSegment(string path, string toolPath)
        {
            string relative = path.Substring(toolPath.Length).TrimStart(Path.DirectorySeparatorChar, '/');
            return relative
                .Split(Path.DirectorySeparatorChar, '/')
                .Contains(".stage", StringComparer.Ordinal);
        }
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
