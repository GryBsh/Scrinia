using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

public class WorkspaceConfigTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope = new();

    public void Dispose() => _scope.Dispose();

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
}
