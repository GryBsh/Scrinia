using System.Xml.Linq;
using FluentAssertions;

namespace Scrinia.Tests.Shared;

public sealed class McpCoreReferenceTests
{
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

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath));
}
