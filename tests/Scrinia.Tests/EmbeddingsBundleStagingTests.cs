using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class EmbeddingsBundleStagingTests
{
    private static readonly Lazy<(int ExitCode, string Output)> Build = new(() => Run(
        "build", RepoPaths.Combine("tools", "Scrinia.ReleaseTools"), "-c", "Release"));

    [Theory]
    [InlineData("win-x64", "scri-plugin-embeddings.exe", "ggml-base.dll,ggml-cpu.dll,ggml-vulkan.dll,ggml.dll,llama.dll,mtmd.dll")]
    [InlineData("linux-x64", "scri-plugin-embeddings", "libggml-base.so,libggml-cpu.so,libggml-vulkan.so,libggml.so,libllama.so,libmtmd.so")]
    [InlineData("osx-arm64", "scri-plugin-embeddings", "ggml-metal.metal,libggml-base.dylib,libggml-blas.dylib,libggml-cpu.dylib,libggml-metal.dylib,libggml.dylib,libllama.dylib,libmtmd.dylib")]
    public void Stage_UsesExactPackageSourcesAndVersionedInventory(string rid, string executable, string names)
    {
        using var fixture = new Fixture(rid, executable);
        var result = fixture.Stage();
        Assert.True(result.ExitCode == 0, result.Output);
        string[] expected = names.Split(',').Append(executable).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, Directory.GetFiles(fixture.Payload).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(fixture.Manifest));
        Assert.Equal(1, manifest.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("scri-plugin-embeddings", manifest.RootElement.GetProperty("plugin").GetString());
        Assert.Equal(fixture.Version, manifest.RootElement.GetProperty("pluginVersion").GetString());
        Assert.Equal(rid, manifest.RootElement.GetProperty("rid").GetString());
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(expected, files.Select(f => f.GetProperty("name").GetString()));
        foreach (var file in files)
        {
            string name = file.GetProperty("name").GetString()!;
            byte[] source = File.ReadAllBytes(fixture.Sources[name]);
            Assert.Equal(source, File.ReadAllBytes(Path.Combine(fixture.Payload, name)));
            Assert.Equal(source.LongLength, file.GetProperty("length").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(source)), file.GetProperty("sha256").GetString());
        }
        Assert.False(File.ReadAllBytes(fixture.Manifest).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(File.GetUnixFileMode(fixture.Sources[executable]), File.GetUnixFileMode(Path.Combine(fixture.Payload, executable)));
        Assert.Equal(0, fixture.Stage().ExitCode); // Identical staging is repeatable.
    }

    [Fact]
    public void Stage_MissingApprovedNativeCannotPublishCompleteManifest()
    {
        using var fixture = new Fixture("win-x64", "scri-plugin-embeddings.exe");
        File.Delete(fixture.Sources["ggml-cpu.dll"]);
        var result = fixture.Stage();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ggml-cpu.dll", result.Output);
        Assert.False(File.Exists(fixture.Manifest));
        Assert.False(Directory.Exists(fixture.Payload));
    }

    [Fact]
    public void Stage_DifferentExistingPayloadIsConflictAndPreservesUnknownDestinations()
    {
        using var fixture = new Fixture("win-x64", "scri-plugin-embeddings.exe");
        Assert.Equal(0, fixture.Stage().ExitCode);
        string state = Path.Combine(fixture.Root, "plugins", "scri-plugin-embeddings", "state", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "state");
        string unknown = Path.Combine(fixture.Payload, "keep.txt");
        File.WriteAllText(unknown, "unknown");
        Assert.NotEqual(0, fixture.Stage().ExitCode);
        Assert.Equal("unknown", File.ReadAllText(unknown));
        Assert.Equal("state", File.ReadAllText(state));
        File.Delete(unknown);
        File.WriteAllText(fixture.Sources["ggml-cpu.dll"], "changed cpu source");
        Assert.NotEqual(0, fixture.Stage().ExitCode);
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("total")]
    public void Stage_RejectsOversizedSourcesBeforePublication(string limit)
    {
        using var fixture = new Fixture("win-x64", "scri-plugin-embeddings.exe");
        string[] oversized = limit == "file" ? ["ggml-cpu.dll"] : ["ggml-cpu.dll", "ggml-base.dll", "llama.dll"];
        foreach (string name in oversized)
        {
            using var stream = new FileStream(fixture.Sources[name], FileMode.Open, FileAccess.Write);
            stream.SetLength(limit == "file" ? 512L * 1024 * 1024 + 1 : 400L * 1024 * 1024);
        }
        Assert.NotEqual(0, fixture.Stage().ExitCode);
        Assert.False(File.Exists(fixture.Manifest));
        Assert.False(Directory.Exists(fixture.Payload));
    }

    [Fact]
    public void Stage_RejectsUnsafeVersionBeforeWritingOutsideVersionDirectory()
    {
        using var fixture = new Fixture("win-x64", "scri-plugin-embeddings.exe");
        Assert.NotEqual(0, fixture.Stage("../escape").ExitCode);
        Assert.False(File.Exists(fixture.Manifest));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("traversal")]
    public void Stage_RejectsUnsafePolicyBeforePublishing(string violation)
    {
        using var fixture = new Fixture("win-x64", "scri-plugin-embeddings.exe");
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(RepoPaths.Combine("release", "embeddings-payloads.json")))!;
        var natives = document["win-x64"]!["nativeFiles"]!.AsArray();
        if (violation == "count")
        {
            for (int i = 0; i < 33; i++) natives.Add(natives[0]!.DeepClone());
        }
        else natives[0]!["packageRelativePath"] = "../escape.dll";
        string policyPath = Path.Combine(fixture.Root, "fixture-policy.json");
        File.WriteAllText(policyPath, document.ToJsonString());
        Assert.NotEqual(0, fixture.Stage(policyPath: policyPath).ExitCode);
        Assert.False(File.Exists(fixture.Manifest));
        Assert.False(Directory.Exists(fixture.Payload));
    }

    [Fact]
    public void Stage_RejectsLinkedSourceAndPreservesTheLinkTarget()
    {
        using var fixture = new Fixture("win-x64", "scri-plugin-embeddings.exe");
        string source = fixture.Sources["ggml-cpu.dll"];
        string target = Path.Combine(fixture.Root, "link-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        File.Delete(source);
        // Directory links on Windows can use a junction without requiring developer mode.
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{source}\" \"{target}\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = System.Diagnostics.Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        else File.CreateSymbolicLink(source, Path.Combine(target, "keep.txt"));
        try
        {
            var result = fixture.Stage();
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("link", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(fixture.Manifest));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
        }
        finally
        {
            if (OperatingSystem.IsWindows()) Directory.Delete(source);
            else File.Delete(source);
        }
    }

    [SkippableFact]
    public void Stage_RepeatRejectsLostUnixExecutePermissionWithoutChangingExistingPayload()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.If(true, "Unix file modes require a native Unix host");
            return;
        }
        using var fixture = new Fixture("linux-x64", "scri-plugin-embeddings");
        Assert.Equal(0, fixture.Stage().ExitCode);
        string executable = Path.Combine(fixture.Payload, "scri-plugin-embeddings");
        byte[] bytes = File.ReadAllBytes(executable);
        UnixFileMode mode = File.GetUnixFileMode(executable) &
            ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(executable, mode);
        string state = Path.Combine(fixture.Root, "plugins", "scri-plugin-embeddings", "state", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "keep");

        var result = fixture.Stage();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("mode", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.Manifest));
        Assert.Equal(bytes, File.ReadAllBytes(executable));
        Assert.Equal(mode, File.GetUnixFileMode(executable));
        Assert.Equal("keep", File.ReadAllText(state));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".embeddings-stage-*"));
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"scrinia-staging-{Guid.NewGuid():N}");
        private readonly string _raw;
        private readonly string _packages;
        private readonly string _rid;
        private readonly string _policy = RepoPaths.Combine("release", "embeddings-payloads.json");
        public string Version => "9.8.7";
        public string Root { get; }
        public string Payload => Path.Combine(Root, "plugins", "scri-plugin-embeddings", Version, _rid);
        public string Manifest => Path.Combine(Root, "embeddings-bundle.json");
        public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);

        public Fixture(string rid, string executable)
        {
            _rid = rid;
            _raw = Directory.CreateDirectory(Path.Combine(_directory, "raw")).FullName;
            _packages = Directory.CreateDirectory(Path.Combine(_directory, "packages")).FullName;
            Root = Directory.CreateDirectory(Path.Combine(_directory, "root")).FullName;
            string executablePath = Path.Combine(_raw, executable);
            File.WriteAllText(executablePath, "published executable");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Sources.Add(executable, executablePath);
            File.WriteAllText(Path.Combine(_raw, "extra.pdb"), "excluded debug symbols");
            if (!File.Exists(_policy)) return;
            using var policy = JsonDocument.Parse(File.ReadAllBytes(_policy));
            foreach (var native in policy.RootElement.GetProperty(rid).GetProperty("nativeFiles").EnumerateArray())
            {
                string name = native.GetProperty("name").GetString()!;
                string package = native.GetProperty("package").GetString()!;
                string source = Path.Combine(_packages, package, native.GetProperty("packageVersion").GetString()!, native.GetProperty("packageRelativePath").GetString()!);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.WriteAllText(source, $"package:{package}:{name}");
                File.WriteAllText(Path.Combine(_raw, name), $"wrong raw duplicate:{name}");
                Sources.Add(name, source);
            }
        }

        public (int ExitCode, string Output) Stage(string? version = null, string? policyPath = null)
        {
            var build = Build.Value;
            if (build.ExitCode != 0) return build;
            return Run("run", "--project", RepoPaths.Combine("tools", "Scrinia.ReleaseTools"), "-c", "Release", "--no-build", "--",
                "stage-embeddings", "--raw", _raw, "--root", Root, "--rid", _rid, "--version", version ?? Version, "--nuget-root", _packages, "--policy", policyPath ?? _policy);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
