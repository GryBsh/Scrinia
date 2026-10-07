using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

public class WorkspaceConfigTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope = new();

    public void Dispose()
    {
        _scope.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_NoFile_ReturnsEmptyDict()
    {
        var config = WorkspaceConfig.Load(_scope.WorkspaceDir);
        config.Should().BeEmpty();
    }

    [Fact]
    public void SetValue_CreatesFileAndSetsKey()
    {
        WorkspaceConfig.SetValue(_scope.WorkspaceDir, "plugins:embeddings", "my-plugin");

        string path = Path.Combine(_scope.WorkspaceDir, ".scrinia", "config.json");
        File.Exists(path).Should().BeTrue();

        string json = File.ReadAllText(path);
        json.Should().Contain("plugins:embeddings");
        json.Should().Contain("my-plugin");
    }

    [Fact]
    public void GetValue_ReturnsSetValue()
    {
        WorkspaceConfig.SetValue(_scope.WorkspaceDir, "plugins:embeddings", "my-plugin");

        string? value = WorkspaceConfig.GetValue(_scope.WorkspaceDir, "plugins:embeddings");
        value.Should().Be("my-plugin");
    }

    [Fact]
    public void GetValue_MissingKey_ReturnsNull()
    {
        string? value = WorkspaceConfig.GetValue(_scope.WorkspaceDir, "nonexistent");
        value.Should().BeNull();
    }

    [Fact]
    public void UnsetValue_RemovesKey()
    {
        WorkspaceConfig.SetValue(_scope.WorkspaceDir, "plugins:embeddings", "my-plugin");

        bool removed = WorkspaceConfig.UnsetValue(_scope.WorkspaceDir, "plugins:embeddings");
        removed.Should().BeTrue();

        WorkspaceConfig.GetValue(_scope.WorkspaceDir, "plugins:embeddings").Should().BeNull();
    }

    [Fact]
    public void UnsetValue_MissingKey_ReturnsFalse()
    {
        bool removed = WorkspaceConfig.UnsetValue(_scope.WorkspaceDir, "nonexistent");
        removed.Should().BeFalse();
    }

    [Fact]
    public void Save_Load_Roundtrips()
    {
        var original = new Dictionary<string, string>
        {
            ["plugins:embeddings"] = "scri-plugin-embeddings",
            ["Scrinia:Embeddings:Provider"] = "onnx",
            ["Scrinia:Embeddings:Hardware"] = "directml",
        };

        WorkspaceConfig.Save(_scope.WorkspaceDir, original);
        var loaded = WorkspaceConfig.Load(_scope.WorkspaceDir);

        loaded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Load_IsCaseInsensitive()
    {
        WorkspaceConfig.SetValue(_scope.WorkspaceDir, "Plugins:Embeddings", "my-plugin");

        string? value = WorkspaceConfig.GetValue(_scope.WorkspaceDir, "plugins:embeddings");
        value.Should().Be("my-plugin");
    }

    // `scri hint` runs on EVERY user prompt, and each GetValue used to re-open, re-read and
    // re-deserialize config.json -- 10 times per hint, 20+ per search, on a file that is 91
    // bytes in this workspace. The file is read once per process now.
    [Fact]
    public void GetValue_ReadsTheFileOncePerProcess()
    {
        WorkspaceConfig.SetValue(_scope.WorkspaceDir, "a", "1");
        WorkspaceConfig.GetValue(_scope.WorkspaceDir, "a").Should().Be("1");

        // Rewrite the file behind the cache. A re-reading implementation would see "2".
        string path = Path.Combine(_scope.WorkspaceDir, ".scrinia", "config.json");
        File.WriteAllText(path, """{"a":"2"}""");

        WorkspaceConfig.GetValue(_scope.WorkspaceDir, "a").Should().Be("1",
            because: "the cache is per-process and only a write through this class invalidates it");
    }

    // The staleness window above is only acceptable because a mutation through this class
    // clears it. Without this, `scri config set` followed by a read in the same process --
    // exactly what SetValue/UnsetValue do internally -- would return the pre-write value.
    [Fact]
    public void SetValue_And_UnsetValue_InvalidateTheCache()
    {
        WorkspaceConfig.SetValue(_scope.WorkspaceDir, "k", "first");
        WorkspaceConfig.GetValue(_scope.WorkspaceDir, "k").Should().Be("first");

        WorkspaceConfig.SetValue(_scope.WorkspaceDir, "k", "second");
        WorkspaceConfig.GetValue(_scope.WorkspaceDir, "k").Should().Be("second");

        WorkspaceConfig.UnsetValue(_scope.WorkspaceDir, "k").Should().BeTrue();
        WorkspaceConfig.GetValue(_scope.WorkspaceDir, "k").Should().BeNull();
    }

    // Two workspaces in one process must not share an entry -- the CLI resolves a root per
    // invocation, and tests run many roots inside one process.
    [Fact]
    public void Cache_IsKeyedByWorkspaceRoot()
    {
        using var other = new TestHelpers.StoreScope();

        WorkspaceConfig.SetValue(_scope.WorkspaceDir, "shared", "mine");
        WorkspaceConfig.SetValue(other.WorkspaceDir, "shared", "theirs");

        WorkspaceConfig.GetValue(_scope.WorkspaceDir, "shared").Should().Be("mine");
        WorkspaceConfig.GetValue(other.WorkspaceDir, "shared").Should().Be("theirs");
    }
}
