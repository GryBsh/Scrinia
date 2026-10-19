using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Scrinia.Core.Process;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>Artifact-level contract tests for the immutable <c>scri</c> global tool package.</summary>
// GlobalWorkspaceConfigCollection: both snapshots shell `dotnet pack` on the shared
// src/Scrinia project (shared bin/obj state). GlobalToolInstallTests packs the same
// project with -p:Version overrides; a concurrent version-stamped pack between the two
// snapshots can skew assembly or package metadata, so these packs must serialize with
// that collection. The pinned SDK generates a fresh GUID for the OPC core-properties part.
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class PackageContentTests
{
    private static readonly Lazy<PackagePair> Packs = new(
        PackagePair.Build, LazyThreadSafetyMode.ExecutionAndPublication);

    private static PackageSnapshot Snapshot => Packs.Value.First;

    [Fact]
    public void Package_DeclaresExactToolMetadata()
    {
        Snapshot.Metadata.Id.Should().Be("scrinia");
        Snapshot.Metadata.Version.Should().Be("0.6.0");
        Snapshot.Metadata.Authors.Should().Be("Scrinia");
        Snapshot.Metadata.LicenseType.Should().Be("expression");
        Snapshot.Metadata.LicenseValue.Should().Be("BSD-3-Clause");
        Snapshot.Metadata.Readme.Should().Be("package-readme.md");
        Snapshot.Metadata.PackageType.Should().Be("DotnetTool");
        Snapshot.Metadata.Description.Should().Be(
            "Scrinia: portable LLM memory. Installs the `scri` global tool (local + shared MCP servers, BM25 + hybrid search, NMP/2 memory), with models and plugins living under SCRINIA_HOME.");
        Snapshot.Metadata.Tags.Should().Be(
            "scrinia llm memory mcp vector-search bm25 named-memory-protocol");
    }

    [Fact]
    public void Package_PutsScriCommandUnderTools_NotLib()
    {
        Snapshot.EntryPaths.Should().Contain(path =>
            path.StartsWith("tools/", StringComparison.Ordinal) &&
            path.EndsWith("/scri.dll", StringComparison.OrdinalIgnoreCase));
        Snapshot.EntryPaths.Should().NotContain(path =>
            path.StartsWith("lib/", StringComparison.Ordinal));

        Snapshot.ToolCommand.Should().Be(("scri", "scri.dll", "dotnet"));
    }

    [Fact]
    public void Package_ContainsManagedDependencies()
    {
        string[] expectedDependencies =
        [
            "Scrinia.Core.dll",
            "Scrinia.Mcp.dll",
            "Scrinia.Plugin.Abstractions.dll",
            "Spectre.Console.dll",
            "Microsoft.Extensions.Hosting.dll",
            "ModelContextProtocol.dll",
        ];

        foreach (string dependency in expectedDependencies)
        {
            Snapshot.EntryPaths.Should().Contain(path =>
                path.Split('/').Last().Equals(dependency, StringComparison.OrdinalIgnoreCase),
                because: $"{dependency} is part of the packaged command closure");
        }
    }

    [Fact]
    public void Package_IncludesCompleteOfflineResourceSet()
    {
        string[] expectedResources =
        [
            "Scrinia.Mcp.plugins.opencode.scrinia.ts",
            "Scrinia.Mcp.prompts.guide.md",
            "Scrinia.Mcp.prompts.scrinia-memory-skill.md",
            "Scrinia.Mcp.prompts.scaffolds.domain-expert.md",
            "Scrinia.Mcp.prompts.scaffolds.researcher.md",
            "Scrinia.Mcp.prompts.scaffolds.reviewer.md",
            "Scrinia.Mcp.skills.auditor.md",
            "Scrinia.Mcp.skills.chaos-engineer.md",
            "Scrinia.Mcp.skills.debugger.md",
            "Scrinia.Mcp.skills.evolutionary.md",
            "Scrinia.Mcp.skills.onboarder.md",
            "Scrinia.Mcp.skills.qa.md",
            "Scrinia.Mcp.skills.self-reflector.md",
        ];

        Snapshot.McpEmbeddedResources.Should().BeEquivalentTo(expectedResources);
        Snapshot.McpEmbeddedResourceContents.Values.Should().OnlyContain(content => !string.IsNullOrWhiteSpace(content));
    }

    [Fact]
    public void Package_IncludesPackageReadme_AtRoot()
    {
        Snapshot.EntryPaths.Should().Contain("package-readme.md");
    }

    [Fact]
    public void Package_PacksHaveEquivalentContractContent()
    {
        PackageSnapshot other = Packs.Value.Second;

        PackagePathNormalizer.Normalize(@"tools\net10.0\any\scri.dll")
            .Should().Be("tools/net10.0/any/scri.dll");
        IReadOnlyDictionary<string, PackageEntryFingerprint> firstEntries = PackageContractEntries.Normalize(Snapshot.EntryFingerprints);
        IReadOnlyDictionary<string, PackageEntryFingerprint> secondEntries = PackageContractEntries.Normalize(other.EntryFingerprints);
        firstEntries.Keys.Should().BeEquivalentTo(secondEntries.Keys);
        foreach (string path in firstEntries.Keys)
        {
            firstEntries[path].Should().BeEquivalentTo(secondEntries[path],
                because: $"contract entry {path} must be identical across fresh packs");
        }

        Snapshot.Metadata.Should().BeEquivalentTo(other.Metadata);
        Snapshot.ToolCommand.Should().Be(other.ToolCommand);
        Snapshot.McpEmbeddedResources.Should().BeEquivalentTo(other.McpEmbeddedResources);
        Snapshot.McpEmbeddedResourceContents.Should().BeEquivalentTo(other.McpEmbeddedResourceContents);
    }

    [Fact]
    public void Package_ExcludesMergeAndScriMerge()
    {
        AssertNoPathContaining("scrinia.merge");
        AssertNoPathContaining("scri-merge");
        AssertNoPathContaining("merge.dll");
    }

    [Fact]
    public void Package_ExcludesVulkanAndLLamaSharp()
    {
        AssertNoPathContaining("vulkan");
        AssertNoPathContaining("llamasharp");
    }

    [Fact]
    public void Package_ExcludesNativeRidAssets()
    {
        foreach (string normalized in NormalizedPaths())
        {
            normalized.Should().NotMatchRegex(@"(?:^|/)(?:[^/]+\.(?:so|dylib|a|lib))(?:$|/)");
            normalized.Should().NotMatchRegex(@"(?:^|/)runtimes/[^/]+/native(?:/|$)");
        }
    }

    [Fact]
    public void Package_ExcludesDownloadedModels()
    {
        AssertNoPathSegment("models");
        AssertNoPathSegment("model");
        AssertNoPathContaining(".gguf");
        AssertNoPathContaining(".safetensors");
    }

    [Fact]
    public void Package_ExcludesRepositoryAndWorkspaceData()
    {
        AssertNoPathSegment(".git");
        AssertNoPathSegment(".scrinia");
        AssertNoPathSegment("repository");
        AssertNoPathSegment("repositories");
    }

    [Fact]
    public void Package_ExcludesMutableCaches()
    {
        foreach (string segment in new[] { "embeddings", "indexes", "exports", "versions", "cache", "caches" })
        {
            AssertNoPathSegment(segment);
        }

        foreach (string normalized in NormalizedPaths())
        {
            normalized.Should().NotMatchRegex(@"(?:^|/)[^/]+\.(?:tmp|lock)(?:$|/)");
        }
    }

    private static IEnumerable<string> NormalizedPaths() =>
        Snapshot.EntryPaths.Select(PackagePathNormalizer.Normalize);

    private static void AssertNoPathContaining(string prohibitedText)
    {
        NormalizedPaths().Should().NotContain(path => path.Contains(prohibitedText, StringComparison.Ordinal),
            because: $"the package must not contain {prohibitedText}");
    }

    private static void AssertNoPathSegment(string prohibitedSegment)
    {
        NormalizedPaths().Should().NotContain(path =>
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Contains(prohibitedSegment, StringComparer.Ordinal),
            because: $"the package must not contain the {prohibitedSegment} path segment");
    }
}

