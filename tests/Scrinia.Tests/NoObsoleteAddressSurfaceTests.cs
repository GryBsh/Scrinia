using System.Reflection;
using System.Diagnostics;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using System.Text.RegularExpressions;

namespace Scrinia.Tests;

public sealed class NoObsoleteAddressSurfaceTests
{
    private static readonly Regex ForbiddenSurface = new(
        "ArtifactEntry\\.Uri|ArtifactUri|ResolveArtifactAsync|file://|MemoryWrite\\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // This is intentionally path-exact. Historical plans/release notes, rejection examples,
    // compatibility fixtures, and stale fixture prose may retain the removed vocabulary; living
    // production and reference surfaces may not. New exceptions must be classified here by file,
    // rather than weakening the expression or ignoring a whole directory.
    private static readonly IReadOnlyDictionary<string, string> AcceptedHistoricalText =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHANGELOG.md"] = "released history",
            ["task-11-report.md"] = "historical acceptance report",
            [".superpowers/sdd/2026-08-29-unified-memory-addressing-task-1/task-10-debug-report.md"] = "historical task report",
            [".superpowers/sdd/2026-08-29-unified-memory-addressing-whole-branch-review/FINAL-REVIEW.md"] = "historical whole-branch review",
            ["docs/superpowers/plans/2026-08-29-unified-memory-addressing.md"] = "historical plan",
            ["docs/superpowers/plans/2026-09-06-wave-1-critical.md"] = "historical plan",
            ["docs/superpowers/plans/2026-09-06-wave-2-major.md"] = "historical plan",
            ["docs/superpowers/plans/2026-09-06-wave-3-minor.md"] = "historical plan",
            ["docs/superpowers/specs/2026-09-06-review-remediation-design.md"] = "historical specification",
            ["docs/superpowers/specs/2026-08-29-unified-memory-addressing-design.md"] = "historical specification",
            ["docs/cli-reference.md"] = "explicit unsupported-input guidance",
            ["docs/getting-started.md"] = "explicit unsupported-input guidance",
            ["docs/plugin-authoring.md"] = "explicit plugin rejection guidance",
            ["docs/security.md"] = "explicit unsupported-input guidance",
            ["src/Scrinia.Mcp/prompts/guide.md"] = "explicit unsupported-input guidance",
            ["src/Scrinia.Core/Models/MemoryWrite.cs"] = "typed constructor implementation",
            ["tests/Scrinia.Tests/NoObsoleteAddressSurfaceTests.cs"] = "this gate's pattern and allowlist",
            ["tests/Scrinia.Tests/CliAddressValidationTests.cs"] = "negative address rejection tests",
            ["tests/Scrinia.Tests/McpAddressSurfaceTests.cs"] = "negative address rejection tests",
            ["tests/Scrinia.Tests/MemoryAddressTests.cs"] = "negative address rejection tests",
            ["tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs"] = "negative serialization tests",
            ["tests/Scrinia.Tests/BundleArtifactKeyPortabilityTests.cs"] = "negative artifact portability tests",
            ["tests/Scrinia.Tests/ContractTypingTests.cs"] = "negative contract tests",
            ["tests/Scrinia.Tests/ArtifactKeyBoundaryTests.cs"] = "negative artifact-key tests",
            ["tests/Scrinia.Tests/MemoryWriteValidationTests.cs"] = "negative write validation tests",
            ["tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginReindexTests.cs"] = "stale fixture prose",
            ["tests/Scrinia.Tests/Embeddings/EmbeddingReindexerNamespacedScopeTests.cs"] = "stale fixture prose",
            ["benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs"] = "stale benchmark prose",
            ["tests/Scrinia.Tests/AttributeExtractorTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/BundleExitCodeTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/CliDisplayCutoverTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/CliLifecycleFactFlushTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/CliRecoveryBlockedRouteTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/DriftRenameTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/DualModeParseTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/EntityFactDerivationTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/EntityFactMaterializerTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/FileLockTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/FileMemoryStoreTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/LazyStoreCreationTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/ListLineEndingTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/LoadIndexCacheHitTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/McpDisplayCutoverTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/RecoveryBlockedFailureClassCoverageTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/ScriniaMcpToolsTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/SearchCandidatesHintTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs"] = "legacy compatibility fixtures",
            ["tests/Scrinia.Tests/StoreLayoutMigrationTests.cs"] = "legacy compatibility fixtures"
        };

    [Fact]
    public void Forbidden_address_surface_is_absent_outside_classified_historical_text()
    {
        string root = FindRepositoryRoot();
        var violations = new List<string>();

        foreach (string file in EnumerateReviewableTextFiles(root))
        {
            string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            Match? firstMatch = File.ReadLines(file)
                .SelectMany(line => ForbiddenSurface.Matches(line).Cast<Match>())
                .FirstOrDefault();
            if (firstMatch is null)
                continue;

            if (!AcceptedHistoricalText.TryGetValue(relativePath, out string? classification))
            {
                violations.Add($"{relativePath}: {firstMatch.Value}");
                continue;
            }

            _ = classification;
        }

        violations.Should().BeEmpty("only explicitly classified historical, rejection, or fixture text may retain removed surface vocabulary");
    }

