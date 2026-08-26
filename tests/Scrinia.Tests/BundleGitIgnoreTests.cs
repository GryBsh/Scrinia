using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Mcp;

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

    /// <summary>
    /// Regression coverage for the MCP <c>bundle('export', ...)</c> tool
    /// (<see cref="Scrinia.Mcp.ScriniaMcpTools.Export"/>), which creates
    /// <c>.scrinia/exports/</c> via <see cref="Directory.CreateDirectory"/> directly. This is
    /// the exact recovery path the orphan-store warning prints (<c>scri bundle export</c>), so on
    /// a workspace with no prior scrinia activity it must materialize <c>.gitignore</c> itself
    /// rather than relying on some earlier write (such as <see cref="Scrinia.Core.FileMemoryStore.Upsert"/>,
    /// which has its own <c>EnsureGitIgnore</c> call) having done so first. The fixture deliberately
    /// seeds no store data — Export must create the exports directory (and thus the .gitignore)
    /// before it ever checks whether the requested topic has entries.
    /// </summary>
    [Fact]
    public async Task Export_FirstActionInFreshWorkspace_ProducesGitIgnore()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);

        string yaml = (await new ScriniaMcpTools().Export(topics: ["notes"], cancellationToken: default)).ToYaml();

        yaml.Should().Contain("error:", because: "no topic data was seeded, so the export itself is " +
            "expected to fail — the fixture proves .gitignore appears even on that failing path");

        string gitignorePath = Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitignore");
        File.Exists(gitignorePath).Should().BeTrue(
            because: "bundle export can be a workspace's first scrinia write (it's the orphan-store " +
                     "recovery command) and must still materialize .gitignore before creating exports/");
    }

    [Fact]
    public void DefaultGitIgnore_ExcludesRestoreJournals()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);

        Scrinia.Core.FileMemoryStore.EnsureGitIgnore(scope.WorkspaceDir);

        string contents = File.ReadAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitignore"));
        contents.Should().Contain(".restore-journal.json");
    }
}