internal static class PackagePathNormalizer
{
    public static string Normalize(string path) =>
        path.Replace('\\', '/').Trim('/').ToLowerInvariant();
}

internal sealed class PackagePair
{
    public required PackageSnapshot First { get; init; }

    public required PackageSnapshot Second { get; init; }

    public static PackagePair Build()
    {
        return new PackagePair
        {
            First = PackageSnapshot.Build(),
            Second = PackageSnapshot.Build(),
        };
    }
}

internal sealed class PackageSnapshot
{
    public required PackageMetadata Metadata { get; init; }

    public required IReadOnlyDictionary<string, PackageEntryFingerprint> EntryFingerprints { get; init; }

    public required IReadOnlyList<string> EntryPaths { get; init; }

    public required IReadOnlyList<string> McpEmbeddedResources { get; init; }

    public required IReadOnlyDictionary<string, string> McpEmbeddedResourceContents { get; init; }

    public required (string Name, string EntryPoint, string Runner) ToolCommand { get; init; }

    public static PackageSnapshot Build()
    {
        string? dotnet = new ProcessRunner().ResolveExecutable("dotnet");
        dotnet.Should().NotBeNullOrEmpty("the dotnet SDK is required to pack the global tool package");

        string csproj = RepoPaths.Combine("src", "Scrinia", "Scrinia.csproj");
        string packOut = Path.Combine(Path.GetTempPath(), $"scrinia-pack-{Guid.NewGuid():N}");
        List<string> temporaryFiles = [];
        Directory.CreateDirectory(packOut);

        try
        {
            ProcessResult result = new ProcessRunner().RunAsync(
                dotnet!, ["pack", csproj, "-c", "Release", "-o", packOut, "--disable-build-servers", "--no-restore"], null,
                CancellationToken.None).GetAwaiter().GetResult();
            result.ExitCode.Should().Be(0, because:
                $"dotnet pack exited {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");

            string[] nupkgs = Directory.GetFiles(packOut, "*.nupkg");
            nupkgs.Should().ContainSingle("dotnet pack must produce exactly one .nupkg");
            return ReadArtifact(nupkgs[0], temporaryFiles);
        }
        finally
        {
            CleanupPackageArtifacts(packOut, temporaryFiles);
        }
    }

