using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class ReleaseArtifactIdentityTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] AggregateDependencies = ["build-check", "native"];
    private static readonly Lazy<string> Tool = new(() => ResolveTool(new DirectoryInfo(AppContext.BaseDirectory)));

    private static string ResolveTool(DirectoryInfo output)
    {
        string path = output.Parent is { Name: "Scrinia.Tests", Parent.Name: "bin" } project
            ? Path.Combine(project.Parent.FullName, "Scrinia.ReleaseTools", output.Name, "Scrinia.ReleaseTools.dll")
            : RepoPaths.Combine("tools", "Scrinia.ReleaseTools", "bin", output.Parent!.Name, output.Name, "Scrinia.ReleaseTools.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("The current test configuration's release tool was not built.", path);
        return path;
    }

    [Fact]
    public void ReleaseTool_MissingCurrentConfigurationNeverFallsBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "scri-tool-pivot-" + Guid.NewGuid().ToString("N"));
        try
        {
            string release = Path.Combine(root, "bin", "Scrinia.ReleaseTools", "release");
            Directory.CreateDirectory(release);
            File.WriteAllText(Path.Combine(release, "Scrinia.ReleaseTools.dll"), "stale other configuration");
            var output = new DirectoryInfo(Path.Combine(root, "bin", "Scrinia.Tests", "debug"));
            var error = Assert.Throws<FileNotFoundException>(() => ResolveTool(output));
            Assert.Equal(Path.Combine(root, "bin", "Scrinia.ReleaseTools", "debug", "Scrinia.ReleaseTools.dll"), error.FileName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReleaseTool_UsesTheCurrentTestBuildOutput()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        string expected = output.Parent is { Name: "Scrinia.Tests", Parent.Name: "bin" } project
            ? Path.Combine(project.Parent.FullName, "Scrinia.ReleaseTools", output.Name, "Scrinia.ReleaseTools.dll")
            : RepoPaths.Combine("tools", "Scrinia.ReleaseTools", "bin", output.Parent!.Name, output.Name, "Scrinia.ReleaseTools.dll");
        Assert.Equal(expected, Tool.Value);
        Assert.True(File.Exists(Tool.Value), "The configuration-matched project reference must build the selected tool.");
    }

    [Fact]
    public void VerifyArtifacts_RejectsChangedBytesOfIdenticalLength()
    {
        using var fixture = new Fixture();
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData("before"u8)), Convert.ToHexString(SHA256.HashData("after!"u8)));
        Assert.Equal(0, fixture.Verify().Exit);
        File.WriteAllText(Path.Combine(fixture.Root, fixture.Artifacts[0].Name), "after!");
        Assert.NotEqual(0, fixture.Verify().Exit);
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("version")]
    [InlineData("schema")]
    [InlineData("size")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate-name")]
    [InlineData("duplicate-rid")]
    [InlineData("unsafe")]
    [InlineData("count")]
    public void VerifyArtifacts_RejectsInvalidCandidate(string mutation)
    {
        using var fixture = new Fixture();
        Assert.Equal(0, fixture.Verify().Exit);
        string sha = Sha, version = "0.6.0";
        switch (mutation)
        {
            case "sha": sha = new string('f', 40); break;
            case "version": version = "0.6.1"; break;
            case "schema": fixture.Schema = 2; break;
            case "size": fixture.Artifacts[0] = fixture.Artifacts[0] with { Length = 5 }; break;
            case "missing": File.Delete(Path.Combine(fixture.Root, fixture.Artifacts[1].Name)); break;
            case "extra": File.WriteAllText(Path.Combine(fixture.Root, "extra.tar.gz"), "before"); break;
            case "duplicate-name": fixture.Artifacts[1] = fixture.Artifacts[1] with { Name = fixture.Artifacts[2].Name }; break;
            case "duplicate-rid": fixture.Artifacts[1] = fixture.Artifacts[1] with { Rid = "linux-x64" }; break;
            case "unsafe": fixture.Artifacts[0] = fixture.Artifacts[0] with { Name = "../escape.nupkg" }; break;
            case "count": fixture.Artifacts.RemoveAt(1); break;
        }
        fixture.WriteManifest();
        Assert.NotEqual(0, fixture.Verify(sha, version).Exit);
    }

    [Fact]
    public void ValidationScript_RejectsWrongRevisionBeforeCreatingOutput()
    {
        string output = Path.Combine(Path.GetTempPath(), "scrinia rejected candidate " + Guid.NewGuid().ToString("N"));
        var result = Run("pwsh", ["-NoProfile", "-File", RepoPaths.Combine("scripts", "release", "Validate-Release.ps1"),
            "-Revision", new string('f', 40), "-Rid", "win-x64", "-Output", output]);
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("Checkout does not match", result.Output);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void NativeProbe_RejectsForeignArchitectureBeforeReadingPayload()
    {
        string foreign = OperatingSystem.IsMacOS() ? "win-x64" : "osx-arm64";
        var result = Run("dotnet", [Tool.Value, "native-probe", "--payload", "missing-payload", "--rid", foreign]);
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("Native RID mismatch", result.Output);
    }

    [Fact]
    public void Workflow_RequiresCurrentSolutionAndEveryNativeLaneBeforeAggregation()
    {
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        using var reader = File.OpenText(RepoPaths.Combine(".github", "workflows", "release-validation.yml"));
        yaml.Load(reader);
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlDotNet.RepresentationModel.YamlMappingNode)root.Children["jobs"];
        var native = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children["native"];
        Assert.Equal("build-check", native.Children["needs"].ToString());
        var aggregate = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children["aggregate"];
        Assert.Equal(AggregateDependencies, ((YamlDotNet.RepresentationModel.YamlSequenceNode)aggregate.Children["needs"]).Select(node => node.ToString()));
        string workflow = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release-validation.yml"));
        Assert.Contains("dotnet build Scrinia.sln -c Release --no-incremental", workflow);
        Assert.Contains("dotnet test Scrinia.sln -c Release --no-build --logger trx", workflow);
        Assert.DoesNotContain("Scrinia.Merge", workflow);
        Assert.DoesNotContain("secrets:", workflow);
        Assert.DoesNotContain("contents: write", workflow);
        Assert.Contains("fail-fast: false", workflow);
        foreach (string rid in new[] { "win-x64", "linux-x64", "osx-arm64" }) Assert.Contains("rid: " + rid, workflow);
    }

    private sealed record Artifact(string Name, long Length, string Sha256, string Kind, string? Rid);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "scrinia artifact identity " + Guid.NewGuid().ToString("N"));
        public List<Artifact> Artifacts { get; } = [];
        public int Schema { get; set; } = 1;
        private string Manifest => Path.Combine(Root, "release-artifacts.json");
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Add("scrinia.0.6.0.nupkg", "nuget", null);
            foreach (string rid in new[] { "win-x64", "linux-x64", "osx-arm64" })
                foreach (string kind in new[] { "plain-archive", "embeddings-archive" })
                    Add($"scrinia-0.6.0-{rid}-{kind}.tar.gz", kind, rid);
            WriteManifest();
        }
        private void Add(string name, string kind, string? rid)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, "before");
            using var stream = File.OpenRead(path);
            Artifacts.Add(new(name, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)), kind, rid));
        }
        public void WriteManifest() => File.WriteAllText(Manifest, JsonSerializer.Serialize(new
        { Version = Schema, Commit = Sha, PackageVersion = "0.6.0", RunId = "fixture", SdkVersion = "10.0.203", Artifacts },
            JsonOptions));
        public (int Exit, string Output) Verify(string sha = Sha, string version = "0.6.0") => Run("dotnet",
            [Tool.Value, "verify-artifacts", "--manifest", Manifest, "--root", Root, "--sha", sha, "--version", version]);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static (int Exit, string Output) Run(string executable, string[] args)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new(executable)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (string arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new TimeoutException(); }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
