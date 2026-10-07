using System.Diagnostics;
using System.Text.RegularExpressions;
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
        ["Scrinia.Benchmarks"] =
            "Benchmark harness under tests/ — run from source via `dotnet run`, never distributed " +
            "to users.",
        ["Scrinia.TestProcess"] =
            "Child-process harness for admission and vector integration tests; built by the " +
            "solution for tests and never distributed to users.",
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
                    ? "@echo off\r\nexit /b 0\r\n"
                    : "#!/usr/bin/env bash\nexit 0\n");
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
    public void VulkanBackendVersion_MatchesThePackageReference()
    {
        string csproj = File.ReadAllText(RepoPaths.Combine(
            "src", "Scrinia.Plugin.Embeddings", "Scrinia.Plugin.Embeddings.csproj"));
        var declared = Regex.Match(csproj,
            """<PackageReference\s+Include="LLamaSharp\.Backend\.Vulkan"\s+Version="([^"]+)"\s*/?>""");
        declared.Success.Should().BeTrue(because: "the plugin must still reference the Vulkan backend");
        string version = declared.Groups[1].Value;

        // Anchored on the assignment itself and compared for equality, not a bare substring
        // search over the whole script: a substring check still passes if the OLD version
        // survives anywhere else in the file — e.g. a "# upgraded from 0.25.0" comment left next
        // to a bumped assignment — on exactly the broken tree this test exists to catch.
        var scriptVersionPatterns = new Dictionary<string, string>
        {
            ["publish.sh"] = @"VULKAN_BACKEND_VERSION=""([^""]+)""",
            ["publish.ps1"] = @"\$VulkanBackendVersion\s*=\s*'([^']+)'",
        };

        using var scope = new AssertionScope();
        foreach ((string script, string pattern) in scriptVersionPatterns)
        {
            string text = File.ReadAllText(RepoPaths.Combine(script));
            var matches = Regex.Matches(text, pattern);
            matches.Should().HaveCount(1,
                because: $"{script} must declare VULKAN_BACKEND_VERSION exactly once — a second " +
                         "assignment could disagree with the first and neither would be caught by name");

            if (matches.Count == 1)
            {
                matches[0].Groups[1].Value.Should().Be(version,
                    because: $"{script} builds the Vulkan native path from this hardcoded version; when " +
                             "it drifts from the PackageReference the path does not exist and the " +
                             "overwrite is skipped, shipping the CPU backend under a Vulkan switch");
            }
        }
    }

    /// <summary>
    /// The manifest governs the two developer scripts; .github/workflows/release.yml is a third,
    /// hand-maintained list — and the one that produces the archives users download. Without this
    /// rule a new project could be added to the manifest, pass every check above, and never ship.
    /// </summary>
    [Fact]
    public void TheReleaseWorkflow_ShipsEveryManifestProject_AndNothingUndeclared()
    {
        // Vacuously true while the set is empty — which it is, since Scrinia.Server was its only
        // entry. AllSatisfy throws on an empty collection, so the emptiness is handled rather than
        // asserted away: "every entry carries a reason" is a statement about entries, and zero
        // entries satisfy it.
        foreach (string reason in ReleasedOutsideTheManifest.Values)
            reason.Should().NotBeNullOrWhiteSpace(
                because: "an exclusion with no reason is exactly the unexplained absence this test exists to prevent");

        var manifestRows = ReadManifest();
        var manifestProjects = manifestRows
            .Select(r => Path.GetFileNameWithoutExtension(r.Project))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        manifestProjects.Should().NotBeEmpty(because: "an emptied manifest must not make this loop vacuous");

        var workflowTargets = ReadWorkflowPublishTargets();
        workflowTargets.Should().NotBeEmpty(because: "the release workflow must invoke a publisher");

        var expectedTargets = manifestRows
            .SelectMany<ManifestRow, string>(row => row.Condition == "with-embeddings"
                ? ["publish-embeddings"]
                : ["publish", "publish-embeddings"])
            .ToHashSet(StringComparer.Ordinal);
        workflowTargets.Select(target => target.OutputDir).ToHashSet(StringComparer.Ordinal)
            .Should().Equal(expectedTargets,
                because: "the workflow must have one plain and one optional/plugin expansion " +
                         "target, so every manifest row is covered by the scripts");

        using var scope = new AssertionScope();
        foreach (var row in ReadManifest())
        {
            string requiredTarget = row.Condition == "with-embeddings" ? "publish-embeddings" : "publish";
            workflowTargets.Should().Contain(target => target.OutputDir == requiredTarget,
                because: $"manifest row '{row.Id}' must be covered by the {requiredTarget} expansion");
        }
    }

    [Fact]
    public void TheReleaseWorkflow_UsesLocalPublishScriptsAndValidatesBothArchiveLayouts()
    {
        string yaml = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml"));
        int expectedTargetCount = ReadManifest()
            .SelectMany<ManifestRow, string>(row => row.Condition == "with-embeddings"
                ? ["publish-embeddings"]
                : ["publish", "publish-embeddings"])
            .Distinct(StringComparer.Ordinal)
            .Count();

        using var scope = new AssertionScope();
        ReadWorkflowPublishTargets().Should().HaveCount(expectedTargetCount * 2,
            because: "each supported publish target has exactly one Windows and one Unix invocation");
        yaml.Should().Contain("--with-embeddings");
        yaml.Should().Contain("plugins/scri-plugin-embeddings");
        yaml.Should().Contain("scri-plugin-embeddings");
        yaml.Should().Contain("tar -tzf");
        yaml.Should().Contain("Expand-Archive");
        yaml.Should().NotContain("scri-merge");
    }

    [Fact]
    public void ReleaseContract_EnumeratesOnlySupportedRidsAndPreservesHostAotRestrictions()
    {
        string[] supportedRids = ["win-x64", "linux-x64", "osx-arm64"];
        string scripts = File.ReadAllText(RepoPaths.Combine("publish.sh")) +
                         File.ReadAllText(RepoPaths.Combine("publish.ps1"));
        string workflow = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml"));
        string manifest = File.ReadAllText(ManifestPath);

        using var scope = new AssertionScope();
        foreach (string rid in supportedRids)
        {
            scripts.Should().Contain(rid);
            workflow.Should().Contain(rid);
        }

        scripts.Should().Contain("rids=host");
        scripts.Should().Contain("Set-VulkanNatives");
        scripts.Should().Contain("overwrite_vulkan_natives");
    }

    private sealed record WorkflowStep(
        string Name, string Shell, string Condition, IReadOnlyList<string> Commands);

    private sealed record WorkflowPublishTarget(
        string StepName, string Shell, string Condition, string ScriptPath, string OutputDir,
        bool WithEmbeddings, string Command);

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
        ParseWorkflowSteps(File.ReadAllLines(RepoPaths.Combine(".github", "workflows", "release.yml")));

    private static string? FindStepProperty(string[] lines, int start, int length, string property)
    {
        Match match = Regex.Match(string.Join('\n', lines.Skip(start).Take(length)),
            $@"(?m)^        {Regex.Escape(property)}:\s*(?<value>.+)$");
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static List<WorkflowPublishTarget> ReadWorkflowPublishTargets()
    {
        var targets = new List<WorkflowPublishTarget>();
        foreach (WorkflowStep step in ReadWorkflowSteps()
                     .Where(step => step.Name.StartsWith("Publish release trees", StringComparison.Ordinal)))
        {
            foreach (string command in step.Commands)
            {
                Match windows = Regex.Match(command,
                    @"^\./publish\.ps1 -OutputDir (?<output>\S+) -Platform \$\{\{ matrix\.rid \}\}(?<emb> -WithEmbeddings)?$");
                Match unix = Regex.Match(command,
                    @"^\./publish\.sh (?<output>\S+) --platform=\$\{\{ matrix\.rid \}\}(?<emb> --with-embeddings)?$");
                if (windows.Success)
                    targets.Add(new WorkflowPublishTarget(step.Name, step.Shell, step.Condition,
                        "./publish.ps1", windows.Groups["output"].Value, windows.Groups["emb"].Success,
                        command));
                else if (unix.Success)
                    targets.Add(new WorkflowPublishTarget(step.Name, step.Shell, step.Condition,
                        "./publish.sh", unix.Groups["output"].Value, unix.Groups["emb"].Success,
                        command));
            }
        }

        return targets;
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
        string[] supportedRids = ["win-x64", "linux-x64", "osx-arm64"];
        string workflow = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml"));
        var targets = ReadWorkflowPublishTargets();
        var publishSteps = ReadWorkflowSteps()
            .Where(step => step.Name.StartsWith("Publish release trees", StringComparison.Ordinal))
            .ToList();

        publishSteps.Select(step => step.Name).Should().Equal(
            "Publish release trees (Windows)", "Publish release trees (Unix)");
        publishSteps.Select(step => step.Shell).Should().Equal("pwsh", "bash");
        publishSteps.Select(step => step.Condition).Should().Equal(
            "runner.os == 'Windows'", "runner.os != 'Windows'");
        Regex.Matches(workflow,
                @"(?ms)^\s*- rid: (?<rid>\S+)\s*\r?\n\s*os: (?<os>\S+)")
            .Select(match => (Rid: match.Groups["rid"].Value, Os: match.Groups["os"].Value))
            .Should().Equal(
                ("win-x64", "windows-latest"),
                ("linux-x64", "ubuntu-latest"),
                ("osx-arm64", "macos-latest"));
        targets.Where(target => target.ScriptPath == "./publish.ps1")
            .Select(target => target.OutputDir).Should().Equal("publish", "publish-embeddings");
        targets.Where(target => target.ScriptPath == "./publish.sh")
            .Select(target => target.OutputDir).Should().Equal("publish", "publish-embeddings");
        targets.Count(target => target.WithEmbeddings).Should().Be(2,
            because: "the plugin is an optional manifest expansion, not a second hand-maintained project list");
        targets.Count(target => !target.WithEmbeddings).Should().Be(2);
        targets.Where(target => !target.WithEmbeddings)
            .Select(target => target.Command)
            .Should().OnlyContain(command => !Regex.IsMatch(command, "with-embeddings", RegexOptions.IgnoreCase));
        targets.Where(target => target.WithEmbeddings)
            .Select(target => Regex.Count(target.Command, "(?:-WithEmbeddings|--with-embeddings)", RegexOptions.IgnoreCase))
            .Should().OnlyContain(count => count == 1);

        ReadWorkflowSteps().SelectMany(step => step.Commands)
            .Where(ContainsDirectDotnetPublish)
            .Should().BeEmpty(
                because: "release run blocks must not directly publish outside the approved scripts, " +
                         "including dotnet.exe and shell-wrapped variants");

        var matrixRids = Regex.Matches(workflow, @"^\s*-\s*rid:\s*(\S+)\r?$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).ToArray();
        matrixRids.Should().Equal(supportedRids,
            because: "the workflow matrix is the complete supported publish-target set");
        foreach (string rid in supportedRids)
        {
            workflow.Should().Contain("${{ matrix.rid }}",
                because: $"the {rid} target must be passed to the manifest-driven publisher");
        }
        targets.Select(target => target.Command)
            .Should().OnlyContain(command => command.Contains("${{ matrix.rid }}", StringComparison.Ordinal));
    }

    private static string ExtractWorkflowBlock(string workflow, string start, string end)
    {
        workflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        Match match = Regex.Match(
            workflow,
            $"{Regex.Escape(start)}(?<block>.*?){Regex.Escape(end)}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue(
            because: $"release.yml must contain a workflow block from '{start}' to '{end}'");
        return match.Groups["block"].Value;
    }

    [Fact]
    public void ReleaseWorkflow_ValidatesNativePayloadInTheCorrectRidBranches()
    {
        string workflow = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml"));
        string linuxBranch = ExtractWorkflowBlock(
            workflow,
            "if [[ \"${{ matrix.rid }}\" == \"linux-x64\" ]]; then",
            "elif [[ \"${{ matrix.rid }}\" == \"osx-arm64\" ]]; then");
        string macBranch = ExtractWorkflowBlock(
            workflow,
            "elif [[ \"${{ matrix.rid }}\" == \"osx-arm64\" ]]; then",
            "\n           fi");
        string windowsStep = ExtractWorkflowBlock(
            workflow,
            "      - name: Archive and validate release trees (Windows)\n",
            "      - name: Upload release assets");
        string[] linuxVulkanFiles =
        ["libmtmd.so", "libllama.so", "libggml.so", "libggml-vulkan.so", "libggml-base.so"];
        string[] macCpuFiles =
        [
            "ggml-metal.metal",
            "libggml-base.dylib",
            "libggml-blas.dylib",
            "libggml-cpu.dylib",
            "libggml-metal.dylib",
            "libggml.dylib",
            "libllama.dylib",
            "libmtmd.dylib",
        ];
        string[] windowsVulkanFiles =
        ["mtmd.dll", "llama.dll", "ggml.dll", "ggml-vulkan.dll", "ggml-base.dll"];

        using var scope = new AssertionScope();
        foreach (string file in linuxVulkanFiles)
        {
            linuxBranch.Should().Contain(file,
                because: $"Linux Vulkan validation must require {file} inside the linux-x64 branch");
            macBranch.Should().NotContain(file,
                because: $"macOS validation must not mention Linux Vulkan file {file}");
        }

        foreach (string file in macCpuFiles)
        {
            macBranch.Should().Contain(file,
                because: $"the macOS CPU fallback emitted by publish.sh must validate {file}");
        }

        foreach (string file in windowsVulkanFiles)
        {
            windowsStep.Should().Contain(file,
                because: $"Windows archive validation must retain Vulkan file {file}");
        }

        windowsStep.Should().Contain("llamasharp.backend.vulkan.windows",
            because: "Windows validation must compare against the Vulkan package source");
        Regex.IsMatch(
                windowsStep,
                @"^\s*\$sourcePath\s*=\s*Join-Path \$HOME .*\.nuget/packages/\$sourcePackage/",
                RegexOptions.Multiline)
            .Should().BeTrue(
            because: "Windows validation must construct the native package-source path");
        Regex.IsMatch(
                windowsStep,
                @"^\s*if \(\(Get-FileHash \$archivePath -Algorithm SHA256\)\.Hash -cne " +
                @"\(Get-FileHash \$sourcePath -Algorithm SHA256\)\.Hash\)",
                RegexOptions.Multiline)
            .Should().BeTrue(
            because: "Windows validation must compare archive and package-source SHA256 hashes");

        Match macNativeDeclaration = Regex.Match(
            macBranch,
            @"cpu_native_names=\((?<files>[^)]*)\)",
            RegexOptions.Singleline);
        macNativeDeclaration.Success.Should().BeTrue(
            because: "macOS validation must declare the approved CPU fallback payload");
        if (macNativeDeclaration.Success)
        {
            var declaredMacFiles = Regex.Matches(macNativeDeclaration.Groups["files"].Value, "\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            declaredMacFiles.Should().BeEquivalentTo(macCpuFiles,
                because: "the macOS fallback allowlist must be exact, not merely a set of required names");
        }

        macBranch.Should().Contain(".so",
            because: "macOS validation must explicitly reject Linux native payloads");
        macBranch.Should().Contain("vulkan",
            because: "macOS validation must explicitly reject Vulkan-named artifacts");
        macBranch.Should().Contain(".dylib",
            because: "macOS validation must distinguish approved CPU dylibs from Vulkan dylibs");
        Regex.IsMatch(macBranch, @"(?i)(vulkan[^\s""']*\.dylib|\.dylib[^\s""']*vulkan)")
            .Should().BeTrue(
                because: "macOS validation must reject Vulkan-named dylibs, not only Linux .so files");
    }
}