    private static PackageSnapshot ReadArtifact(string nupkg, List<string> temporaryFiles)
    {
        Dictionary<string, PackageEntryFingerprint> fingerprints = new(StringComparer.Ordinal);
        string? mcpDllPath = null;
        string nuspecText;
        string toolSettingsText;

        using (ZipArchive zip = ZipFile.OpenRead(nupkg))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                byte[] content;
                using (Stream stream = entry.Open())
                using (MemoryStream buffer = new())
                {
                    stream.CopyTo(buffer);
                    content = buffer.ToArray();
                }

                fingerprints.Add(PackagePathNormalizer.Normalize(entry.FullName),
                    PackageEntryFingerprint.Create(content));
                if (entry.FullName.EndsWith("/Scrinia.Mcp.dll", StringComparison.Ordinal))
                {
                    mcpDllPath = Path.Combine(Path.GetTempPath(), $"scrinia-mcp-{Guid.NewGuid():N}.dll");
                    temporaryFiles.Add(mcpDllPath);
                    File.WriteAllBytes(mcpDllPath, content);
                }
            }

            ZipArchiveEntry nuspec = zip.Entries.First(entry =>
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            nuspecText = ReadText(nuspec);

            ZipArchiveEntry settings = zip.Entries.First(entry =>
                entry.FullName.EndsWith("DotnetToolSettings.xml", StringComparison.Ordinal));
            toolSettingsText = ReadText(settings);
        }

        mcpDllPath.Should().NotBeNull("the MCP assembly is part of the command closure");
        Assembly mcpAssembly = Assembly.Load(File.ReadAllBytes(mcpDllPath!));
        string[] resourceNames = mcpAssembly.GetManifestResourceNames()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, string> resourceContents = resourceNames.ToDictionary(
            name => name,
            name => ReadManifestResource(mcpAssembly, name),
            StringComparer.Ordinal);

        XDocument nuspecDocument = XDocument.Parse(nuspecText, LoadOptions.PreserveWhitespace);
        PackageMetadata metadata = PackageMetadata.Parse(nuspecDocument);
        (string Name, string EntryPoint, string Runner) toolCommand = ParseToolCommand(toolSettingsText);

