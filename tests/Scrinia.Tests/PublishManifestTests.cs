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
    /// Every `dotnet publish &lt;project&gt;` line in the release workflow. This is the list that
    /// actually produces what users download, and it is hand-maintained: rule 5 below is what
    /// stops it drifting from the manifest.
    /// </summary>
    private static List<string> ReleaseWorkflowProjects()
    {
        string yaml = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml"));
        return Regex.Matches(yaml, @"dotnet publish\s+(\S+\.csproj)")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
        var rows = new List<ManifestRow>();
        // Skip a leading run of comment lines, then exactly the header row that follows them —
        // so a banner comment ever added above the header doesn't get treated as it and let the
        // real header row be parsed as data.
        foreach (string line in File.ReadAllLines(ManifestPath).SkipWhile(l => l.StartsWith('#')).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] parts = line.Split('\t');
            parts.Should().HaveCount(6, because: $"every manifest row has six tab-separated columns: '{line}'");
            rows.Add(new ManifestRow(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]));
        }
        return rows;
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

        var manifestProjects = ReadManifest()
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

        var manifestProjects = ReadManifest()
            .Select(r => Path.GetFileNameWithoutExtension(r.Project))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        manifestProjects.Should().NotBeEmpty(because: "an emptied manifest must not make this loop vacuous");
        var released = ReleaseWorkflowProjects();

        released.Should().NotBeEmpty(because: "the regex must not silently match nothing");

        using var scope = new AssertionScope();
        foreach (string name in manifestProjects)
        {
            released.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue(
                    because: $"{name} is in publish.projects.tsv but release.yml never publishes it, so " +
                             "it would ship to nobody");
        }

        foreach (string name in released)
        {
            (manifestProjects.Contains(name) || ReleasedOutsideTheManifest.ContainsKey(name))
                .Should().BeTrue(
                    because: $"release.yml publishes {name}, but it is neither in publish.projects.tsv " +
                             "nor named in ReleasedOutsideTheManifest — decide which it is and record " +
                             "the reason. Naming it in ExcludedFromPublish is not enough: that set also " +
                             "contains Scrinia.AppHost, which must never appear in a release");
        }
    }
}
