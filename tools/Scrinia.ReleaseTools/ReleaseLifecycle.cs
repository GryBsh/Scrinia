using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Commands;
using Scrinia.Core.Embeddings;

namespace Scrinia.ReleaseTools;

/// <summary>Build-only isolated validation of exact package and extracted-host bytes.</summary>
internal sealed class ReleaseLifecycle
{
    private const string Candidate = "0.6.0";
    private const string Baseline = "0.6.0-validation-baseline.1";
    private readonly string output;
    private readonly string root;
    private readonly string home;
    private readonly string workspace;
    private readonly string shared;
    private readonly string tool;
    private int sequence;

    private ReleaseLifecycle(string output)
    {
        this.output = Path.GetFullPath(output);
        if (Directory.Exists(this.output) || File.Exists(this.output)) throw new IOException("Lifecycle output must be a new directory.");
        Directory.CreateDirectory(this.output);
        root = Path.Combine(Path.GetTempPath(), "scri-validation-" + Guid.NewGuid().ToString("N"));
        home = Path.Combine(root, "home");
        workspace = Path.Combine(root, "workspace");
        shared = Path.Combine(root, "shared-base");
        tool = Path.Combine(root, "tool");
        foreach (string directory in new[] { root, home, workspace, shared, tool }) Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(this.output, "isolated-root.txt"), root);
        File.WriteAllText(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"10.0.203\",\"rollForward\":\"disable\",\"allowPrerelease\":false}}");
        Directory.CreateDirectory(Path.Combine(shared, ".git"));
    }

    internal static async Task RunAsync(string package, string baselinePackage, string bundleRoot, string output)
    {
        CheckPackage(package, Candidate);
        CheckPackage(baselinePackage, Baseline);
        string rid = EmbeddingsRuntimeIdentity.CurrentRid;
        var bundle = EmbeddingsBundleValidator.Validate(bundleRoot, Candidate, rid);
        var lifecycle = new ReleaseLifecycle(output);
        try { await lifecycle.ExecuteAsync(Path.GetFullPath(package), Path.GetFullPath(baselinePackage), bundle); }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(lifecycle.output, "failure.txt"), error.ToString());
            try { ZipFile.CreateFromDirectory(lifecycle.root, Path.Combine(lifecycle.output, "failed-isolated-state.zip")); }
            catch (Exception preservation) when (preservation is IOException or UnauthorizedAccessException)
            { File.WriteAllText(Path.Combine(lifecycle.output, "state-preservation-failure.txt"), preservation.ToString()); }
            throw;
        }
    }

    private static void CheckPackage(string package, string version)
    {
        using var zip = ZipFile.OpenRead(package);
        var nuspec = zip.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var input = nuspec.Open();
        var metadata = XDocument.Load(input).Descendants().Single(node => node.Name.LocalName == "metadata");
        if (metadata.Elements().Single(node => node.Name.LocalName == "id").Value != "scrinia" ||
            metadata.Elements().Single(node => node.Name.LocalName == "version").Value != version)
            throw new InvalidDataException($"Package '{package}' must have ID 'scrinia' and version '{version}'.");
        var settings = zip.Entries.Single(entry => entry.FullName.EndsWith("DotnetToolSettings.xml", StringComparison.Ordinal));
        using var settingsInput = settings.Open();
        var commands = XDocument.Load(settingsInput).Descendants("Command").ToArray();
        if (commands.Length != 1 || commands[0].Attribute("Name")?.Value != "scri" ||
            zip.Entries.Any(entry => entry.FullName.Contains("Scrinia.ReleaseTools", StringComparison.Ordinal)))
            throw new InvalidDataException("Package must contain sole scri command and exclude build tooling.");
    }

    private async Task ExecuteAsync(string package, string baselinePackage, ValidatedEmbeddingsBundle bundle)
    {
        var sourceTree = Snapshot(bundle.Root);
        string feed = Path.Combine(root, "feed"), baselineFeed = Path.Combine(root, "baseline-feed");
        Directory.CreateDirectory(feed); Directory.CreateDirectory(baselineFeed);
        File.Copy(package, Path.Combine(feed, Path.GetFileName(package)));
        File.Copy(baselinePackage, Path.Combine(baselineFeed, Path.GetFileName(baselinePackage)));
        string config = Path.Combine(root, "NuGet.Config");
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "baseline"), new XAttribute("value", baselineFeed)),
            new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed))))) .Save(config);
        await RunProcess("install-baseline", "dotnet", ["tool", "install", "scrinia", "--tool-path", tool, "--version", Baseline, "--configfile", config]);
        string executable = Path.Combine(tool, OperatingSystem.IsWindows() ? "scri.exe" : "scri");
        await ExerciseHost(executable, Baseline, "baseline");
        await RunProcess("update-candidate", "dotnet", ["tool", "update", "scrinia", "--tool-path", tool, "--version", Candidate, "--configfile", config]);
        await ExerciseHost(executable, Candidate, "candidate");
        var packageTree = Snapshot(tool);

        // Separate fixture evidence uses the established verified downloader seam, not a runtime URL override.
        await FixtureSetup(bundle);
        string fixturePayload = Path.Combine(root, "fixture-home", "plugins", "scri-plugin-embeddings", Candidate, bundle.Manifest.Rid);
        await RunProcess("native-payload", "dotnet", [typeof(ReleaseLifecycle).Assembly.Location, "native-probe", "--payload", fixturePayload, "--rid", bundle.Manifest.Rid]);
        await Handshake("installed-plugin", Path.Combine(fixturePayload, OperatingSystem.IsWindows() ? "scri-plugin-embeddings.exe" : "scri-plugin-embeddings"),
            ["--data-dir", Path.Combine(root, "plugin-data"), "--models-dir", Path.Combine(root, "absent-models")], Candidate, "scrinia-plugin-embeddings");

        // These are required actual public-process setup smokes. Network failure is retained and fails the lane.
        string publicHome = Path.Combine(root, "public-home");
        await RunProcess("candidate-public-setup", executable,
            ["setup", "--no-ollama", "--embeddings-source", bundle.Root, "--workspace-root", Path.Combine(root, "public-workspace")],
            stageHome: publicHome, seconds: 1200);
        AssertTree(tool, packageTree);
        string extracted = Path.Combine(bundle.Root, OperatingSystem.IsWindows() ? "scri.exe" : "scri");
        await RunProcess("extracted-public-setup", extracted,
            ["setup", "--no-ollama", "--workspace-root", Path.Combine(root, "extracted-workspace")],
            stageHome: Path.Combine(root, "extracted-home"), seconds: 1200);
        AssertTree(bundle.Root, sourceTree);
        await ExerciseHost(extracted, Candidate, "extracted");
        AssertTree(bundle.Root, sourceTree);
        await RunProcess("uninstall", "dotnet", ["tool", "uninstall", "scrinia", "--tool-path", tool]);
        if (File.Exists(executable)) throw new InvalidOperationException("Uninstall left the scri shim.");
        File.WriteAllText(Path.Combine(output, "complete.txt"), "All required package, setup, native payload and stdio lifecycle stages passed.\n");
    }

    private async Task ExerciseHost(string executable, string version, string prefix)
    {
        var before = Snapshot(Path.GetDirectoryName(executable)!);
        string actual = await RunProcess(prefix + "-version", executable, ["--version"]);
        if (actual.Trim() != version) throw new InvalidDataException($"Command version mismatch: expected '{version}', received '{actual.Trim()}'.");
        await RunProcess(prefix + "-register-shared", executable, ["shared", "base", "add", "team", shared]);
        await RunProcess(prefix + "-enable-shared", executable, ["shared", "enable", "team"]);
        await Handshake(prefix + "-serve", executable, ["serve", "--no-auto-setup", "--workspace-root", workspace], version, "scri");
        await Handshake(prefix + "-shared-serve", executable, ["shared", "serve", "--workspace-root", workspace], version, "scri", sharedOperations: true);
        string content = Path.Combine(root, "input.txt"); File.WriteAllText(content, "exact candidate lifecycle content");
        await RunProcess(prefix + "-store", executable, ["store", "/validation/candidate", content, "--workspace-root", workspace]);
        string shown = await RunProcess(prefix + "-show", executable, ["show", "/validation/candidate", "--workspace-root", workspace]);
        if (!shown.Contains("exact candidate lifecycle content", StringComparison.Ordinal)) throw new InvalidDataException("Stored content was not recalled.");
        await RunProcess(prefix + "-forget", executable, ["forget", "/validation/candidate", "--workspace-root", workspace]);
        AssertTree(Path.GetDirectoryName(executable)!, before);
    }

    private async Task FixtureSetup(ValidatedEmbeddingsBundle bundle)
    {
        string? oldHome = Environment.GetEnvironmentVariable("SCRINIA_HOME");
        TextWriter oldOut = Console.Out, oldError = Console.Error;
        string stem = Next("verified-http-fixture-setup");
        using var stdout = new StreamWriter(stem + ".stdout.log");
        using var stderr = new StreamWriter(stem + ".stderr.log");
        var timer = Stopwatch.StartNew();
        try
        {
            Environment.SetEnvironmentVariable("SCRINIA_HOME", Path.Combine(root, "fixture-home"));
            Console.SetOut(stdout); Console.SetError(stderr);
            byte[] bytes = "tiny verified release validation HTTP asset"u8.ToArray();
            var asset = new DownloadableAsset("release-fixture", "https://example.invalid/fixture", "tiny.bin", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            int exit = await new ScriniaCommands().SetupCoreAsync(Path.Combine(root, "fixture-workspace"), true, false, false, false,
                bundle.Root, bundle.Root, Candidate, bundle.Manifest.Rid,
                prepareModels: ct => ScriniaCommands.EnsureVerifiedModelAssetsAsync(Path.Combine(root, "fixture-home", "models", "fixture"),
                    [asset], () => new HttpClient(new FixtureHttpHandler(bytes)), NullLogger.Instance, ct));
            if (exit != 0) throw new InvalidOperationException("Verified HTTP fixture setup failed.");
            Outcome(stem, "in-process existing setup seam", 0, timer.Elapsed, null);
        }
        catch (Exception error) { Outcome(stem, "in-process existing setup seam", 1, timer.Elapsed, error.Message); throw; }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); Environment.SetEnvironmentVariable("SCRINIA_HOME", oldHome); }
    }

    private sealed class FixtureHttpHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private Process Start(string executable, string[] args, string? stageHome = null)
    {
        var process = new Process { StartInfo = new(executable)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workspace } };
        foreach (string arg in args) process.StartInfo.ArgumentList.Add(arg);
        foreach (string key in process.StartInfo.Environment.Keys.Where(key => key.StartsWith("Scrinia", StringComparison.OrdinalIgnoreCase)).ToArray())
            process.StartInfo.Environment.Remove(key);
        process.StartInfo.Environment["SCRINIA_HOME"] = stageHome ?? home;
        process.StartInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(root, "dotnet-home");
        process.StartInfo.Environment["NUGET_PACKAGES"] = Path.Combine(root, "nuget-cache");
        return process;
    }

    private async Task<string> RunProcess(string stage, string executable, string[] args, string? stageHome = null, int seconds = 180)
    {
        using var process = Start(executable, args, stageHome);
        return await Capture(stage, process, seconds, async (p, ct, record) =>
        { p.StandardInput.Close(); await Drain(p.StandardOutput, record, ct); });
    }

    private async Task Handshake(string stage, string executable, string[] args, string version, string name, bool sharedOperations = false)
    {
        using var process = Start(executable, args);
        await Capture(stage, process, 60, async (p, ct, record) =>
        {
            await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"release-validation\",\"version\":\"1.0\"}}}");
            await p.StandardInput.FlushAsync(ct);
            string line = await p.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidDataException("Server ended before initialize.");
            record(line + "\n");
            using var response = JsonDocument.Parse(line);
            var info = response.RootElement.GetProperty("result").GetProperty("serverInfo");
            if (response.RootElement.GetProperty("id").GetInt32() != 1 || info.GetProperty("version").GetString() != version || info.GetProperty("name").GetString() != name)
                throw new InvalidDataException($"Stdio server identity does not match expected name '{name}' and version '{version}'.");
            await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await p.StandardInput.FlushAsync(ct);
            if (sharedOperations) await ExerciseSharedOperations(p, record, ct);
            p.StandardInput.Close();
            string? extra;
            while ((extra = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                record(extra + "\n");
                using (JsonDocument.Parse(extra)) { }
            }
        });
    }

    private static async Task ExerciseSharedOperations(Process process, Action<string> record, CancellationToken ct)
    {
        // Exercise the actual registered transport handlers, including dictionary-bearing schemas.
        (string Tool, string Arguments, bool Error)[] calls =
        [
            ("shared_bases", "{}", false),
            ("shared_remember", "{\"base\":\"team\",\"path\":\"/validation/one\",\"description\":\"Shared validation chunks and project attributes.\",\"content\":[\"shared first chunk\",\"second chunk\"],\"attributes\":{\"project\":[\"validation\"]}}", false),
            ("shared_remember", "{\"base\":\"team\",\"path\":\"/validation/one\",\"description\":\"Shared validation chunks and project attributes.\",\"content\":[\"third chunk\"]}", false),
            ("shared_recall", "{\"base\":\"team\",\"path\":\"/validation/one\",\"chunk\":2}", false),
            ("shared_search", "{\"query\":\"shared\",\"base\":\"team\",\"attributeFilter\":{\"project\":[\"validation\"]}}", false),
            ("shared_list", "{\"base\":\"team\",\"mode\":\"full\"}", false),
            ("shared_facts", "{\"base\":\"team\",\"op\":\"entities\"}", false),
            ("shared_remember", "{\"base\":\"team\",\"path\":\"/validation/other\",\"description\":\"Related validation memory.\",\"content\":[\"other body\"]}", false),
            ("shared_link", "{\"base\":\"team\",\"path\":\"/validation/one\",\"destination\":\"/validation/other\"}", false),
            ("shared_compact", "{\"base\":\"team\",\"path\":\"/validation/one\"}", false),
            ("shared_refresh", "{\"base\":\"team\"}", false),
            ("shared_forget", "{\"base\":\"team\",\"path\":\"/validation/other\"}", false),
            ("shared_list", "{\"base\":\"team\",\"unexpected\":\"rejected\"}", true),
            ("shared_recall", "{\"base\":\"team\",\"path\":\"~temporary\"}", true),
            ("shared_list", "{\"limit\":\"wrong type\"}", true),
            ("shared_remember", "{\"base\":\"team\",\"path\":\"/validation/bad\",\"description\":\"Invalid content validation.\",\"content\":null}", true),
            ("shared_recall", "{\"base\":\"private\",\"path\":\"/secret\"}", true),
        ];
        int id = 2;
        foreach (var call in calls)
        {
            await process.StandardInput.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{call.Tool}\",\"arguments\":{call.Arguments}}}}}");
            await process.StandardInput.FlushAsync(ct);
            string line = await process.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidDataException($"Shared host ended during {call.Tool}.");
            record(line + "\n");
            using var response = JsonDocument.Parse(line);
            var message = response.RootElement;
            if (message.GetProperty("id").GetInt32() != id || message.TryGetProperty("error", out _) ||
                !message.TryGetProperty("result", out var result) ||
                (result.TryGetProperty("isError", out var error) && error.GetBoolean()) != call.Error ||
                (call.Error && !result.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("status: error", StringComparison.Ordinal)))
                throw new InvalidDataException($"Required shared operation {call.Tool} returned an unexpected outcome; response is retained in stdout.");
            id++;
        }
    }

    private async Task<string> Capture(string stage, Process process, int seconds, Func<Process, CancellationToken, Action<string>, Task> read)
    {
        string stem = Next(stage);
        var timer = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Task? stderr = null;
        var stdout = new StringBuilder();
        using var stdoutLog = new StreamWriter(stem + ".stdout.log") { AutoFlush = true };
        using var stderrLog = new StreamWriter(stem + ".stderr.log") { AutoFlush = true };
        try
        {
            process.Start(); stderr = Drain(process.StandardError, text => stderrLog.Write(text), timeout.Token);
            await read(process, timeout.Token, text => { stdout.Append(text); stdoutLog.Write(text); });
            await process.WaitForExitAsync(timeout.Token);
            await stderr.WaitAsync(timeout.Token);
            Outcome(stem, process.StartInfo.FileName + " " + string.Join(" ", process.StartInfo.ArgumentList), process.ExitCode, timer.Elapsed, null);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Required lifecycle stage {stage} exited {process.ExitCode}; see {stem}.");
            return stdout.ToString();
        }
        catch (Exception error)
        {
            if (stderr is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    timeout.Cancel();
                    await stderr.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception cleanup) when (cleanup is InvalidOperationException or OperationCanceledException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
                { File.WriteAllText(stem + ".cleanup.log", cleanup.Message); }
            }
            int exit = -1;
            try { if (stderr is not null && process.HasExited) exit = process.ExitCode; }
            catch (InvalidOperationException) { }
            Outcome(stem, process.StartInfo.FileName + " " + string.Join(" ", process.StartInfo.ArgumentList), exit, timer.Elapsed, error.Message);
            throw;
        }
    }

    private static async Task Drain(StreamReader reader, Action<string> record, CancellationToken ct)
    {
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0) record(new string(buffer, 0, count));
    }

    private string Next(string stage) => Path.Combine(output, $"{++sequence:D2}-{stage}");
    private static void Outcome(string stem, string command, int exit, TimeSpan elapsed, string? error)
    {
        using var stream = File.Create(stem + ".outcome.json");
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject(); writer.WriteString("command", command); writer.WriteNumber("exitCode", exit);
        writer.WriteNumber("elapsedSeconds", elapsed.TotalSeconds); writer.WriteString("error", error); writer.WriteEndObject();
    }
    private static Dictionary<string, string> Snapshot(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), path =>
        { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }, StringComparer.Ordinal);
    private static void AssertTree(string directory, Dictionary<string, string> expected)
    {
        var actual = Snapshot(directory);
        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out string? hash) || hash != pair.Value))
            throw new InvalidDataException($"Runtime mutated immutable package/source tree: {directory}");
    }
}