        return new PackageSnapshot
        {
            Metadata = metadata,
            EntryFingerprints = fingerprints,
            EntryPaths = fingerprints.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            McpEmbeddedResources = resourceNames,
            McpEmbeddedResourceContents = resourceContents,
            ToolCommand = toolCommand,
        };
    }

    private static void CleanupPackageArtifacts(string packOut, IEnumerable<string> temporaryFiles)
    {
        List<Exception> failures = [];
        foreach (string path in temporaryFiles.Append(packOut))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    continue;
                }

                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                failures.Add(new IOException($"Failed to remove package test artifact '{path}'.", ex));
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("Package snapshot cleanup was incomplete; temporary package artifacts remain.", failures);
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ReadManifestResource(Assembly assembly, string resourceName)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static (string Name, string EntryPoint, string Runner) ParseToolCommand(string xml)
    {
        XElement command = XDocument.Parse(xml).Descendants("Command").Single();
        return (
            (string?)command.Attribute("Name") ?? string.Empty,
            (string?)command.Attribute("EntryPoint") ?? string.Empty,
            (string?)command.Attribute("Runner") ?? string.Empty);
    }
}

internal sealed record PackageEntryFingerprint(long Size, string Sha256, byte[] Content)
{
    public static PackageEntryFingerprint Create(byte[] content) =>
        new(content.LongLength, Convert.ToHexString(SHA256.HashData(content)), content);
}

internal static class PackageContractEntries
{
    private const string CorePrefix = "package/services/metadata/core-properties/";
    private const string CanonicalCorePath = CorePrefix + "contract.psmdcp";
    private const string CoreRelation = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    private const string ManifestRelation = "http://schemas.microsoft.com/packaging/2010/07/manifest";

    public static IReadOnlyDictionary<string, PackageEntryFingerprint> Normalize(
        IReadOnlyDictionary<string, PackageEntryFingerprint> entries)
    {
        string[] corePaths = entries.Keys.Where(path => path.EndsWith(".psmdcp", StringComparison.Ordinal)).ToArray();
        if (corePaths.Length != 1 || !System.Text.RegularExpressions.Regex.IsMatch(corePaths[0],
            "^package/services/metadata/core-properties/[0-9a-f]{32}\\.psmdcp$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Expected exactly one GUID-named OPC core-properties part.");
        if (!entries.TryGetValue("_rels/.rels", out PackageEntryFingerprint? relationshipEntry))
            throw new InvalidDataException("The OPC root relationships part is missing.");

        string relationships;
        XDocument document;
        try
        {
            relationships = new UTF8Encoding(false, true).GetString(relationshipEntry.Content);
            using var reader = System.Xml.XmlReader.Create(new StringReader(relationships), new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or DecoderFallbackException)
        {
            throw new InvalidDataException("The OPC root relationships part is malformed.", exception);
        }
        XNamespace relationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
        XElement? root = document.Root;
        XElement[] children = root?.Elements().ToArray() ?? [];
        if (root?.Name != relationshipNamespace + "Relationships" || children.Length != 2 ||
            children.Any(element => element.Name != relationshipNamespace + "Relationship"))
            throw new InvalidDataException("Expected only the manifest and core-properties OPC relationships.");
        XElement[] coreRelationships = children.Where(element => (string?)element.Attribute("Type") == CoreRelation).ToArray();
        XElement[] manifestRelationships = children.Where(element => (string?)element.Attribute("Type") == ManifestRelation).ToArray();
        if (coreRelationships.Length != 1 || manifestRelationships.Length != 1 || !entries.ContainsKey("scrinia.nuspec"))
            throw new InvalidDataException("The OPC relationships must identify one existing manifest and one core-properties part.");
        string coreTarget = '/' + corePaths[0];
        ValidateRelationship(coreRelationships[0], coreTarget);
        ValidateRelationship(manifestRelationships[0], "/scrinia.nuspec");

        // Preserve every other byte, including XML whitespace/declaration and core-properties content.
        // Only the validated SDK-generated target and its path-derived Id vary across fresh packs.
        relationships = ReplaceOneAttribute(relationships, "Target", coreTarget, '/' + CanonicalCorePath);
        relationships = ReplaceOneAttribute(relationships, "Id", RelationshipId(coreTarget), RelationshipId('/' + CanonicalCorePath));
        Dictionary<string, PackageEntryFingerprint> normalized = new(entries, StringComparer.Ordinal);
        normalized.Remove(corePaths[0]);
        normalized.Add(CanonicalCorePath, entries[corePaths[0]]);
        normalized["_rels/.rels"] = PackageEntryFingerprint.Create(Encoding.UTF8.GetBytes(relationships));
        return normalized;
    }

    private static void ValidateRelationship(XElement relationship, string target)
    {
        if (relationship.Attributes().Count() != 3 || relationship.HasElements ||
            (string?)relationship.Attribute("Target") != target ||
            (string?)relationship.Attribute("Id") != RelationshipId(target))
            throw new InvalidDataException("An OPC relationship has an external, wrong or dangling target, or a mismatched derived Id.");
    }

    private static string ReplaceOneAttribute(string xml, string name, string original, string replacement)
    {
        string token = name + "=\"" + original + "\"";
        int index = xml.IndexOf(token, StringComparison.Ordinal);
        if (index < 0 || index != xml.LastIndexOf(token, StringComparison.Ordinal))
            throw new InvalidDataException("The validated generated OPC attribute must have one unambiguous encoded occurrence.");
        return xml[..index] + name + "=\"" + replacement + "\"" + xml[(index + token.Length)..];
    }

    private static string RelationshipId(string target) =>
        "R" + Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(target)))[..16];
}

