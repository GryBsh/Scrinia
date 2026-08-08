using System.Reflection;
using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Pins the store boundary: <see cref="IMemoryStore"/> is the contract both stores can honour,
/// so a remote store must be able to implement all of it. The failure mode this catches is
/// someone adding a local-only member back to the shared interface — which nothing else does.
/// </summary>
public sealed class StoreBoundaryTests
{
    [Fact]
    public void HttpMemoryStore_ThrowsNotSupported_ForExactlyOneReason()
    {
        // The one legitimate NotSupportedException in remote mode is the file:// rejection
        // inside ResolveArtifactAsync — a rejection of an input value, not of a capability.
        var sourcePath = Path.Combine(RepoRoot(), "src", "Scrinia", "HttpMemoryStore.cs");
        var throwLines = File.ReadAllLines(sourcePath)
            .Select((line, i) => (Line: line, Number: i + 1))
            .Where(x => x.Line.Contains("throw new NotSupportedException"))
            .ToList();

        throwLines.Should().ContainSingle(
            because: "every other NotSupportedException marked a member that belongs on " +
                     "ILocalMemoryStore, not on the interface a remote store implements");
        throwLines[0].Line.Should().Contain("file://");
    }

    [Fact]
    public void IMemoryStore_DoesNotDeclareLocalOnlyMembers()
    {
        var names = typeof(IMemoryStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        names.Should().NotContain("WorkspaceRoot");
        names.Should().NotContain("ArtifactPath");
        names.Should().NotContain("FindArtifactPath");
        names.Should().NotContain("GetStoreDirForScope");
        names.Should().NotContain("ListTopicArtifacts");
        names.Should().NotContain("ImportTopicEntries");
        names.Should().NotContain("SaveIndex");
        names.Should().NotContain("GenerateContentPreview");

        names.Should().Contain("ArtifactUri",
            because: "HttpMemoryStore implements it for real, returning a remote:// URI");
    }

    private static string RepoRoot()
    {
        // Same walk-up as tests/Scrinia.Merge.Tests/GitMergeHarness.cs:33-38. Copy its
        // failure message too — it names the directory it started from, which is the only
        // thing worth knowing when this fails. That project is outside this branch's test
        // scope, so the helper is duplicated rather than referenced.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrinia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not find Scrinia.sln walking up from {AppContext.BaseDirectory}.");
    }
}
