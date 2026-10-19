using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Scrinia.Commands;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// The publish scripts had two hand-maintained project lists and they had diverged: publish.ps1
/// published three projects, publish.sh two. The missing one was Scrinia.Merge — the .nmp2 merge
/// driver — so a Linux or macOS build produced a tree whose configured merge driver pointed at a
/// binary that did not exist, and .nmp2 merges fell back to git's line-based text merge over
/// Base64 blobs.
///
/// <para>These tests hold the fix's shape rather than its instance: the project list lives in
/// publish.projects.tsv, neither script may name a project itself, and a NEW executable project
/// fails <see cref="EveryShippableExecutable_IsInTheManifestOrNamedAsExcluded"/> until someone
/// decides which it is.</para>
/// </summary>
public sealed class PublishManifestTests
{
    private static readonly string ManifestPath = RepoPaths.Combine("publish.projects.tsv");

    [SkippableTheory]
    [InlineData("publish.sh", "linux-x64", "valid")]
    [InlineData("publish.sh", "osx-arm64", "valid")]
    [InlineData("publish.sh", "linux-x64", "missing")]
    [InlineData("publish.sh", "osx-arm64", "missing")]
    [InlineData("publish.sh", "linux-x64", "directory")]
    [InlineData("publish.sh", "osx-arm64", "directory")]
    [InlineData("publish.sh", "linux-x64", "link")]
    [InlineData("publish.sh", "osx-arm64", "link")]
    [InlineData("publish.sh", "win-x64", "valid")]
    [InlineData("publish.ps1", "linux-x64", "valid")]
    [InlineData("publish.ps1", "osx-arm64", "valid")]
    [InlineData("publish.ps1", "linux-x64", "missing")]
    [InlineData("publish.ps1", "osx-arm64", "missing")]
    [InlineData("publish.ps1", "linux-x64", "directory")]
    [InlineData("publish.ps1", "osx-arm64", "directory")]
    [InlineData("publish.ps1", "linux-x64", "link")]
    [InlineData("publish.ps1", "osx-arm64", "link")]
    [InlineData("publish.ps1", "win-x64", "valid")]
    public void Publishers_CarryOnlyRegularResolvedUnixShim(string publisher, string rid, string fixture)
    {
        Skip.If(OperatingSystem.IsWindows() && (publisher.EndsWith(".sh", StringComparison.Ordinal) || fixture == "link"),
            "Native Bash paths and unprivileged symbolic links are exercised on Unix.");
        string root = Path.Combine(Path.GetTempPath(), "scri-publisher-shim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string output = Path.Combine(root, "output"), sourceRecord = Path.Combine(root, "raw-output.txt");
            File.Copy(RepoPaths.Combine(publisher), Path.Combine(root, publisher));
            File.WriteAllText(Path.Combine(root, "publish.projects.tsv"), ExpectedHeader +
                "\nscri\tsrc/Scrinia/Scrinia.csproj\talways\ttrimmed\trid\tall\n");
            // Only dotnet's raw producer is replaced. The entire production manifest reader,
            // RID selector, refusal checks and copy branches execute unchanged.
            string producer = Path.Combine(root, "produce.ps1");
            File.WriteAllText(producer, """
                param([string]$OutputPath,[string]$Rid,[string]$Fixture)
                $ErrorActionPreference = 'Stop'
                [IO.Directory]::CreateDirectory($OutputPath) | Out-Null
                [IO.File]::WriteAllText($env:SHIM_SOURCE_RECORD,$OutputPath)
                $hostName = if ($Rid -eq 'win-x64') { 'scri.exe' } else { 'scri' }
                [IO.File]::WriteAllBytes((Join-Path $OutputPath $hostName),[byte[]](1,2,3,255))
                $expected = if ($Rid -eq 'osx-arm64') { 'libSystem.Native.dylib' } else { 'libSystem.Native.so' }
                $wrong = if ($expected -eq 'libSystem.Native.so') { 'libSystem.Native.dylib' } else { 'libSystem.Native.so' }
                [IO.File]::WriteAllText((Join-Path $OutputPath $wrong),'wrong carrier')
                $path = Join-Path $OutputPath $expected
                switch ($Fixture) {
                    'valid' { [IO.File]::WriteAllBytes($path,[byte[]](0,9,128,254,255)) }
                    'directory' { [IO.Directory]::CreateDirectory($path) | Out-Null }
                    'link' { [IO.File]::CreateSymbolicLink($path,(Join-Path $OutputPath $wrong)) | Out-Null }
                }
                if (-not $IsWindows -and $Fixture -eq 'valid') {
                    [IO.File]::SetUnixFileMode($path,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::OtherExecute)
                }
                """);
            bool bash = publisher.EndsWith(".sh", StringComparison.Ordinal);
            string wrapper = Path.Combine(root, "run.ps1");
            File.WriteAllText(wrapper, """
                $ErrorActionPreference = 'Stop'
                function global:dotnet {
                    if ($args[0] -eq 'msbuild') { $global:LASTEXITCODE = 0; return '0.6.0' }
                    $values = @($args)
                    $outIndex = [Array]::IndexOf($values,'--output')
                    $ridIndex = [Array]::IndexOf($values,'--runtime')
                    & (Join-Path $PSScriptRoot 'produce.ps1') -OutputPath $values[$outIndex+1] -Rid $values[$ridIndex+1] -Fixture $env:SHIM_FIXTURE
                    $global:LASTEXITCODE = 0
                }
                & (Join-Path $PSScriptRoot 'publish.ps1') -OutputDir (Join-Path $PSScriptRoot 'output') -Platform $env:SHIM_RID
                """);
            if (bash)
            {
                string stub = Path.Combine(root, "dotnet");
                File.WriteAllText(stub, """
                    #!/usr/bin/env bash
                    set -euo pipefail
                    if [ "$1" = msbuild ]; then printf '0.6.0\n'; exit 0; fi
                    out=''; rid=''
                    while [ "$#" -gt 0 ]; do
                        case "$1" in --output) shift; out="$1" ;; --runtime) shift; rid="$1" ;; esac
                        shift
                    done
                    pwsh -NoProfile -File "$SHIM_PRODUCER" -OutputPath "$out" -Rid "$rid" -Fixture "$SHIM_FIXTURE"
                    """);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stub,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var info = new ProcessStartInfo(bash ? "bash" : "pwsh")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            info.Environment["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            info.Environment["SHIM_PRODUCER"] = producer; info.Environment["SHIM_FIXTURE"] = fixture;
            info.Environment["SHIM_RID"] = rid; info.Environment["SHIM_SOURCE_RECORD"] = sourceRecord;
            if (bash) { info.ArgumentList.Add(Path.Combine(root, publisher)); info.ArgumentList.Add(output); info.ArgumentList.Add("--platform=" + rid); }
            else { info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-File"); info.ArgumentList.Add(wrapper); }
            using var process = System.Diagnostics.Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Publisher fixture did not finish."); }
            Task.WaitAll(new Task[] { stdout, stderr }, TimeSpan.FromSeconds(5)).Should().BeTrue("publisher output must finish draining");
            string diagnostic = stdout.Result + stderr.Result;
            bool success = fixture == "valid";
            (process.ExitCode == 0).Should().Be(success, diagnostic);
            string name = rid == "osx-arm64" ? "libSystem.Native.dylib" : "libSystem.Native.so";
            string shim = Path.Combine(output, name);
            if (success)
            {
                string host = rid == "win-x64" ? "scri.exe" : "scri";
                Directory.GetFiles(output).Select(Path.GetFileName).Should().BeEquivalentTo(
                    rid == "win-x64" ? new[] { host } : new[] { host, name });
                File.ReadAllBytes(Path.Combine(output, host)).Should().Equal(1, 2, 3, 255);
                if (rid != "win-x64")
                {
                    File.ReadAllBytes(shim).Should().Equal(0, 9, 128, 254, 255);
                    if (!OperatingSystem.IsWindows()) File.GetUnixFileMode(shim).Should().Be(
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherExecute);
                }
            }
            else
            {
                File.Exists(shim).Should().BeFalse();
                diagnostic.Should().Contain(Path.Combine(File.ReadAllText(sourceRecord), name));
                diagnostic.Should().Contain("RID: " + rid);
                diagnostic.Should().Contain("is not a file");
            }
            Console.WriteLine($"{publisher}/{rid}/{fixture}: exact inventory, bytes, Unix mode or refusal assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("linux-x64", "libSystem.Native.so", "valid", true)]
    [InlineData("osx-arm64", "libSystem.Native.dylib", "valid", true)]
    [InlineData("linux-x64", "libSystem.Native.so", "missing", false)]
    [InlineData("linux-x64", "libSystem.Native.so", "wrong-rid", false)]
    [InlineData("linux-x64", "libSystem.Native.so", "ambiguous", false)]
    [InlineData("linux-x64", "libSystem.Native.so", "directory", false)]
    [InlineData("win-x64", "libSystem.Native.so", "missing", true)]
    public void UnixRuntimeShim_PreservesOnlyTheBuildResolvedCarrier(string rid, string name, string fixture, bool success)
    {
        XElement? target = XDocument.Load(RepoPaths.Combine("src", "Scrinia", "Scrinia.csproj"))
            .Root!.Elements("Target").SingleOrDefault(element => (string?)element.Attribute("Name") == "PreserveUnixRuntimeShim");
        target.Should().NotBeNull("published Unix hosts must retain their exact resolved regular-file classifier shim");
        string root = Path.Combine(Path.GetTempPath(), "scri-shim-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, name), publish = Path.Combine(root, "publish") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(publish);
        if (fixture == "directory") Directory.CreateDirectory(source);
        else if (fixture != "missing") File.WriteAllText(source, "exact runtime-pack bytes");
        XElement Asset(string path) => new("RuntimePackAsset", new XAttribute("Include", path),
            new XElement("DestinationSubPath", name), new XElement("AssetType", "native"),
            new XElement("RuntimeIdentifier", fixture == "wrong-rid" ? "osx-arm64" : rid),
            new XElement("NuGetPackageId", "Microsoft.NETCore.App.Runtime." + rid), new XElement("NuGetPackageVersion", "10.0.7"));
        var items = new XElement("ItemGroup", Asset(source));
        if (fixture == "ambiguous") items.Add(Asset(Path.Combine(root, "duplicate", name)));
        string project = Path.Combine(root, "shim.proj");
        new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XElement("RuntimeIdentifier", rid), new XElement("SelfContained", "true"),
            new XElement("PublishSingleFile", "true"), new XElement("PublishDir", publish)), items, new XElement(target!))).Save(project);
        using var process = new System.Diagnostics.Process { StartInfo = new("dotnet")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in new[] { "msbuild", project, "-target:PreserveUnixRuntimeShim", "-nologo" }) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Runtime shim target did not finish."); }
        Task.WaitAll(new Task[] { stdout, stderr }, TimeSpan.FromSeconds(5)).Should().BeTrue("target output must finish draining");
        (process.ExitCode == 0).Should().Be(success, stdout.Result + stderr.Result);
        string destination = Path.Combine(publish, name);
        if (success && rid != "win-x64") File.ReadAllText(destination).Should().Be("exact runtime-pack bytes");
        else File.Exists(destination).Should().BeFalse();
    }

    /// <summary>
    /// Executable-producing projects anywhere in the repo that deliberately do NOT ship through
    /// the developer publish SCRIPTS. Each entry states why; an unexplained absence is what this
    /// test exists to prevent.
    ///
    /// <para>This answers "does publish.sh/publish.ps1 build this?", not "is this allowed to
    /// appear in a release?" — two questions that had different answers while a server existed,
    /// which is why the two sets below are still separate even though nothing currently sits in
    /// one and not the other. Keying the release-workflow check off this set once let a release
    /// step publishing AppHost pass silently.</para>
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedFromPublish =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["Scrinia.Retrieval.Benchmarks"] =
            "Benchmark harness under benchmarks/ — run from source via `dotnet run`, never " +
            "distributed to users.",
        ["Scrinia.Agent.Benchmarks"] =
            "Developer LongMemEval agent harness under benchmarks/; run from source, not a shipped release unit.",
        ["Scrinia.Agent.Benchmarks.Tests"] =
            "Test assembly with a readable child-process fixture entry point; never distributed to users.",
        ["Scrinia.Benchmarks"] =
            "Benchmark harness under tests/ — run from source via `dotnet run`, never distributed " +
            "to users.",
        ["Scrinia.TestProcess"] =
            "Child-process harness for admission and vector integration tests; built by the " +
            "solution for tests and never distributed to users.",
        ["Scrinia.ReleaseTools"] =
            "Build-only payload stager; not a shipped release unit or NuGet package.",
    };

    /// <summary>
    /// Projects release.yml is allowed to publish that are deliberately NOT in
    /// publish.projects.tsv. Strictly narrower than <see cref="ExcludedFromPublish"/>: every name
    /// here is also excluded from the developer scripts, but not every script exclusion belongs
    /// here — a benchmark harness is excluded from the scripts AND must never appear in a release,
    /// so neither is in this set.
    /// <para>EMPTY, and deliberately kept rather than deleted. Its sole entry was
    /// <c>Scrinia.Server</c>, released by a job that no longer exists. The set is the thing that
    /// makes "the release publishes something the developer scripts do not" a decision someone has
    /// to write down, and that check is worth keeping armed at zero.</para>
    /// </summary>
    private static readonly Dictionary<string, string> ReleasedOutsideTheManifest =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every project path declared by a `Project(...)` line in Scrinia.sln — the authoritative
    /// build list, not a filesystem walk over whatever happens to be on disk. A project not in
    /// the solution is not built by CI and cannot ship; a nested worktree or other stray copy
    /// living somewhere under the repo root cannot forge its way into this list the way it could
    /// into a directory enumeration, because nothing adds it to Scrinia.sln. Solution folders
    /// ("{2150E333-...}" entries with no project file, e.g. "src", "Tests") are filtered out by
    /// requiring the second quoted value to end in .csproj.
    /// </summary>
    private static List<string> SolutionProjectPaths()
    {
        string sln = File.ReadAllText(RepoPaths.Combine("Scrinia.sln"));
        return Regex.Matches(sln,
                @"^Project\(""\{[0-9A-Fa-f-]+\}""\)\s*=\s*""[^""]+"",\s*""([^""]+\.csproj)""",
                RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    private sealed record ManifestRow(
        string Id, string Project, string Condition, string Profile, string Output, string Rids);

    /// <summary>
    /// Deliberately called outside any <see cref="AssertionScope"/> everywhere it's used: a
    /// malformed row must throw immediately from <c>HaveCount(6)</c> rather than be collected,
    /// because a short row would otherwise reach <c>parts[5]</c> and throw
    /// <see cref="IndexOutOfRangeException"/> instead of a message naming the offending line.
    /// </summary>
    private static List<ManifestRow> ReadManifest()
    {
        // Skip a leading run of comment lines, then exactly the header row that follows them —
        // so a banner comment ever added above the header doesn't get treated as it and let the
        // real header row be parsed as data.
        return ParseManifest(File.ReadAllLines(ManifestPath), ManifestPath);
    }

    private static List<ManifestRow> ParseManifest(IEnumerable<string> sourceLines, string sourceName)
    {
        string[] lines = sourceLines.ToArray();
        int headerIndex = Array.FindIndex(lines, line => !line.StartsWith('#'));
        if (headerIndex < 0 || lines[headerIndex] != ExpectedHeader)
            throw new FormatException($"Manifest header in {sourceName} must be '{ExpectedHeader}'.");

        var rows = new List<ManifestRow>();
        foreach (string line in lines.Skip(headerIndex + 1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] parts = line.Split('\t');
            if (parts.Length != 6)
                throw new FormatException(
                    $"Manifest row in {sourceName} has {parts.Length} columns, expected 6: '{line}'");
            rows.Add(new ManifestRow(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]));
        }
        return rows;
    }

    private static string WriteManifestFixture(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"scrinia-publish-{Guid.NewGuid():N}.tsv");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static (bool Available, int ExitCode, string Output) RunPublishReader(
        string scriptName, bool usePublisherStub, params string[] manifestLines)
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia-publish-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(RepoPaths.Combine(scriptName), Path.Combine(root, scriptName));
            File.WriteAllLines(Path.Combine(root, "publish.projects.tsv"), manifestLines);
            if (usePublisherStub)
            {
                string stubName = OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet";
                string stubPath = Path.Combine(root, stubName);
                File.WriteAllText(stubPath, OperatingSystem.IsWindows()
                    ? "@echo off\r\nif \"%1\"==\"msbuild\" (echo 9.8.7& exit /b 0)\r\n:next\r\nif \"%~1\"==\"--output\" goto output\r\nif \"%~1\"==\"\" exit /b 1\r\nshift\r\ngoto next\r\n:output\r\nshift\r\nmkdir \"%~1\"\r\necho executable>\"%~1\\scri.exe\"\r\nexit /b 0\r\n"
                    : "#!/usr/bin/env bash\nif [ \"$1\" = msbuild ]; then echo 9.8.7; exit 0; fi\nwhile [ \"$1\" != --output ]; do shift; done\nshift\nmkdir -p \"$1\"\nprintf executable > \"$1/scri.exe\"\n");
                if (!OperatingSystem.IsWindows())
                    System.Diagnostics.Process.Start(new ProcessStartInfo("chmod", $"+x \"{stubPath}\""))!.WaitForExit();
            }

            bool isPowerShell = scriptName.EndsWith(".ps1", StringComparison.Ordinal);
            var startInfo = new ProcessStartInfo
            {
                FileName = isPowerShell ? "pwsh" : "bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (usePublisherStub)
                startInfo.Environment["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            if (isPowerShell)
            {
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-File");
                startInfo.ArgumentList.Add(Path.Combine(root, scriptName));
                startInfo.ArgumentList.Add("-OutputDir");
                startInfo.ArgumentList.Add(Path.Combine(root, "output"));
                startInfo.ArgumentList.Add("-Platform");
                startInfo.ArgumentList.Add("win-x64");
            }
            else
            {
                startInfo.ArgumentList.Add(Path.Combine(root, scriptName));
                startInfo.ArgumentList.Add(Path.Combine(root, "output"));
                startInfo.ArgumentList.Add("--platform=win-x64");
            }

            try
            {
                using var process = System.Diagnostics.Process.Start(startInfo);
                process.Should().NotBeNull();
                string output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (true, process.ExitCode, output);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (false, -1, string.Empty);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public void PublishSh_RejectsMalformedFixtureThroughTheProductionReader()
    {
        Skip.If(OperatingSystem.IsWindows(), "bash cannot consume Windows paths through the host WSL shim");
        var result = RunPublishReader("publish.sh", false, ExpectedHeader,
            "scri\tsrc/Scrinia/Scrinia.csproj\talways\ttrimmed\trid");
        Skip.If(!result.Available, "bash is unavailable on this host");
        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("5 tab-separated columns");
    }

    [SkippableFact]
    public void PublishPs1_RejectsMalformedFixtureThroughTheProductionReader()
    {
        var result = RunPublishReader("publish.ps1", false, ExpectedHeader,
            "scri\tsrc/Scrinia/Scrinia.csproj\talways\ttrimmed\trid");
        Skip.If(!result.Available, "pwsh is unavailable on this host");
        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("expected 6");
    }

    [SkippableFact]
    public void PublishSh_RejectsReorderedHeaderThroughTheProductionReader()
    {
        Skip.If(OperatingSystem.IsWindows(), "bash cannot consume Windows paths through the host WSL shim");
        var result = RunPublishReader("publish.sh", false,
            "id\tproject\tprofile\tcondition\toutput\trids",
            "scri\tsrc/Scrinia/Scrinia.csproj\ttrimmed\talways\trid\tall");
        Skip.If(!result.Available, "bash is unavailable on this host");
        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("header");
    }

    [SkippableFact]
    public void PublishPs1_RejectsReorderedHeaderThroughTheProductionReader()
    {
        var result = RunPublishReader("publish.ps1", false,
            "id\tproject\tprofile\tcondition\toutput\trids",
            "scri\tsrc/Scrinia/Scrinia.csproj\ttrimmed\talways\trid\tall");
        Skip.If(!result.Available, "pwsh is unavailable on this host");
        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("header");
    }

    [SkippableFact]
    public void PublishSh_AcceptsAValidManifestThroughTheProductionReader()
    {
        Skip.If(OperatingSystem.IsWindows(), "bash cannot consume Windows paths through the host WSL shim");
        var result = RunPublishReader("publish.sh", true, ExpectedHeader,
            "scri	missing.csproj	always	plain	rid	all");
        Skip.If(!result.Available, "bash is unavailable on this host");
        result.ExitCode.Should().Be(0);
    }

    [SkippableFact]
    public void PublishPs1_AcceptsAValidManifestThroughTheProductionReader()
    {
        var result = RunPublishReader("publish.ps1", true, ExpectedHeader,
            "scri	missing.csproj	always	plain	rid	host");
        Skip.If(!result.Available, "pwsh is unavailable on this host");
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public void EveryManifestProject_ExistsOnDisk()
    {
        var rows = ReadManifest();
        rows.Should().NotBeEmpty();

        using var scope = new AssertionScope();
        foreach (var row in rows)
        {
            File.Exists(RepoPaths.Combine(row.Project.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue(because: $"the manifest names {row.Project} for '{row.Id}'");
        }
    }

    /// <summary>Token that must appear on every real `dotnet publish` invocation line in each
    /// script — proof the path it publishes came from the manifest-derived array/object rather
    /// than a literal. Matched per-script because the two scripts hold that value differently
    /// (a bash array element vs. a PowerShell object property).</summary>
    private static readonly Dictionary<string, string> ManifestDerivedToken = new()
    {
        ["publish.sh"] = "PROJECTS[",
        ["publish.ps1"] = "$p.project",
    };

    [Fact]
    public void NeitherScript_NamesAProjectItself()
    {
        using var scope = new AssertionScope();
        foreach (string script in new[] { "publish.sh", "publish.ps1" })
        {
            string text = File.ReadAllText(RepoPaths.Combine(script));
            text.Should().NotContain(".csproj",
                because: $"{script} must take its project list from publish.projects.tsv — a literal " +
                         "project path here is how the two lists diverged in the first place");

            // `dotnet publish` also accepts a bare project DIRECTORY (it resolves the single
            // project inside), so a hardcoded `dotnet publish src/Scrinia.Merge` would reintroduce
            // the same defect while never containing the substring ".csproj" above. Asserting the
            // positive instead closes that gap: every actual invocation line (matched at line
            // start so an unrelated string like the ps1 error message below is not swept in) must
            // reference the manifest-derived variable, not merely avoid one literal spelling.
            string token = ManifestDerivedToken[script];
            var invocationLines = text
                .Split('\n')
                .Where(line => Regex.IsMatch(line, @"^\s*dotnet publish\b"))
                .ToList();
            invocationLines.Should().NotBeEmpty(because: $"{script} must actually invoke dotnet publish somewhere");
            foreach (string line in invocationLines)
            {
                line.Should().Contain(token,
                    because: $"{script} must publish only a path it read from the manifest — this line " +
                             $"should reference {token}, not a literal project or directory argument");
            }
        }
    }

    [Fact]
    public void EveryShippableExecutable_IsInTheManifestOrNamedAsExcluded()
    {
        ExcludedFromPublish.Values.Should().AllSatisfy(r => r.Should().NotBeNullOrWhiteSpace(),
            because: "an exclusion with no reason is exactly the unexplained absence this test exists to prevent");

        var manifestRows = ReadManifest();
        var manifestProjects = manifestRows
            .Select(r => Path.GetFileNameWithoutExtension(r.Project))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Sourced from Scrinia.sln, not a filesystem walk: a directory enumeration finds whatever
        // happens to be on disk, which once included a nested git worktree under .claude/
        // containing a full second copy of the repo (gitignored — see .gitignore:2) and required
        // an explicit path exclusion to work around. That exclusion stops working the moment a
        // stray copy lands anywhere else, and it is the same class of hazard that already broke
        // BenchmarkDotNet's toolchain once (it searches the whole tree for a matching project
        // file and throws when it finds two). The solution is the authoritative list instead: a
        // project not in it is not built by CI and cannot ship, and no stray copy can add itself
        // to Scrinia.sln.
        var executables = SolutionProjectPaths()
            .Select(relPath => RepoPaths.Combine(relPath.Replace('\\', Path.DirectorySeparatorChar)))
            .Where(p =>
            {
                string text = File.ReadAllText(p);
                return text.Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal)
                    || text.Contains("Sdk=\"Microsoft.NET.Sdk.Web\"", StringComparison.Ordinal);
            })
            .Select(Path.GetFileNameWithoutExtension)
            .Select(n => n!)
            .ToList();

        executables.Should().NotBeEmpty(because: "the enumeration itself must not silently match nothing");

        using var scope = new AssertionScope();
        foreach (string name in executables)
        {
            (manifestProjects.Contains(name) || ExcludedFromPublish.ContainsKey(name))
                .Should().BeTrue(
                    because: $"{name} produces an executable but is neither in publish.projects.tsv nor " +
                             "named in ExcludedFromPublish — decide which it is and record the reason");
        }

        foreach (string excluded in ExcludedFromPublish.Keys)
        {
            executables.Should().Contain(excluded,
                because: $"{excluded} is named as excluded but no longer produces an executable — " +
                         "a stale exclusion hides the next real one");
        }
    }

    /// <summary>
    /// The six manifest columns, in the order both scripts must agree on. publish.sh reads them
    /// POSITIONALLY (<c>IFS=$'\t' read -r m_id m_project ...</c>); publish.ps1 reads them by
    /// column NAME (<c>Import-Csv</c>). A reordered header keeps publish.ps1 correct while
    /// silently misassigning every field for publish.sh — neither script errored on this before
    /// both scripts started pinning the header line themselves.
    /// </summary>
    private static readonly string[] ExpectedHeaderColumns =
        ["id", "project", "condition", "profile", "output", "rids"];

    private static readonly string ExpectedHeader = string.Join('\t', ExpectedHeaderColumns);

    [Fact]
    public void ManifestHeader_IsExactlyTheExpectedColumnsInOrder()
    {
        string headerLine = File.ReadAllLines(ManifestPath).SkipWhile(l => l.StartsWith('#')).First();
        headerLine.Split('\t').Should().Equal(ExpectedHeaderColumns,
            because: "publish.sh reads manifest columns POSITIONALLY — a reordered or renamed " +
                     "header would silently misassign every field to the wrong variable there " +
                     "while publish.ps1 (which reads by column name) kept working, and neither " +
                     "script would raise an error");
    }

    [Fact]
    public void EveryManifestRow_HasExactlySixTabSeparatedFields()
    {
        using var scope = new AssertionScope();
        foreach (string line in File.ReadAllLines(ManifestPath).SkipWhile(l => l.StartsWith('#')).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            line.Split('\t').Should().HaveCount(6,
                because: $"Import-Csv cannot see a field beyond the header's column count — it " +
                         $"silently drops an extra one rather than erroring the way publish.sh's " +
                         $"positional `read` does: '{line}'");
        }
    }

    [Fact]
    public void ManifestParser_RejectsAReorderedHeaderBeforeBindingColumns()
    {
        string path = WriteManifestFixture(
            "id\tproject\tprofile\tcondition\toutput\trids",
            "scri\tsrc/Scrinia/Scrinia.csproj\ttrimmed\talways\trid\tall");

        try
        {
            Action parse = () => ParseManifest(File.ReadAllLines(path), path);

            parse.Should().Throw<FormatException>().WithMessage($"*{path}*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ManifestParser_RejectsMalformedRowsWithTheirFieldCount()
    {
        string path = WriteManifestFixture(
            ExpectedHeader,
            "scri\tsrc/Scrinia/Scrinia.csproj\talways\ttrimmed\trid");

        try
        {
            Action parse = () => ParseManifest(File.ReadAllLines(path), path);

            parse.Should().Throw<FormatException>().WithMessage($"*{path}*5 columns*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The documented vocabulary for each gating column, matched case-sensitively:
    /// PowerShell's plain <c>-eq</c>/<c>-in</c>/<c>switch</c> are all case-INSENSITIVE, so
    /// <c>output=Rid</c> or <c>rids=Host</c> would silently pass validation there while bash's
    /// <c>case</c> statement (case-sensitive by default) rejects them — the exact divergence that
    /// let a manifest typo ship the embeddings plugin in every build.</summary>
    private static readonly Dictionary<string, string[]> ManifestVocabulary = new(StringComparer.Ordinal)
    {
        ["condition"] = ["always", "with-embeddings"],
        ["profile"] = ["trimmed", "plain"],
        ["output"] = ["rid", "plugin"],
        ["rids"] = ["all", "host"],
    };

    [Fact]
    public void EveryManifestValue_IsWithinTheDocumentedVocabulary_CaseSensitive()
    {
        var rows = ReadManifest();
        rows.Should().NotBeEmpty();

        using var scope = new AssertionScope();
        foreach (var row in rows)
        {
            ManifestVocabulary["condition"].Contains(row.Condition, StringComparer.Ordinal).Should().BeTrue(
                because: $"'{row.Condition}' for '{row.Id}' is not a recognised condition — an " +
                         "unrecognised value here (e.g. a 'with_embeddings' typo) used to make both " +
                         "scripts silently ship the embeddings plugin in EVERY build, condition or not");
            ManifestVocabulary["profile"].Contains(row.Profile, StringComparer.Ordinal).Should().BeTrue(
                because: $"'{row.Profile}' for '{row.Id}' is not a recognised profile — an unrecognised " +
                         "value here must be rejected at read time, not fall through to an undefined " +
                         "publish shape");
            ManifestVocabulary["output"].Contains(row.Output, StringComparer.Ordinal).Should().BeTrue(
                because: $"'{row.Output}' for '{row.Id}' is not a recognised output kind — " +
                         "PowerShell's plain -notin would silently ACCEPT a wrong-case value like " +
                         "'Rid' here where bash's case statement rejects it");
            ManifestVocabulary["rids"].Contains(row.Rids, StringComparer.Ordinal).Should().BeTrue(
                because: $"'{row.Rids}' for '{row.Id}' is not a recognised rids scope — an " +
                         "unrecognised value here (e.g. 'rids=hostt') used to silently degrade to " +
                         "'all' and attempt a cross-OS NativeAOT publish, aborting the whole run");
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> appears as one alternative of a bash `case` pattern arm
    /// — e.g. the "rid" in "    rid|plugin) ;;" — rather than anywhere at all in the file. A bare
    /// substring search (the shape this replaced) still passes if the value survives only in a
    /// comment; this is anchored to the syntax that actually dispatches on the value.
    /// </summary>
    private static bool BashCaseArmContains(string scriptText, string value)
    {
        string escaped = Regex.Escape(value);
        string pattern = $@"^[ \t]*(?:[\w-]+\|)*{escaped}(?:\|[\w-]+)*\)";
        return Regex.IsMatch(scriptText, pattern, RegexOptions.Multiline);
    }

    /// <summary>
    /// True when <paramref name="value"/> appears in one of publish.ps1's three real dispatch
    /// shapes for a manifest value: a `-cnotin @(...)` vocabulary list, a `-ceq '...'`
    /// comparison, or a quoted `switch -CaseSensitive` case label. Anchored the same way as
    /// <see cref="BashCaseArmContains"/> and for the same reason — a bare substring search
    /// passes on a comment.
    /// </summary>
    private static bool PowerShellDispatchContains(string scriptText, string value)
    {
        string escaped = Regex.Escape(value);

        // `^[^#\r\n]*` with Multiline is what makes this an anchor rather than a substring search,
        // and it is not decoration. Without it these three patterns matched ANYWHERE in the file,
        // so commenting out the real check while leaving its text behind —
        //     # if ($p.output -cnotin @('bundle')) { throw ... }
        // — kept this green with nothing dispatching on the value. That is precisely the
        // vacuous-pass shape this helper exists to close, and it survived on the PowerShell side
        // for one commit while the doc comment already claimed otherwise.
        //
        // Requiring no '#' between line start and the construct rejects both a fully commented-out
        // line and a trailing comment that happens to quote real dispatch syntax.
        bool Dispatches(string body) =>
            Regex.IsMatch(scriptText, $@"^[^#\r\n]*{body}", RegexOptions.Multiline);

        bool inNotInList = Dispatches($@"-cnotin\s+@\([^)]*'{escaped}'[^)]*\)");
        bool inCeq = Dispatches($@"-ceq\s+'{escaped}'");
        bool inSwitchLabel = Dispatches($@"'{escaped}'\s*\{{");
        return inNotInList || inCeq || inSwitchLabel;
    }

    [Fact]
    public void BothScripts_ContainEveryVocabularyLiteralTheManifestActuallyUses()
    {
        var rows = ReadManifest();
        var usedValues = rows
            .SelectMany(r => new[] { r.Condition, r.Profile, r.Output, r.Rids })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        usedValues.Should().NotBeEmpty();

        string shText = File.ReadAllText(RepoPaths.Combine("publish.sh"));
        string ps1Text = File.ReadAllText(RepoPaths.Combine("publish.ps1"));

        using var scope = new AssertionScope();
        foreach (string value in usedValues)
        {
            BashCaseArmContains(shText, value).Should().BeTrue(
                because: $"publish.sh must recognise manifest value '{value}' in a real `case` pattern " +
                         "arm, not merely mention it somewhere in the file (e.g. in a comment) — " +
                         "teaching only one script a new vocabulary literal (e.g. adding output=bundle " +
                         "to the manifest and updating only one script's vocabulary list) would let " +
                         "that script silently mis-handle every row using it");
            PowerShellDispatchContains(ps1Text, value).Should().BeTrue(
                because: $"publish.ps1 must recognise manifest value '{value}' in a real -cnotin list, " +
                         "-ceq comparison, or switch case label, not merely mention it somewhere in the " +
                         "file (e.g. in a comment) — teaching only one script a new vocabulary literal " +
                         "would let that script silently mis-handle every row using it");
        }
    }

    [Fact]
    public void PayloadPolicy_PackageVersionsMatchTheBackendReference()
    {
        string csproj = File.ReadAllText(RepoPaths.Combine(
            "src", "Scrinia.Plugin.Embeddings", "Scrinia.Plugin.Embeddings.csproj"));
        var declared = Regex.Match(csproj,
            """<PackageReference\s+Include="LLamaSharp\.Backend\.Vulkan"\s+Version="([^"]+)"\s*/?>""");
        declared.Success.Should().BeTrue(because: "the plugin must still reference the Vulkan backend");
        string version = declared.Groups[1].Value;

        using var policy = JsonDocument.Parse(File.ReadAllBytes(RepoPaths.Combine("release", "embeddings-payloads.json")));
        foreach (var rid in policy.RootElement.EnumerateObject())
        {
            var files = rid.Value.GetProperty("nativeFiles").EnumerateArray().ToArray();
            files.Should().HaveCount(rid.Name == "osx-arm64" ? 8 : 6);
            foreach (var file in files)
            {
                file.GetProperty("packageVersion").GetString().Should().Be(version);
                string name = file.GetProperty("name").GetString()!;
                bool cpu = rid.Name == "osx-arm64" || name.Contains("ggml-cpu", StringComparison.Ordinal);
                file.GetProperty("package").GetString().Should().Be(cpu
                    ? "llamasharp.backend.cpu"
                    : "llamasharp.backend.vulkan." + (rid.Name == "win-x64" ? "windows" : "linux"));
                file.GetProperty("packageRelativePath").GetString().Should().Be(
                    $"runtimes/{rid.Name}/native/{(rid.Name == "osx-arm64" ? string.Empty : cpu ? "noavx/" : "vulkan/")}{name}");
            }
        }
    }

    /// <summary>The validation workflow routes every manifest expansion through the local publishers.</summary>
    [Fact]
    public void TheReleaseWorkflow_ShipsEveryManifestProject_AndNothingUndeclared()
    {
        foreach (string reason in ReleasedOutsideTheManifest.Values)
            reason.Should().NotBeNullOrWhiteSpace();
        var rows = ReadManifest();
        rows.Should().NotBeEmpty();
        ReleasedOutsideTheManifest.Should().BeEmpty("no additional shipping project is approved");
        ReadWorkflowSteps().SelectMany(step => step.Commands).Where(ContainsDirectDotnetPublish)
            .Should().BeEmpty("workflow steps must route shipping builds through validation and the manifest publishers");
        string validation = ReadValidationScript();
        validation.Split('\n').Where(ContainsDirectDotnetPublish).Should().BeEmpty();
        foreach (string rid in new[] { "win-x64", "linux-x64", "osx-arm64" })
        {
            var calls = ReadValidationPublisherCalls(rid);
            calls.Select(call => call.Name).Should().Equal("publish-plain", "publish-embeddings");
            foreach (var row in rows)
                calls.Should().Contain(call => call.Name == (row.Condition == "with-embeddings" ? "publish-embeddings" : "publish-plain"));
        }
        // The unchanged manifest vocabulary and per-invocation manifest-derived path tests
        // above enforce project membership at the publishers that these calls now reach.
        File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml"))
            .Should().NotContain("./publish.", "publication consumes the validated bytes");
    }

    [Fact]
    public void TheReleaseWorkflow_UsesLocalPublishScriptsAndValidatesBothArchiveLayouts()
    {
        var steps = ReadWorkflowSteps();
        steps.Single(step => step.Name == "Publish, extract, verify and exercise exact candidate")
            .Commands.Should().Equal("./scripts/release/Validate-Release.ps1 -Revision $env:RELEASE_REVISION -Rid '${{ matrix.rid }}' -Output \"$env:RUNNER_TEMP/release-lane\" -CandidatePackage \"$env:RUNNER_TEMP/managed-candidate/scrinia.0.6.0.nupkg\" -CandidatePackageSha256 '${{ needs.build-check.outputs.package-sha256 }}'");
        string script = ReadValidationScript();
        script.Should().Contain("Invoke-Stage \"archive-$kind\" 'tar' @('-a','-cf',$archive,'-C',$tree,'.')");
        script.Should().Contain("Invoke-Stage \"archive-$kind\" 'tar' @('-czf',$archive,'-C',$tree,'.')");
        script.Should().Contain("Invoke-Stage \"extract-$kind\" 'tar' @('-xf',$archive,'-C',$extracted)");
        script.Should().Contain("Compare-Object $before $after -CaseSensitive");
        script.Should().Contain("'lifecycle','--package',$package");
        script.Should().Contain("'--bundle-root',$extracted");
        foreach (string rid in new[] { "win-x64", "linux-x64", "osx-arm64" })
            AssertPublisherCalls(rid, ReadValidationPublisherCalls(rid));
    }

    [Fact]
    public void ReleaseContract_EnumeratesOnlySupportedRidsAndPreservesHostAotRestrictions()
    {
        string[] supportedRids = ["win-x64", "linux-x64", "osx-arm64"];
        string scripts = File.ReadAllText(RepoPaths.Combine("publish.sh")) +
                         File.ReadAllText(RepoPaths.Combine("publish.ps1"));
        string workflow = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release-validation.yml"));
        string validation = ReadValidationScript();

        using var scope = new AssertionScope();
        foreach (string rid in supportedRids)
        {
            scripts.Should().Contain(rid);
            workflow.Should().Contain(rid);
        }

        scripts.Should().Contain("rids=host");
        validation.Should().Contain("[ValidateSet('win-x64','linux-x64','osx-arm64')]");
        validation.Should().Contain("if ($resolved -cne $Rid)");
        foreach (string script in new[] { "publish.sh", "publish.ps1" })
        {
            string text = File.ReadAllText(RepoPaths.Combine(script));
            text.Should().Contain("stage-embeddings");
            text.Should().Contain("release/embeddings-payloads.json");
            text.Should().Contain("-getProperty:Version");
            text.Should().Contain("-getProperty:NuGetPackageRoot");
            text.Should().NotContain("llamasharp.backend.vulkan.");
        }
    }

    private sealed record WorkflowStep(
        string Name, string Shell, string Condition, IReadOnlyList<string> Commands);

    [Fact]
    public void WorkflowParser_CollectsUnnamedScalarAndBlockRunCommandsForDirectPublishChecks()
    {
        string[] fixture =
        [
            "      steps:",
            "      - run: dotnet publish src/Unexpected/Unexpected.csproj",
            "      - name: Literal commands",
            "        shell: bash",
            "        if: always()",
            "        run: |",
            "          dotnet restore",
            "          dotnet publish src/Literal/Literal.csproj",
            "      - name: Folded command",
            "        run: >",
            "          dotnet publish src/Folded/Folded.csproj",
            "          --configuration Release",
        ];

        var steps = ParseWorkflowSteps(fixture);

        steps.SelectMany(step => step.Commands)
            .Should().Contain(command => ContainsDirectDotnetPublish(command));
        steps.Should().Contain(step => step.Name == string.Empty &&
            step.Commands.Contains("dotnet publish src/Unexpected/Unexpected.csproj"));
        steps.Should().Contain(step => step.Name == "Literal commands" &&
            step.Shell == "bash" && step.Condition == "always()" &&
            step.Commands.Contains("dotnet publish src/Literal/Literal.csproj"));
        steps.Should().Contain(step => step.Commands.Any(command =>
            command.Contains("dotnet publish src/Folded/Folded.csproj", StringComparison.Ordinal) &&
            command.Contains("--configuration Release", StringComparison.Ordinal)));
    }

    /// <summary>Reads every workflow step and its scalar, literal, or folded <c>run</c> command.</summary>
    private static List<WorkflowStep> ParseWorkflowSteps(IEnumerable<string> sourceLines)
    {
        string[] lines = sourceLines.ToArray();
        var steps = new List<WorkflowStep>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("      - ", StringComparison.Ordinal)) continue;
            string stepName = lines[i].StartsWith("      - name: ", StringComparison.Ordinal)
                ? lines[i][14..]
                : string.Empty;
            int stepEnd = i + 1;
            while (stepEnd < lines.Length && !lines[stepEnd].StartsWith("      - ", StringComparison.Ordinal))
                stepEnd++;

            string shell = FindStepProperty(lines, i + 1, stepEnd, "shell") ?? string.Empty;
            string condition = FindStepProperty(lines, i + 1, stepEnd, "if") ?? string.Empty;
            bool inlineRun = lines[i].StartsWith("      - run:", StringComparison.Ordinal);
            int runLine = inlineRun
                ? i
                : Array.FindIndex(lines, i + 1, stepEnd - i - 1,
                    line => line.StartsWith("        run:", StringComparison.Ordinal));
            if (runLine < 0)
            {
                steps.Add(new WorkflowStep(stepName, shell, condition, Array.Empty<string>()));
                continue;
            }

            string runPrefix = inlineRun ? "      - run:" : "        run:";
            string runValue = lines[runLine][runPrefix.Length..].Trim();
            if (!runValue.StartsWith('|') && !runValue.StartsWith('>'))
            {
                steps.Add(new WorkflowStep(stepName, shell, condition, [runValue]));
                continue;
            }

            var runBody = new List<string>();
            for (int bodyLine = runLine + 1; bodyLine < stepEnd; bodyLine++)
            {
                if (!string.IsNullOrWhiteSpace(lines[bodyLine]) &&
                    !lines[bodyLine].StartsWith("          ", StringComparison.Ordinal))
                    break;
                runBody.Add(lines[bodyLine].Length >= 10 ? lines[bodyLine][10..] : string.Empty);
            }

            IReadOnlyList<string> commands = runValue.StartsWith('>')
                ? [string.Join(' ', runBody).Trim()]
                : runBody;
            steps.Add(new WorkflowStep(stepName, shell, condition, commands));
        }

        return steps;
    }

    private static List<WorkflowStep> ReadWorkflowSteps() =>
        ParseWorkflowSteps(File.ReadAllLines(RepoPaths.Combine(".github", "workflows", "release-validation.yml")));

    private static string? FindStepProperty(string[] lines, int start, int length, string property)
    {
        Match match = Regex.Match(string.Join('\n', lines.Skip(start).Take(length)),
            $@"(?m)^        {Regex.Escape(property)}:\s*(?<value>.+)$");
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private sealed record PublisherCall(string Name, string File, string[] Arguments);
    private static string ReadValidationScript() =>
        File.ReadAllText(RepoPaths.Combine("scripts", "release", "Validate-Release.ps1"));

    private static PublisherCall[] ReadValidationPublisherCalls(string rid, string? script = null)
    {
        script = (script ?? ReadValidationScript()).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string start = "    foreach ($kind in @('plain','embeddings')) {";
        int begin = script.IndexOf(start, StringComparison.Ordinal);
        int end = script.IndexOf("\n        $name = ", begin, StringComparison.Ordinal);
        begin.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(begin);
        // Execute the production kind loop and dispatch unchanged. Host identity is the only
        // substituted input so all three RID branches can be observed on either test host.
        string dispatch = script[begin..end].Replace("$IsWindows", "$fixtureWindows", StringComparison.Ordinal) + "\n}";
        string directory = Path.Combine(Path.GetTempPath(), "scri-validation-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string wrapper = Path.Combine(directory, "dispatch.ps1");
            File.WriteAllText(wrapper, """
                param($Root,$Output,$Rid)
                $ErrorActionPreference='Stop'
                $fixtureWindows=$Rid -ceq 'win-x64'
                $script:calls=[Collections.Generic.List[object]]::new()
                function Invoke-Stage {
                  param($Name,$File,$Arguments)
                  $script:calls.Add([pscustomobject]@{name=$Name;file=$File;arguments=@($Arguments)})
                }
                """ + "\n" + dispatch + "\nConvertTo-Json -InputObject @($script:calls) -Depth 5 -Compress");
            var result = RunReleaseFixture("pwsh", ["-NoProfile", "-File", wrapper, RepoPaths.Root(), directory, rid]);
            result.Exit.Should().Be(0, result.Output);
            using var json = JsonDocument.Parse(result.Output);
            return json.RootElement.EnumerateArray().Select(call => new PublisherCall(
                call.GetProperty("name").GetString()!, call.GetProperty("file").GetString()!,
                call.GetProperty("arguments").EnumerateArray().Select(arg => arg.GetString()!).ToArray())).ToArray();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssertPublisherCalls(string rid, PublisherCall[] calls)
    {
        calls.Select(call => call.Name).Should().Equal("publish-plain", "publish-embeddings");
        bool windows = rid == "win-x64";
        for (int index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            call.File.Should().Be(windows ? "pwsh" : "bash");
            string script = RepoPaths.Combine(windows ? "publish.ps1" : "publish.sh");
            string tree = call.Arguments.Single(arg => arg.EndsWith(index == 0 ? "plain-tree" : "embeddings-tree", StringComparison.Ordinal));
            string[] expected = windows
                ? ["-NoProfile", "-File", script, "-OutputDir", tree, "-Platform", rid]
                : [script, tree, "--platform=" + rid];
            if (index == 1) expected = [.. expected, windows ? "-WithEmbeddings" : "--with-embeddings"];
            call.Arguments.Should().Equal(expected);
        }
    }

    private static (int Exit, string Output) RunReleaseFixture(string executable, string[] arguments)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new(executable)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("Release contract fixture exceeded deadline."); }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static bool ContainsDirectDotnetPublish(string command)
    {
        // Normalize quoting before classifying the executable token. This intentionally catches
        // `dotnet.exe`, `command dotnet`, `sudo dotnet`, and `bash -c 'dotnet publish ...'`, while
        // approved publish.sh/publish.ps1 invocations do not contain a dotnet executable at the
        // workflow boundary.
        string normalized = Regex.Replace(command, "['\"]", string.Empty).Trim();
        return Regex.IsMatch(normalized, @"(?<![\w.-])dotnet(?:\.exe)?\s+publish\b",
            RegexOptions.IgnoreCase);
    }

    [Fact]
    public void ReleaseWorkflow_UsesExactManifestPublisherInvocationsForEverySupportedTarget()
    {
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        using var reader = File.OpenText(RepoPaths.Combine(".github", "workflows", "release-validation.yml"));
        yaml.Load(reader);
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlDotNet.RepresentationModel.YamlMappingNode)root.Children["jobs"];
        var native = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children["native"];
        var strategy = (YamlDotNet.RepresentationModel.YamlMappingNode)native.Children["strategy"];
        var matrix = (YamlDotNet.RepresentationModel.YamlMappingNode)strategy.Children["matrix"];
        ((YamlDotNet.RepresentationModel.YamlSequenceNode)matrix.Children["include"])
            .Cast<YamlDotNet.RepresentationModel.YamlMappingNode>()
            .Select(item => (Rid: item.Children["rid"].ToString(), Os: item.Children["os"].ToString()))
            .Should().Equal(("win-x64", "windows-latest"), ("linux-x64", "ubuntu-latest"), ("osx-arm64", "macos-15"));
        foreach (string rid in new[] { "win-x64", "linux-x64", "osx-arm64" })
            AssertPublisherCalls(rid, ReadValidationPublisherCalls(rid));
    }

    [Theory]
    [InlineData("win-x64", "'-Platform',$Rid", "'-Platform','linux-x64'")]
    [InlineData("linux-x64", "--platform=$Rid", "--platform=win-x64")]
    [InlineData("osx-arm64", "$args += '--with-embeddings'", "$args += '--wrong-expansion'")]
    public void ValidationPublisherContract_DetectsWrongRidOrExpansion(string rid, string before, string after)
    {
        string mutated = ReadValidationScript().Replace(before, after, StringComparison.Ordinal);
        mutated.Should().NotBe(ReadValidationScript());
        var calls = ReadValidationPublisherCalls(rid, mutated);
        Action check = () => AssertPublisherCalls(rid, calls);
        check.Should().Throw<Exception>("captured production dispatch must expose changed publisher arguments");
    }

    [Fact]
    public void ReleaseWorkflow_ValidatesNativePayloadInTheCorrectRidBranches()
    {
        string lifecycle = File.ReadAllText(RepoPaths.Combine("tools", "Scrinia.ReleaseTools", "ReleaseLifecycle.cs"));
        lifecycle.Should().Contain("EmbeddingsBundleValidator.Validate(bundleRoot, Candidate, rid)");
        lifecycle.Should().Contain("\"native-probe\", \"--payload\", fixturePayload, \"--rid\", bundle.Manifest.Rid");
        var expected = new Dictionary<string, string[]>
        {
            ["win-x64"] = ["ggml-base.dll", "ggml-cpu.dll", "ggml-vulkan.dll", "ggml.dll", "llama.dll", "mtmd.dll"],
            ["linux-x64"] = ["libggml-base.so", "libggml-cpu.so", "libggml-vulkan.so", "libggml.so", "libllama.so", "libmtmd.so"],
            ["osx-arm64"] = ["ggml-metal.metal", "libggml-base.dylib", "libggml-blas.dylib", "libggml-cpu.dylib", "libggml-metal.dylib", "libggml.dylib", "libllama.dylib", "libmtmd.dylib"],
        };
        using var policy = JsonDocument.Parse(File.ReadAllBytes(RepoPaths.Combine("release", "embeddings-payloads.json")));
        policy.RootElement.EnumerateObject().Select(platform => platform.Name).Should().BeEquivalentTo(expected.Keys);
        foreach (var pair in expected)
        {
            policy.RootElement.GetProperty(pair.Key).GetProperty("nativeFiles").EnumerateArray()
                .Select(file => file.GetProperty("name").GetString()).Should().Equal(pair.Value);
            using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", pair.Key);
            var bundle = EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", pair.Key);
            bundle.Manifest.Files.Select(file => file.Name).Should().BeEquivalentTo(
                pair.Value.Append(pair.Key == "win-x64" ? "scri-plugin-embeddings.exe" : "scri-plugin-embeddings"));
            ValidatePackageSourceBytes(pair.Key, policy.RootElement.GetProperty(pair.Key));
        }
    }

    private static void ValidatePackageSourceBytes(string rid, JsonElement policy)
    {
        string directory = Path.Combine(Path.GetTempPath(), "scri-release-package-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string raw = Directory.CreateDirectory(Path.Combine(directory, "raw")).FullName;
            string packages = Directory.CreateDirectory(Path.Combine(directory, "packages")).FullName;
            string root = Directory.CreateDirectory(Path.Combine(directory, "bundle")).FullName;
            string executable = policy.GetProperty("executable").GetString()!;
            File.WriteAllText(Path.Combine(raw, executable), "published plugin");
            var sources = new Dictionary<string, string>();
            foreach (var native in policy.GetProperty("nativeFiles").EnumerateArray())
            {
                string name = native.GetProperty("name").GetString()!;
                string path = Path.Combine(packages, native.GetProperty("package").GetString()!,
                    native.GetProperty("packageVersion").GetString()!, native.GetProperty("packageRelativePath").GetString()!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "authoritative package source " + rid + "/" + name);
                File.WriteAllText(Path.Combine(raw, name), "wrong raw duplicate " + name);
                sources.Add(name, path);
            }
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            string tool = output.Parent is { Name: "Scrinia.Tests", Parent.Name: "bin" } project
                ? Path.Combine(project.Parent.FullName, "Scrinia.ReleaseTools", output.Name, "Scrinia.ReleaseTools.dll")
                : RepoPaths.Combine("tools", "Scrinia.ReleaseTools", "bin", output.Parent!.Name, output.Name, "Scrinia.ReleaseTools.dll");
            var result = RunReleaseFixture("dotnet", [tool, "stage-embeddings", "--raw", raw, "--root", root,
                "--rid", rid, "--version", "0.6.0", "--nuget-root", packages,
                "--policy", RepoPaths.Combine("release", "embeddings-payloads.json")]);
            result.Exit.Should().Be(0, result.Output);
            var verified = EmbeddingsBundleValidator.Validate(root, "0.6.0", rid);
            foreach (var source in sources)
            {
                byte[] bytes = File.ReadAllBytes(source.Value);
                File.ReadAllBytes(Path.Combine(verified.PayloadDirectory, source.Key)).Should().Equal(bytes);
                verified.Manifest.Files.Single(file => file.Name == source.Key).Sha256
                    .Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("win-x64", "changed-bytes")]
    [InlineData("linux-x64", "changed-bytes")]
    [InlineData("osx-arm64", "changed-bytes")]
    [InlineData("win-x64", "foreign-native")]
    [InlineData("linux-x64", "foreign-native")]
    [InlineData("osx-arm64", "foreign-native")]
    [InlineData("osx-arm64", "vulkan-dylib")]
    public void ExtractedPayloadVerifier_RefusesTamperedOrForeignNativeFiles(string rid, string mutation)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", rid);
        string archive = fixture.Root + ".zip", extracted = fixture.Root + "-extracted";
        try
        {
            ZipFile.CreateFromDirectory(fixture.Root, archive);
            ZipFile.ExtractToDirectory(archive, extracted);
            var bundle = EmbeddingsBundleValidator.Validate(extracted, "0.6.0", rid);
            if (mutation == "changed-bytes")
            {
                string native = bundle.Manifest.Files.First(file => file.Name != "scri-plugin-embeddings" && file.Name != "scri-plugin-embeddings.exe").Name;
                string path = Path.Combine(bundle.PayloadDirectory, native);
                byte[] bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes);
            }
            else
                File.WriteAllText(Path.Combine(bundle.PayloadDirectory,
                    mutation == "vulkan-dylib" ? "libggml-vulkan.dylib" : rid == "linux-x64" ? "llama.dll" : "libllama.so"), "foreign payload");
            Action verify = () => EmbeddingsBundleValidator.Validate(extracted, "0.6.0", rid);
            verify.Should().Throw<EmbeddingsBundleException>();
        }
        finally { File.Delete(archive); if (Directory.Exists(extracted)) Directory.Delete(extracted, true); }
    }

    private static YamlDotNet.RepresentationModel.YamlMappingNode ReadPublicationJobs(string workflow)
    {
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        yaml.Load(new StringReader(workflow));
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;
        return (YamlDotNet.RepresentationModel.YamlMappingNode)root.Children["jobs"];
    }

    private static string[] JobRunCommands(YamlDotNet.RepresentationModel.YamlMappingNode job) =>
        ((YamlDotNet.RepresentationModel.YamlSequenceNode)job.Children["steps"]).Children
            .Cast<YamlDotNet.RepresentationModel.YamlMappingNode>()
            .Where(step => step.Children.ContainsKey("run"))
            .Select(step => step.Children["run"].ToString()).ToArray();

    private static void AssertFinalPublicationConsumesValidatedBytes(string workflow)
    {
        var jobs = ReadPublicationJobs(workflow);
        var final = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children["publish"];
        JobRunCommands(final).Where(ContainsFinalPublicationProducer).Should().BeEmpty(
            "the final publication job must consume transferred validated bytes without producing replacements");
    }

    private static bool ContainsFinalPublicationProducer(string command)
    {
        string normalized = Regex.Replace(command, "['\"]", string.Empty);
        return Regex.IsMatch(normalized, @"(?<![\w.-])dotnet(?:\.exe)?\s+(?:build|pack|publish)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(normalized, @"(?<![\w.-])publish\.(?:ps1|sh)\b", RegexOptions.IgnoreCase);
    }

    [Fact]
    public void PublicationJob_ConsumesValidatedBytesAndPermitsIsolatedPreflightHelper()
    {
        string workflow = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml"));
        AssertFinalPublicationConsumesValidatedBytes(workflow);
        var jobs = ReadPublicationJobs(workflow);
        var preflight = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children["preflight"];
        string helperBuild = JobRunCommands(preflight).Single(command => command.Contains("dotnet build", StringComparison.Ordinal));
        helperBuild.Should().Contain("tools/Scrinia.ReleaseTools");
        helperBuild.Should().Contain("-p:PublicationOnly=true");
        helperBuild.Should().Contain("--artifacts-path");
        helperBuild.Should().Contain("--output");
        helperBuild.Should().NotContain("Scrinia.sln");
    }

    [Theory]
    [InlineData("scalar", "dotnet publish src/Scrinia/Scrinia.csproj")]
    [InlineData("scalar", "dotnet pack src/Scrinia/Scrinia.csproj")]
    [InlineData("block", "dotnet build Scrinia.sln -c Release\nWrite-Output done")]
    [InlineData("folded", "dotnet\npublish src/Scrinia/Scrinia.csproj")]
    [InlineData("block", "command dotnet.exe publish src/Scrinia/Scrinia.csproj")]
    [InlineData("scalar", "bash -c 'dotnet pack src/Scrinia/Scrinia.csproj'")]
    [InlineData("scalar", "pwsh -Command \"& dotnet build src/Scrinia/Scrinia.csproj\"")]
    [InlineData("block", "./publish.ps1 -OutputDir replacements -Platform win-x64")]
    [InlineData("folded", "bash ./publish.sh\nreplacements --platform=linux-x64")]
    [InlineData("scalar", "dotnet build tools/Scrinia.ReleaseTools -p:PublicationOnly=true")]
    public void PublicationJob_RejectsInsertedProducerCommands(string style, string command)
    {
        string workflow = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml")).TrimEnd();
        string run = style == "scalar" ? JsonSerializer.Serialize(command)
            : (style == "block" ? "|\n" : ">\n") +
              string.Join('\n', command.Split('\n').Select(line => "          " + line));
        // Append one valid step to the current final job; parsing proves the fixture did not
        // accidentally land in preflight or disappear into a literal block or comment.
        string fixture = workflow + "\n      - name: Forbidden producer fixture\n        run: " + run + "\n";
        var jobs = ReadPublicationJobs(fixture);
        var final = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children["publish"];
        JobRunCommands(final).Select(runCommand => Regex.Replace(runCommand, @"\s+", " ").Trim())
            .Should().Contain(Regex.Replace(command, @"\s+", " ").Trim());
        Action guard = () => AssertFinalPublicationConsumesValidatedBytes(fixture);
        guard.Should().Throw<Exception>("a valid final-job producer insertion must trip the parsed publication guard");
    }

}