public sealed class PackageContractComparisonTests
{
    private const string CorePrefix = "package/services/metadata/core-properties/";
    private const string FirstGuid = "0123456789abcdef0123456789abcdef";
    private const string SecondGuid = "fedcba9876543210fedcba9876543210";
    private const string CoreRelation = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";

    [Fact]
    public void OnlyValidatedGeneratedOpcIdentityVariesAcrossEquivalentContracts()
    {
        Dictionary<string, PackageEntryFingerprint> first = Fixture(FirstGuid);
        Dictionary<string, PackageEntryFingerprint> second = Fixture(SecondGuid);
        PackageContractEntries.Normalize(first).Should().BeEquivalentTo(PackageContractEntries.Normalize(second));
        first.Keys.Should().Contain(CorePrefix + FirstGuid + ".psmdcp", "raw artifact identity must remain intact");
        second.Keys.Should().Contain(CorePrefix + SecondGuid + ".psmdcp");
    }

    [Theory]
    [InlineData("malformed-part")]
    [InlineData("duplicate-part")]
    [InlineData("dangling-core-target")]
    [InlineData("wrong-core-id")]
    [InlineData("duplicate-core-relationship")]
    [InlineData("wrong-manifest-target")]
    [InlineData("external-target")]
    [InlineData("malformed-xml")]
    public void InvalidOpcMetadataCannotBeCanonicalized(string corruption)
    {
        Dictionary<string, PackageEntryFingerprint> entries = Fixture(FirstGuid);
        string corePath = CorePrefix + FirstGuid + ".psmdcp";
        string relationships = Encoding.UTF8.GetString(entries["_rels/.rels"].Content);
        switch (corruption)
        {
            case "malformed-part":
                entries.Add(CorePrefix + "not-a-guid.psmdcp", entries[corePath]);
                entries.Remove(corePath);
                break;
            case "duplicate-part": entries.Add(CorePrefix + SecondGuid + ".psmdcp", entries[corePath]); break;
            case "dangling-core-target": relationships = relationships.Replace(FirstGuid, SecondGuid, StringComparison.Ordinal); break;
            case "wrong-core-id": relationships = relationships.Replace(RelationshipId('/' + corePath), "R0000000000000000", StringComparison.Ordinal); break;
            case "duplicate-core-relationship":
                XElement relationship = XDocument.Parse(relationships).Root!.Elements().Single(element => (string?)element.Attribute("Type") == CoreRelation);
                relationships = relationships.Replace("</Relationships>", relationship + "</Relationships>", StringComparison.Ordinal);
                break;
            case "wrong-manifest-target": relationships = relationships.Replace("/scrinia.nuspec", "/missing.nuspec", StringComparison.Ordinal); break;
            case "external-target": relationships = relationships.Replace("Type=\"" + CoreRelation + "\"", "TargetMode=\"External\" Type=\"" + CoreRelation + "\"", StringComparison.Ordinal); break;
            case "malformed-xml": relationships = "<broken>"; break;
            default: throw new InvalidOperationException(corruption);
        }
        entries["_rels/.rels"] = PackageEntryFingerprint.Create(Encoding.UTF8.GetBytes(relationships));
        Action normalize = () => PackageContractEntries.Normalize(entries);
        normalize.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadAndCorePropertiesContentChangesRemainDetectable(bool coreProperties)
    {
        Dictionary<string, PackageEntryFingerprint> original = Fixture(FirstGuid);
        Dictionary<string, PackageEntryFingerprint> changed = Fixture(FirstGuid);
        string path = coreProperties ? CorePrefix + FirstGuid + ".psmdcp" : "tools/net10.0/any/scri.dll";
        changed[path] = PackageEntryFingerprint.Create(Encoding.UTF8.GetBytes("changed contract content"));
        PackageEntryFingerprint before = PackageContractEntries.Normalize(original).Single(pair =>
            coreProperties ? pair.Key.EndsWith(".psmdcp", StringComparison.Ordinal) : pair.Key == path).Value;
        PackageEntryFingerprint after = PackageContractEntries.Normalize(changed).Single(pair =>
            coreProperties ? pair.Key.EndsWith(".psmdcp", StringComparison.Ordinal) : pair.Key == path).Value;
        before.Sha256.Should().NotBe(after.Sha256);
        before.Content.Should().NotEqual(after.Content);
    }

    private static Dictionary<string, PackageEntryFingerprint> Fixture(string guid)
    {
        string corePath = CorePrefix + guid + ".psmdcp";
        string relationships = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Type=\"http://schemas.microsoft.com/packaging/2010/07/manifest\" Target=\"/scrinia.nuspec\" Id=\"{RelationshipId("/scrinia.nuspec")}\" /><Relationship Type=\"{CoreRelation}\" Target=\"/{corePath}\" Id=\"{RelationshipId('/' + corePath)}\" /></Relationships>";
        return new(StringComparer.Ordinal)
        {
            [corePath] = PackageEntryFingerprint.Create(Encoding.UTF8.GetBytes("<coreProperties>same metadata</coreProperties>")),
            ["_rels/.rels"] = PackageEntryFingerprint.Create(Encoding.UTF8.GetBytes(relationships)),
            ["scrinia.nuspec"] = PackageEntryFingerprint.Create(Encoding.UTF8.GetBytes("<package>same nuspec</package>")),
            ["tools/net10.0/any/scri.dll"] = PackageEntryFingerprint.Create(Encoding.UTF8.GetBytes("same managed payload")),
        };
    }

    private static string RelationshipId(string target) =>
        "R" + Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(target)))[..16];
}

internal sealed record PackageMetadata(
    string Id,
    string Version,
    string Authors,
    string LicenseType,
    string LicenseValue,
    string Readme,
    string Description,
    string Tags,
    string PackageType)
{
    public static PackageMetadata Parse(XDocument document)
    {
        XNamespace ns = "http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd";
        XElement metadata = document.Root?.Element(ns + "metadata")
            ?? throw new InvalidDataException("The package nuspec has no metadata element.");

        string Required(string name) => metadata.Element(ns + name)?.Value
            ?? throw new InvalidDataException($"The package nuspec has no {name} element.");

        string packageType = metadata.Element(ns + "packageTypes")?.Element(ns + "packageType")?
            .Attribute("name")?.Value
            ?? throw new InvalidDataException("The package nuspec has no package type.");

        return new PackageMetadata(
            Required("id"), Required("version"), Required("authors"),
            metadata.Element(ns + "license")?.Attribute("type")?.Value
                ?? throw new InvalidDataException("The package nuspec has no license type."),
            Required("license"),
            Required("readme"), Required("description"), Required("tags"), packageType);
    }
}
