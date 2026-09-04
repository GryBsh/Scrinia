using System.Xml.Linq;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

public sealed class McpCoreReferenceTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "scrinia-runtime-identity", Guid.NewGuid().ToString("N"));
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void Mcp_project_references_Core_and_not_executable()
    {
        XDocument project = LoadProject("src/Scrinia.Mcp/Scrinia.Mcp.csproj");
        string[] references = project.Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? "")
            .ToArray();

        references.Should().Contain(reference => reference.Contains("Scrinia.Core", StringComparison.Ordinal));
        references.Should().NotContain(reference => reference.Contains("Scrinia\\Scrinia.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_has_no_reference_to_executable()
    {
        XDocument project = LoadProject("src/Scrinia.Core/Scrinia.Core.csproj");
        project.Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? "")
            .Should().NotContain(reference => reference.Contains("Scrinia\\Scrinia.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void Executable_shared_host_contains_no_shared_algorithms()
    {
        string adapterPath = Path.Combine(RepositoryRoot, "src", "Scrinia", "Commands", "SharedServeHostAdapter.cs");

        File.Exists(adapterPath).Should().BeTrue();
        string source = File.ReadAllText(adapterPath);
        source.Should().Contain("ISharedBaseRuntimeFactory");
        source.Should().NotContain("new SharedBaseRegistry");
        source.Should().NotContain("SharedClassifier.");
        source.Should().NotContain("Fingerprint(");
        source.Should().NotContain("Yaml(");
        source.Should().NotContain("McpResponse.");
    }

    [Fact]
    public void Equivalent_physical_roots_through_a_symbolic_alias_share_one_runtime()
    {
        string target = CreateDirectory("target");
        string alias = Path.Combine(_temporaryRoot, "alias");
        CreateDirectoryAlias(alias, target);
        var factory = new SharedBaseRuntimeFactory();

        SharedBaseRuntime first = factory.Open(CreateEntry("team", target));
        SharedBaseRuntime second = factory.Open(CreateEntry("team", alias));

        second.Should().BeSameAs(first);
        first.RegisteredRoot.Should().Be(PathContainment.ResolveCanonical(target));
    }

    [Fact]
    public void Different_base_names_remain_distinct_for_the_same_physical_root()
    {
        string target = CreateDirectory("same-root");
        var factory = new SharedBaseRuntimeFactory();

        SharedBaseRuntime first = factory.Open(CreateEntry("team", target));
        SharedBaseRuntime second = factory.Open(CreateEntry("archive", target));

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void Different_physical_roots_remain_distinct()
    {
        var factory = new SharedBaseRuntimeFactory();

        SharedBaseRuntime first = factory.Open(CreateEntry("team", CreateDirectory("first")));
        SharedBaseRuntime second = factory.Open(CreateEntry("team", CreateDirectory("second")));

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void An_alias_resolving_to_a_store_directory_fails_closed_before_keying()
    {
        string target = CreateDirectory("unsafe-target");
        string store = Directory.CreateDirectory(Path.Combine(target, ".scrinia")).FullName;
        string alias = Path.Combine(_temporaryRoot, "unsafe-alias");
        CreateDirectoryAlias(alias, store);

        Action open = () => new SharedBaseRuntimeFactory().Open(CreateEntry("team", alias));

        open.Should().Throw<ArgumentException>();
    }

    private string CreateDirectory(string name) => Directory.CreateDirectory(Path.Combine(_temporaryRoot, name)).FullName;

    private static SharedBaseEntry CreateEntry(string name, string root) => new() { Name = name, Root = root };

    private void CreateDirectoryAlias(string alias, string target)
    {
        Directory.CreateDirectory(_temporaryRoot);
        try
        {
            Directory.CreateSymbolicLink(alias, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip.If(true, $"The test environment cannot create directory aliases: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath));
}
