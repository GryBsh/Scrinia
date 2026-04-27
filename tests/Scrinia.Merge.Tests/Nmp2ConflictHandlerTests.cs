using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class Nmp2ConflictHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MergeConfig _config = new();

    public Nmp2ConflictHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-merge-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteFile(string fileName, string content)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteFilePair(string baseName, string nmp2Content, string metaContent)
    {
        string nmp2Path = Path.Combine(_tempDir, baseName + ".nmp2");
        string metaPath = Path.Combine(_tempDir, baseName + ".meta.json");
        File.WriteAllText(nmp2Path, nmp2Content);
        File.WriteAllText(metaPath, metaContent);
        return nmp2Path;
    }

    [Fact]
    public void Handle_OursUnchanged_TakesTheirs()
    {
        // ancestor == ours → copy theirs
        string ancestorPath = WriteFile("ancestor.nmp2", "original content");
        string oursPath = WriteFile("ours.nmp2", "original content");
        string theirsPath = WriteFile("theirs.nmp2", "updated by theirs");

        int result = Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, _config);

        result.Should().Be(0);
        File.ReadAllText(oursPath).Should().Be("updated by theirs",
            because: "ours was unchanged from ancestor, so theirs should win");
    }

    [Fact]
    public void Handle_TheirsUnchanged_KeepsOurs()
    {
        // ancestor == theirs → keep ours
        string ancestorPath = WriteFile("ancestor.nmp2", "original content");
        string oursPath = WriteFile("ours.nmp2", "updated by ours");
        string theirsPath = WriteFile("theirs.nmp2", "original content");

        int result = Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, _config);

        result.Should().Be(0);
        File.ReadAllText(oursPath).Should().Be("updated by ours",
            because: "theirs was unchanged from ancestor, so ours should remain");
    }

    [Fact]
    public void Handle_BothChanged_CreatesConflictDirs()
    {
        // All three differ → conflict-as-data
        string ancestorPath = WriteFile("ancestor.nmp2", "original");
        string oursPath = WriteFile("ours.nmp2", "changed by ours");
        string theirsPath = WriteFile("theirs.nmp2", "changed by theirs");

        // Also create a meta.json sidecar for ours so MarkMetaConflicted works
        File.WriteAllText(Path.ChangeExtension(oursPath, ".meta.json"),
            "{\n  \"name\": \"test\"\n}");

        int result = Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, _config);

        result.Should().Be(0, because: "conflict is tracked as data, not a git failure");

        string conflictDir = Path.Combine(_tempDir, _config.ConflictDir);
        string currentDir = Path.Combine(conflictDir, "current");
        string incomingDir = Path.Combine(conflictDir, "incoming");

        Directory.Exists(currentDir).Should().BeTrue();
        Directory.Exists(incomingDir).Should().BeTrue();

        File.ReadAllText(Path.Combine(currentDir, "ours.nmp2"))
            .Should().Be("changed by ours");
        File.ReadAllText(Path.Combine(incomingDir, "ours.nmp2"))
            .Should().Be("changed by theirs");
    }

    [Fact]
    public void Handle_BothChanged_MarksMetaConflicted()
    {
        string ancestorPath = WriteFile("ancestor.nmp2", "original");
        string oursPath = WriteFile("ours.nmp2", "changed by ours");
        string theirsPath = WriteFile("theirs.nmp2", "changed by theirs");

        // Create meta.json sidecar
        string metaPath = Path.ChangeExtension(oursPath, ".meta.json");
        File.WriteAllText(metaPath, "{\n  \"name\": \"test\"\n}");

        Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, _config);

        string metaContent = File.ReadAllText(metaPath);
        metaContent.Should().Contain("\"conflicted\": true",
            because: "the meta.json should be marked as conflicted when both sides changed");
    }

    [Fact]
    public void Handle_OursUnchanged_CopiesSidecar()
    {
        // When ours is unchanged and theirs has a sidecar, the sidecar should be copied too
        string ancestorPath = WriteFile("ancestor.nmp2", "original content");
        string oursPath = WriteFile("ours.nmp2", "original content");
        string theirsPath = WriteFile("theirs.nmp2", "updated by theirs");

        // Create sidecar for theirs
        string theirsMeta = Path.ChangeExtension(theirsPath, ".meta.json");
        File.WriteAllText(theirsMeta, "{\n  \"name\": \"theirs-version\"\n}");

        Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, _config);

        string oursMeta = Path.ChangeExtension(oursPath, ".meta.json");
        File.Exists(oursMeta).Should().BeTrue();
        File.ReadAllText(oursMeta).Should().Contain("theirs-version",
            because: "the sidecar from theirs should be copied alongside the nmp2");
    }
}
