using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class ReleasePublicationTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("artifact-missing")]
    [InlineData("artifact-expired")]
    [InlineData("artifact-duplicate")]
    [InlineData("artifact-wrong-sha")]
    [InlineData("artifact-digest-mismatch")]
    [InlineData("artifact-missing-digest")]
    [InlineData("local-consistent-replacement")]
    [InlineData("artifact-unsafe-path")]
    [InlineData("artifact-duplicate-entry")]
    [InlineData("artifact-payload-mismatch")]
    [InlineData("artifact-oversized")]
    public void Provenance_RejectsUnprovenOrReplacedSelectedRunArtifact(string scenario)
    {
        using var fixture = new Fixture(scenario);
        var result = fixture.Run();
        Assert.True(result.Exit != 0, result.Output);
        Assert.Empty(fixture.Writes());
    }

    [Fact]
    public void Provenance_RetryRevalidatesSelectedRunArtifact()
    {
        using var fixture = new Fixture("publish-failure");
        Assert.NotEqual(0, fixture.Run().Exit);
        fixture.SetScenario("artifact-digest-mismatch"); fixture.ClearCalls();
        Assert.NotEqual(0, fixture.Run().Exit);
        Assert.Empty(fixture.Writes());
    }

    [Theory]
    [InlineData("failed-native")]
    [InlineData("wrong-sha")]
    [InlineData("wrong-tag")]
    [InlineData("missing-eligibility")]
    [InlineData("wrong-repository")]
    [InlineData("wrong-workflow")]
    [InlineData("pull-request")]
    [InlineData("changed-hash")]
    [InlineData("missing-secret")]
    [InlineData("missing-owner")]
    [InlineData("skipped-native")]
    [InlineData("duplicate-job")]
    [InlineData("returned-job-attempt-mismatch")]
    [InlineData("attempt-changed")]
    [InlineData("status-changed")]
    [InlineData("head-changed")]
    [InlineData("repository-changed")]
    [InlineData("reusable-missing-reference")]
    [InlineData("nuget-unknown")]
    public void Publication_RejectsInvalidIdentityBeforeAnyWrite(string scenario)
    {
        using var fixture = new Fixture(scenario);
        var result = fixture.Run();
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("Publication refused", result.Output);
        Assert.Empty(fixture.Writes());
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("reusable")]
    public void ValidateOnly_AdmitsProvenValidationWithoutWrites(string scenario)
    {
        using var fixture = new Fixture(scenario);
        var result = fixture.Run(validateOnly: true);
        Assert.True(result.Exit == 0, result.Output);
        Assert.Contains("no publication performed", result.Output);
        Assert.Empty(fixture.Writes());
        Assert.False(File.Exists(fixture.Progress));
    }

    [Theory]
    [InlineData("upload-failure", false)]
    [InlineData("publish-failure", true)]
    [InlineData("push-unknown", true)]
    [InlineData("create-unknown", false)]
    [InlineData("publish-unknown", true)]
    public void Retry_ReconcilesConfirmedExternalOutcomes(string scenario, bool packageWasPushed)
    {
        using var fixture = new Fixture(scenario);
        var first = fixture.Run();
        Assert.NotEqual(0, first.Exit);
        Assert.Contains("Publication refused", first.Output);
        if (scenario == "create-unknown") Assert.False(File.Exists(fixture.Progress));
        else
        {
            Assert.True(File.Exists(fixture.Progress));
            Assert.DoesNotContain("fixture-secret", File.ReadAllText(fixture.Progress));
        }
        Assert.DoesNotContain("fixture-secret", first.Output);
        fixture.SetScenario("direct");
        var second = fixture.Run();
        Assert.True(second.Exit == 0, second.Output);
        using var progress = JsonDocument.Parse(File.ReadAllText(fixture.Progress));
        Assert.True(progress.RootElement.GetProperty("releasePublished").GetBoolean());
        Assert.Equal(7, progress.RootElement.GetProperty("assetsVerified").GetArrayLength());
        Assert.Single(fixture.Writes(), line => line == "push");
        Assert.Equal(packageWasPushed, fixture.WritesBeforeRetry.Contains("push"));
        Assert.DoesNotContain("pack", fixture.Calls());
        Assert.DoesNotContain("publish-compilation", fixture.Calls());
    }

    [Theory]
    [InlineData("identical")]
    [InlineData("signed-identical")]
    public void ExistingIdenticalPackage_IsVerifiedAndNeverPushed(string scenario)
    {
        using var fixture = new Fixture(scenario);
        fixture.PreseedPackage(scenario == "signed-identical");
        var result = fixture.Run();
        Assert.True(result.Exit == 0, result.Output);
        Assert.DoesNotContain("push", fixture.Writes());
        using var progress = JsonDocument.Parse(File.ReadAllText(fixture.Progress));
        Assert.NotEmpty(progress.RootElement.GetProperty("nugetVerified").GetProperty("remoteSha256").GetString()!);
        Assert.NotEmpty(progress.RootElement.GetProperty("nugetVerified").GetProperty("entries").EnumerateArray());
    }

    [Theory]
    [InlineData("different-package")]
    [InlineData("different-opc")]
    [InlineData("different-asset")]
    [InlineData("different-metadata")]
    public void Retry_RefusesDifferentRemoteArtifactWithoutWrites(string scenario)
    {
        using var fixture = new Fixture("publish-failure");
        Assert.NotEqual(0, fixture.Run().Exit);
        fixture.SetScenario(scenario);
        fixture.ClearCalls();
        Assert.NotEqual(0, fixture.Run().Exit);
        Assert.Empty(fixture.Writes());
    }

    [Theory]
    [InlineData("different-package")]
    [InlineData("different-opc")]
    [InlineData("different-asset")]
    public void PublishedDifferentArtifact_RemainsConflict(string scenario)
    {
        using var fixture = new Fixture("direct");
        var first = fixture.Run(); Assert.True(first.Exit == 0, first.Output);
        fixture.SetScenario(scenario); fixture.ClearCalls();
        Assert.NotEqual(0, fixture.Run().Exit);
        Assert.Empty(fixture.Writes());
    }

    [Fact]
    public void NewRunner_RecoversDraftIdentityAfterLostProgress()
    {
        using var fixture = new Fixture("publish-failure");
        Assert.NotEqual(0, fixture.Run().Exit);
        File.Delete(fixture.Progress);
        fixture.SetScenario("direct");
        fixture.ClearCalls();
        var result = fixture.Run();
        Assert.True(result.Exit == 0, result.Output);
        Assert.Equal("publish", Assert.Single(fixture.Writes()));
    }

    [Fact]
    public void Retry_RefusesMissingConfirmedDraftWithoutWritesOrProgressChanges()
    {
        using var fixture = new Fixture("publish-failure");
        Assert.NotEqual(0, fixture.Run().Exit);
        byte[] progress = File.ReadAllBytes(fixture.Progress);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "release.json")));
        File.Delete(Path.Combine(fixture.Root, "release.json"));
        fixture.SetScenario("direct");
        fixture.ClearCalls();

        var result = fixture.Run();

        Assert.NotEqual(0, result.Exit);
        Assert.Empty(fixture.Writes());
        Assert.Equal(progress, File.ReadAllBytes(fixture.Progress));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "release.json")));
    }

    [Fact]
    public void Retry_RejectsChangedProgressBeforeAnyRemoteWrite()
    {
        using var fixture = new Fixture("publish-failure");
        Assert.NotEqual(0, fixture.Run().Exit);
        string progress = File.ReadAllText(fixture.Progress).Replace(Sha, new string('f', 40), StringComparison.Ordinal);
        File.WriteAllText(fixture.Progress, progress);
        fixture.SetScenario("direct"); fixture.ClearCalls();
        Assert.NotEqual(0, fixture.Run().Exit);
        Assert.Empty(fixture.Writes());
    }

    [Fact]
    public void PublicationWorkflow_RequiresReadOnlyPreflightAndApprovedPublishEnvironment()
    {
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        using var reader = File.OpenText(RepoPaths.Combine(".github", "workflows", "release.yml")); yaml.Load(reader);
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;
        var triggers = (YamlDotNet.RepresentationModel.YamlMappingNode)root.Children["on"];
        Assert.Equal("workflow_dispatch", Assert.Single(triggers.Children).Key.ToString());
        var permissions = (YamlDotNet.RepresentationModel.YamlMappingNode)root.Children["permissions"];
        Assert.Equal("read", permissions.Children["contents"].ToString());
        var jobs = (YamlDotNet.RepresentationModel.YamlMappingNode)root.Children["jobs"];
        var preflight = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children["preflight"];
        Assert.False(preflight.Children.ContainsKey("environment"));
        var publish = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children["publish"];
        Assert.Equal("preflight", publish.Children["needs"].ToString());
        Assert.Equal("release", publish.Children["environment"].ToString());
        Assert.Equal("write", ((YamlDotNet.RepresentationModel.YamlMappingNode)publish.Children["permissions"]).Children["contents"].ToString());
        var steps = (YamlDotNet.RepresentationModel.YamlSequenceNode)publish.Children["steps"];
        var retention = (YamlDotNet.RepresentationModel.YamlMappingNode)steps.Children[^1];
        Assert.Equal("always()", retention.Children["if"].ToString());
        foreach (var node in ((YamlDotNet.RepresentationModel.YamlSequenceNode)preflight.Children["steps"]).Children.Cast<YamlDotNet.RepresentationModel.YamlMappingNode>())
            if (node.Children.TryGetValue("env", out var environment))
                Assert.DoesNotContain(((YamlDotNet.RepresentationModel.YamlMappingNode)environment).Children.Values, value => value.ToString().Contains("secrets.", StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "scrinia publication " + Guid.NewGuid().ToString("N"));
        internal string Progress => Path.Combine(Root, "progress.json");
        internal string[] WritesBeforeRetry { get; private set; } = [];
        private string Artifacts => Path.Combine(Root, "artifacts");
        private string Package => Path.Combine(Artifacts, "scrinia.0.6.0.nupkg");
        private string Scenario { get; set; }
        internal Fixture(string scenario)
        {
            Scenario = scenario;
            Directory.CreateDirectory(Artifacts);
            using (var zip = ZipFile.Open(Package, ZipArchiveMode.Create))
            {
                Entry(zip, "scrinia.nuspec", "<package><metadata><id>scrinia</id><version>0.6.0</version></metadata></package>");
                Entry(zip, "package/services/metadata/core-properties/fixed.psmdcp", "<properties><identifier>immutable-guid</identifier></properties>");
                Entry(zip, "tools/net10.0/any/scri.dll", "immutable managed bytes");
            }
            var records = new List<object>();
            Add("scrinia.0.6.0.nupkg", "nuget", null);
            foreach (string rid in new[] { "win-x64", "linux-x64", "osx-arm64" })
                foreach (string kind in new[] { "plain-archive", "embeddings-archive" })
                {
                    string name = $"scrinia-0.6.0-{rid}-{kind}.tar.gz";
                    File.WriteAllText(Path.Combine(Artifacts, name), name);
                    Add(name, kind, rid);
                }
            File.WriteAllText(Path.Combine(Artifacts, "release-artifacts.json"), JsonSerializer.Serialize(new
            { version = 1, commit = Sha, packageVersion = "0.6.0", runId = "42", sdkVersion = "10.0.203", artifacts = records }));
            using (var zip = ZipFile.Open(Path.Combine(Root, "validation.zip"), ZipArchiveMode.Create))
            {
                foreach (string file in Directory.GetFiles(Artifacts))
                    if (scenario == "artifact-payload-mismatch" && Path.GetFileName(file) == "scrinia.0.6.0.nupkg") Entry(zip, "artifacts/scrinia.0.6.0.nupkg", "different server payload");
                    else zip.CreateEntryFromFile(file, "artifacts/" + Path.GetFileName(file));
                if (scenario == "artifact-unsafe-path") Entry(zip, "../outside.txt", "unsafe path");
                if (scenario == "artifact-duplicate-entry") Entry(zip, "ARTIFACTS/release-artifacts.json", "case alias");
            }
            File.WriteAllText(Path.Combine(Root, "scenario.txt"), scenario);
            if (scenario == "changed-hash") File.AppendAllText(Package, "changed");
            if (scenario == "local-consistent-replacement")
            {
                File.AppendAllText(Package, "different envelope");
                var local = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Artifacts, "release-artifacts.json")))!;
                local["artifacts"]![0]!["sha256"] = Hash(Package);
                local["artifacts"]![0]!["length"] = new FileInfo(Package).Length;
                File.WriteAllText(Path.Combine(Artifacts, "release-artifacts.json"), local.ToJsonString());
            }
            void Add(string name, string kind, string? rid)
            {
                string path = Path.Combine(Artifacts, name);
                records.Add(new { name, length = new FileInfo(path).Length, sha256 = Hash(path), kind, rid });
            }
        }
        private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        private static void Entry(ZipArchive zip, string path, string contents)
        { using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(contents); }
        internal void PreseedPackage(bool signed)
        {
            string path = Path.Combine(Root, "remote.nupkg");
            File.Copy(Package, path);
            if (signed) { using var zip = ZipFile.Open(path, ZipArchiveMode.Update); Entry(zip, ".signature.p7s", "service signature"); }
        }
        internal void SetScenario(string scenario)
        { WritesBeforeRetry = Writes(); Scenario = scenario; File.WriteAllText(Path.Combine(Root, "scenario.txt"), scenario); }
        internal string[] Calls() => File.Exists(Path.Combine(Root, "calls.txt")) ? File.ReadAllLines(Path.Combine(Root, "calls.txt")) : [];
        internal string[] Writes() => Calls().Where(x => x is "create" or "upload" or "push" or "publish").ToArray();
        internal void ClearCalls() => File.Delete(Path.Combine(Root, "calls.txt"));
        internal (int Exit, string Output) Run(bool validateOnly = false)
        {
            string tool = ResolveOutput("Scrinia.ReleaseTools", "tools");
            string harness = ResolveOutput("Scrinia.TestProcess", "tests");
            string wrapper = Path.Combine(Root, "run.ps1");
            File.WriteAllText(wrapper, "param($Script,$Harness,$Tool,$Root,$Only)\n$ErrorActionPreference='Stop'\n" +
                "$runner = { param($Executable,$Arguments,$OutputPath)\n" +
                "  & dotnet $Harness publication-transport $Root $Executable $OutputPath @Arguments\n" +
                "  if ($LASTEXITCODE -ne 0) { throw 'Fake transport failed fixture-secret' }\n" +
                "}\n" +
                "& $Script -ValidationRunId 42 -Revision '" + Sha + "' -Version 0.6.0 -ArtifactRoot \"$Root/artifacts\" -ProgressPath \"$Root/progress.json\" -ValidatorPath $Tool -ValidateOnly:($Only -eq 'true') -TestProcessRunner $runner\nexit $LASTEXITCODE\n");
            using var process = new System.Diagnostics.Process { StartInfo = new("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (string arg in new[] { "-NoProfile", "-File", wrapper, RepoPaths.Combine("scripts", "release", "Publish-ValidatedRelease.ps1"), harness, tool, Root, validateOnly ? "true" : "false" }) process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.Environment["GH_TOKEN"] = "";
            process.StartInfo.Environment["GITHUB_TOKEN"] = "";
            process.StartInfo.Environment["NUGET_API_KEY"] = Scenario == "missing-secret" ? "" : "fixture-secret";
            process.StartInfo.Environment["GITHUB_REPOSITORY"] = "owner/scrinia";
            process.StartInfo.Environment["SCRINIA_NUGET_OWNER"] = Scenario == "missing-owner" ? "" : "owner";
            process.StartInfo.Environment["SCRINIA_NUGET_OWNERSHIP_CONFIRMED"] = "scrinia:owner";
            process.StartInfo.Environment["SCRINIA_PUBLICATION_APPROVED"] = "true";
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) { process.Kill(true); throw new TimeoutException("Publication fixture exceeded deadline."); }
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        private static string ResolveOutput(string name, string folder)
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            return output.Parent is { Name: "Scrinia.Tests", Parent.Name: "bin" } project
                ? Path.Combine(project.Parent.FullName, name, output.Name, name + ".dll")
                : RepoPaths.Combine(folder, name, "bin", output.Parent!.Name, output.Name, name + ".dll");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