    [Fact]
    public void Ignored_large_dataset_files_are_not_scan_candidates()
    {
        EnumerateReviewableTextFiles(FindRepositoryRoot())
            .Should().NotContain(file => file.Replace('\\', '/').Contains("/datasets/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Public_memory_contracts_do_not_expose_uri_or_resolver_members()
    {
        var contractTypes = new[]
        {
            typeof(ArtifactEntry), typeof(IMemoryStore), typeof(IPluginStore),
            typeof(IMemoryEventSink), typeof(MemoryWrite), typeof(MemorySearchRequest),
            typeof(MemoryListRequest)
        };

        foreach (var type in contractTypes)
        {
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Should().NotContain(property => property.Name == "Uri" || property.Name == "ArtifactUri");
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Should().NotContain(method => method.Name == "ArtifactUri" || method.Name == "ResolveArtifactAsync");
        }
    }

    [Fact]
    public void Artifact_and_write_constructors_require_typed_addresses()
    {
        typeof(ArtifactEntry).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().OnlyContain(constructor =>
                constructor.GetParameters().First().ParameterType == typeof(MemoryAddress));
        typeof(MemoryWrite).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().OnlyContain(constructor =>
                constructor.GetParameters().First().ParameterType == typeof(MemoryAddress));
        typeof(MemoryWrite).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().NotContain(constructor =>
                constructor.GetParameters().Length > 0 &&
                constructor.GetParameters()[0].ParameterType == typeof(string));
    }

    [Fact]
    public void Public_store_and_plugin_routes_accept_typed_addresses()
    {
        typeof(IMemoryStore).GetMethod(nameof(IMemoryStore.ReadAsync))!
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
        typeof(IMemoryStore).GetMethod(nameof(IMemoryStore.WriteAsync))!
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
        typeof(IMemoryStore).GetMethod(nameof(IMemoryStore.DeleteAsync))!
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
        typeof(IPluginStore).GetMethod(nameof(IPluginStore.ReadArtifactAsync))!
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
    }

    [Fact]
    public void Physical_store_helpers_are_not_published_as_logical_address_routes()
    {
        typeof(FileMemoryStore).GetMethod(nameof(FileMemoryStore.ParseQualifiedName))
            .Should().BeNull();
        typeof(FileMemoryStore).GetMethod(nameof(FileMemoryStore.IsEphemeral))
            .Should().BeNull();
        typeof(MemoryArtifactSource).GetMethod(nameof(MemoryArtifactSource.IsEphemeral))
            .Should().BeNull();
        typeof(MemoryVectorIndexer).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Should().NotContain(method => method.Name == "RemoveAsync" &&
                method.GetParameters().Length > 0 &&
                method.GetParameters()[0].ParameterType == typeof(string));
    }

    [Fact]
    public void Protocol_address_adapters_accept_json_null()
    {
        var nullability = new NullabilityInfoContext();
        var addressParameterNames = new HashSet<string>(StringComparer.Ordinal)
            { "path", "destination", "name" };

        foreach (var method in typeof(ScriniaMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!method.GetCustomAttributes(false).Any(attribute =>
                    attribute.GetType().Name == "McpServerToolAttribute"))
                continue;

            foreach (var parameter in method.GetParameters().Where(parameter =>
                         addressParameterNames.Contains(parameter.Name ?? "")))
                nullability.Create(parameter).ReadState.Should().Be(NullabilityState.Nullable,
                    $"MCP adapter {method.Name}.{parameter.Name} must classify JSON null itself");
        }

        var resourceParameter = typeof(ScriniaMcpResources).GetMethod(nameof(ScriniaMcpResources.ReadMemory))!
            .GetParameters().Single(parameter => parameter.Name == "name");
        nullability.Create(resourceParameter).ReadState.Should().Be(NullabilityState.Nullable);
    }

    private static IEnumerable<string> EnumerateReviewableTextFiles(string root)
    {
        using var process = System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "ls-files", "-z" }
        }) ?? throw new InvalidOperationException("Could not start git to enumerate tracked files.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git ls-files failed: {error}");

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(IsReviewableTextFile)
            .Select(relativePath => Path.Combine(root, relativePath));
    }

    private static bool IsReviewableTextFile(string path)
    {
        string normalized = "/" + path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.scrinia/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/datasets/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/BenchmarkDotNet.Artifacts/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/src/Scrinia.Server/", StringComparison.OrdinalIgnoreCase))
            return false;

        string extension = Path.GetExtension(path);
        return extension is ".cs" or ".md" or ".csproj" or ".props" or ".targets" or ".json";
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Scrinia.sln")))
                return directory.FullName;

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

}
