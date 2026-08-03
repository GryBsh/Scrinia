using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for the CLI <c>scri bundle</c> command (<see cref="ScriniaCommands.Bundle"/>),
/// which writes straight to <c>.scrinia/exports/</c> via <see cref="Directory.CreateDirectory"/>
/// without ever going through <see cref="Scrinia.Core.FileMemoryStore.Upsert"/> or
/// <see cref="Scrinia.Core.FileMemoryStore.SaveIndex"/>. It can be the very first scrinia-related
/// write in a fresh repo (bundling raw files needs no pre-existing memory store), so it must
/// materialize <c>.gitignore</c> itself rather than relying on some earlier write having done so.
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class BundleGitIgnoreTests : IDisposable
{
    private readonly string _tempDir;

    public BundleGitIgnoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_bundle_gitignore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Bundle_FirstActionInFreshWorkspace_ProducesGitIgnore()
    {
        string sourceFile = Path.Combine(_tempDir, "notes.md");
        await File.WriteAllTextAsync(sourceFile, "# Notes\nSome raw file content.");

        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Bundle(topic: "notes", files: sourceFile, workspaceRoot: _tempDir);

        exitCode.Should().Be(0);
        string gitignorePath = Path.Combine(_tempDir, ".scrinia", ".gitignore");
        File.Exists(gitignorePath).Should().BeTrue(
            because: "bundling raw files can be a workspace's first scrinia write and must still " +
                     "materialize .gitignore even though it never reaches Upsert/SaveIndex");
    }
}
