using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Scrinia.TestProcess;

/// <summary>Isolated executable fake for publication service transport; never launches a service client.</summary>
internal static class PublicationTransport
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    internal static Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Owned absolute fixture required.");
        string root = args[0], executable = args[1], outputPath = args[2];
        string[] command = args[3..];
        string scenario = File.ReadAllText(Path.Combine(root, "scenario.txt"));
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GH_TOKEN")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
            throw new InvalidOperationException("Real credentials are forbidden in fake publication transport.");
        void Call(string name) => File.AppendAllText(Path.Combine(root, "calls.txt"), name + Environment.NewLine);
        void Print(JsonNode value) => Console.WriteLine(value.ToJsonString());
        string Option(string name) { int index = Array.IndexOf(command, name); return index >= 0 && index + 1 < command.Length ? command[index + 1] : throw new ArgumentException("Missing fake transport option " + name); }
        string releasePath = Path.Combine(root, "release.json");
        string assetsRoot = Path.Combine(root, "remote-assets");
        string remotePackage = Path.Combine(root, "remote.nupkg");
        Directory.CreateDirectory(assetsRoot);

        if (executable == "curl")
        {
            Call("read-nuget");
            if (scenario == "nuget-unknown") { Console.Error.Write("503"); return Task.FromResult(0); }
            if (command.Length != 6 || Option("--write-out") != "%{stderr}%{http_code}" || command[^1] != "https://api.nuget.org/v3-flatcontainer/scrinia/0.6.0/scrinia.0.6.0.nupkg") throw new ArgumentException("Unexpected NuGet read.");
            if (!File.Exists(remotePackage)) { Console.Error.Write("404"); return Task.FromResult(0); }
            string destination = Path.Combine(root, "response.nupkg");
            File.Copy(remotePackage, destination, true);
            if (scenario is "different-package" or "different-opc")
            {
                using var zip = ZipFile.Open(destination, ZipArchiveMode.Update);
                string name = scenario == "different-opc" ? "package/services/metadata/core-properties/fixed.psmdcp" : "tools/net10.0/any/scri.dll";
                zip.GetEntry(name)!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("different immutable content");
            }
            using (var response = File.OpenRead(destination)) response.CopyTo(Console.OpenStandardOutput());
            Console.Error.Write("200"); return Task.FromResult(0);
        }
        if (executable == "dotnet")
        {
            if (command.Length != 7 || command[0] != "nuget" || command[1] != "push" || Option("--source") != "https://api.nuget.org/v3/index.json" || Option("--api-key") != "fixture-secret") throw new ArgumentException("Unexpected NuGet write.");
            Call("push");
            if (File.Exists(remotePackage)) throw new InvalidOperationException("Duplicate push must be reconciled first.");
            File.Copy(command[2], remotePackage);
            return Task.FromResult(scenario == "push-unknown" ? 1 : 0);
        }
        if (executable != "gh") throw new ArgumentException("Unexpected fake executable.");
        if (command.Length >= 3 && command[0] == "release" && command[1] == "upload")
        {
            if (command.Length != 6 || command[2] != "v0.6.0" || command[4] != "--repo" || command[5] != "owner/scrinia") throw new ArgumentException("Unexpected asset upload.");
            Call("upload");
            File.Copy(command[3], Path.Combine(assetsRoot, Path.GetFileName(command[3])));
            return Task.FromResult(scenario == "upload-failure" ? 1 : 0);
        }
        if (command.Length < 4 || command[0] != "api" || command[1] != "--method") throw new ArgumentException("Explicit API method required.");
        string method = command[2], endpoint = command[3];
        const string prefix = "repos/owner/scrinia/";
        if (!endpoint.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Unexpected API repository.");
        endpoint = endpoint[prefix.Length..];
        if (method == "POST" && endpoint == "releases")
        {
            Call("create");
            if (File.Exists(releasePath)) throw new InvalidOperationException("Duplicate draft creation.");
            var release = JsonNode.Parse(File.ReadAllText(Option("--input")))!.AsObject();
            release["id"] = 7;
            File.WriteAllText(releasePath, release.ToJsonString()); Print(release); return Task.FromResult(scenario == "create-unknown" ? 1 : 0);
        }
        if (method == "PATCH" && endpoint == "releases/7")
        {
            Call("publish");
            if (JsonNode.Parse(File.ReadAllText(Option("--input")))!["draft"]!.GetValue<bool>()) throw new ArgumentException("Unexpected publication update.");
            if (scenario == "publish-failure") return Task.FromResult(1);
            var release = JsonNode.Parse(File.ReadAllText(releasePath))!;
            release["draft"] = false; File.WriteAllText(releasePath, release.ToJsonString()); Print(release); return Task.FromResult(scenario == "publish-unknown" ? 1 : 0);
        }
        if (method != "GET") throw new ArgumentException("Unexpected service mutation.");
        Call("read-" + endpoint);
        bool reusable = scenario.StartsWith("reusable", StringComparison.Ordinal);
        if (endpoint == "actions/runs/42")
        {
            var run = new JsonObject
            {
                ["id"] = 42, ["repository"] = new JsonObject { ["id"] = 1, ["full_name"] = scenario == "wrong-repository" ? "other/scrinia" : "owner/scrinia" },
                ["head_repository"] = new JsonObject { ["id"] = 1, ["full_name"] = "owner/scrinia" },
                ["head_sha"] = scenario == "wrong-sha" ? new string('f', 40) : Sha,
                ["status"] = "completed", ["conclusion"] = "success", ["run_attempt"] = 2, ["workflow_id"] = 9,
                ["event"] = scenario == "pull-request" ? "pull_request" : reusable ? "push" : "workflow_dispatch",
                ["referenced_workflows"] = new JsonArray(new JsonObject { ["path"] = "owner/scrinia/.github/workflows/release-validation.yml@refs/heads/main", ["sha"] = Sha, ["ref"] = "refs/heads/main" })
            };
            int runReads = File.ReadAllLines(Path.Combine(root, "calls.txt")).Count(line => line == "read-actions/runs/42");
            if (runReads > 1)
                switch (scenario)
                {
                    case "attempt-changed": run["run_attempt"] = 3; break;
                    case "status-changed": run["status"] = "in_progress"; break;
                    case "head-changed": run["head_sha"] = new string('f', 40); break;
                    case "repository-changed": run["repository"]!["full_name"] = "other/scrinia"; break;
                }
            if (scenario == "reusable-missing-reference") run["referenced_workflows"] = new JsonArray();
            Print(run); return Task.FromResult(0);
        }
        if (endpoint == "actions/workflows/9")
        { Print(new JsonObject { ["id"] = 9, ["path"] = scenario == "wrong-workflow" ? ".github/workflows/unrelated.yml" : reusable ? ".github/workflows/ci.yml" : ".github/workflows/release-validation.yml" }); return Task.FromResult(0); }
        if (endpoint.StartsWith("actions/runs/42/artifacts?", StringComparison.Ordinal))
        {
            var artifacts = new JsonArray();
            if (endpoint.EndsWith("page=1", StringComparison.Ordinal))
                for (int i = 0; i < 100; i++) artifacts.Add(new JsonObject { ["id"] = 1000 + i, ["name"] = "unrelated " + i });
            else if (scenario != "artifact-missing")
            {
                string archive = Path.Combine(root, "validation.zip");
                var artifact = new JsonObject
                {
                    ["id"] = 77, ["name"] = "validated-release-42-" + Sha, ["expired"] = scenario == "artifact-expired",
                    ["size_in_bytes"] = scenario == "artifact-oversized" ? 3L * 1024 * 1024 * 1024 : new FileInfo(archive).Length,
                    ["digest"] = scenario == "artifact-missing-digest" ? null : "sha256:" + (scenario == "artifact-digest-mismatch" ? new string('f', 64) : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive)))),
                    ["workflow_run"] = new JsonObject { ["id"] = 42, ["repository_id"] = 1, ["head_repository_id"] = 1, ["head_sha"] = scenario == "artifact-wrong-sha" ? new string('f', 40) : Sha }
                };
                artifacts.Add(artifact);
                if (scenario == "artifact-duplicate") artifacts.Add(artifact.DeepClone());
            }
            Print(new JsonObject { ["total_count"] = 101, ["artifacts"] = artifacts }); return Task.FromResult(0);
        }
        if (endpoint == "actions/artifacts/77/zip")
        { File.Copy(Path.Combine(root, "validation.zip"), outputPath, true); return Task.FromResult(0); }
        if (endpoint.StartsWith("actions/runs/42/attempts/2/jobs?", StringComparison.Ordinal))
        {
            var jobs = new JsonArray();
            if (endpoint.EndsWith("page=1", StringComparison.Ordinal))
                for (int i = 0; i < 100; i++) jobs.Add(Job("unrelated " + i, "success"));
            else
            {
                string[] names = ["build-check", "native (win-x64)", "native (linux-x64)", "native (osx-arm64)", "aggregate"];
                foreach (string name in names)
                {
                    if (scenario == "missing-eligibility" && name == "aggregate") continue;
                    string conclusion = name.Contains("linux-x64", StringComparison.Ordinal) ? scenario switch { "failed-native" => "failure", "skipped-native" => "skipped", _ => "success" } : "success";
                    var job = Job((reusable ? "release-validation / " : "") + name, conclusion);
                    if (scenario == "returned-job-attempt-mismatch") job["run_attempt"] = 1;
                    jobs.Add(job);
                }
                if (scenario == "duplicate-job") jobs.Add(Job("aggregate", "success"));
            }
            Print(new JsonObject { ["total_count"] = 105, ["jobs"] = jobs }); return Task.FromResult(0);
        }
        if (endpoint == "git/ref/tags/v0.6.0")
        { Print(new JsonObject { ["ref"] = "refs/tags/v0.6.0", ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = scenario == "wrong-tag" ? new string('f', 40) : Sha } }); return Task.FromResult(0); }
        if (endpoint.StartsWith("releases?", StringComparison.Ordinal))
        {
            var releases = new JsonArray();
            if (File.Exists(releasePath))
            {
                var release = JsonNode.Parse(File.ReadAllText(releasePath))!;
                if (scenario == "different-metadata") release["body"] = "other validated candidate";
                releases.Add(release);
            }
            Print(releases); return Task.FromResult(0);
        }
        string[] files = Directory.GetFiles(assetsRoot).Order(StringComparer.Ordinal).ToArray();
        if (endpoint.StartsWith("releases/7/assets?", StringComparison.Ordinal))
        {
            var assets = new JsonArray();
            for (int i = 0; i < files.Length; i++) assets.Add(new JsonObject { ["id"] = i + 100, ["name"] = Path.GetFileName(files[i]), ["size"] = new FileInfo(files[i]).Length, ["state"] = "uploaded" });
            Print(assets); return Task.FromResult(0);
        }
        if (endpoint.StartsWith("releases/assets/", StringComparison.Ordinal))
        {
            int id = int.Parse(endpoint["releases/assets/".Length..], System.Globalization.CultureInfo.InvariantCulture);
            File.Copy(files[id - 100], outputPath, true);
            if (scenario == "different-asset") File.AppendAllText(outputPath, "changed");
            return Task.FromResult(0);
        }
        throw new ArgumentException("Unexpected read-only API endpoint " + endpoint);
    }

    private static JsonObject Job(string name, string conclusion) => new()
    { ["id"] = 123, ["name"] = name, ["status"] = "completed", ["conclusion"] = conclusion, ["head_sha"] = Sha, ["run_id"] = 42 };
}
