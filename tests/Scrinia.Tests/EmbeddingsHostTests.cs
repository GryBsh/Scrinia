using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// Phase 2 Task 2 — verifies the embeddings host extracted from <c>WorkspaceSetup</c>.
/// These exercise the pure network-provider classifier and the containment-safe plugin
/// resolution, both of which must never throw when no plugin is installed.
/// </summary>
public class EmbeddingsHostTests
{
    [Fact]
    public void IsHttpEmbeddingsProvider_RecognizesNetworkProviders()
    {
        Assert.True(EmbeddingsHost.IsHttpEmbeddingsProvider("ollama"));
        Assert.True(EmbeddingsHost.IsHttpEmbeddingsProvider("OpenAI"));
        Assert.True(EmbeddingsHost.IsHttpEmbeddingsProvider("voyageai"));
        Assert.True(EmbeddingsHost.IsHttpEmbeddingsProvider("azure"));
        Assert.True(EmbeddingsHost.IsHttpEmbeddingsProvider("google"));
    }

    [Fact]
    public void IsHttpEmbeddingsProvider_RejectsLocalProviders()
    {
        Assert.False(EmbeddingsHost.IsHttpEmbeddingsProvider("model2vec"));
        Assert.False(EmbeddingsHost.IsHttpEmbeddingsProvider(""));
    }

    [Fact]
    public void ResolveEmbeddingsPluginExe_NullWhenNoPluginInstalled()
    {
        // ResolveEmbeddingsPluginExe reads config via WorkspaceSetup.GetConfigValue, which
        // resolves Root — scope and configure that up so the resolution path is reachable
        // without a plugin installed.
        using var scope = new TestHelpers.StoreScope();
        WorkspaceSetup.Configure(scope.WorkspaceDir);
        // No plugin installed in the test environment — must resolve to null, never throw.
        Assert.Null(EmbeddingsHost.ResolveEmbeddingsPluginExe());
    }
}
