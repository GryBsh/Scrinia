using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests.Setup;

/// <summary>
/// Tests for <see cref="ScriniaCommands.ClearStaleOllamaConfig"/>. The setup flow calls
/// this whenever the Ollama path falls through (probe failed, user declined, or
/// <c>--no-ollama</c>) so a prior Ollama configuration doesn't silently persist into the
/// next startup. Covers both the cleanup positive paths and the "preserve unrelated
/// config" negative paths.
/// </summary>
public sealed class ClearStaleOllamaConfigTests : IDisposable
{
    private readonly string _root;

    public ClearStaleOllamaConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_clear_ollama_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ClearsEmbeddingsKeys_WhenProviderWasOllama()
    {
        WorkspaceConfig.SetValue(_root, "Scrinia:Embeddings:Provider", "ollama");
        WorkspaceConfig.SetValue(_root, "Scrinia:Embeddings:OllamaBaseUrl", "http://localhost:11434");
        WorkspaceConfig.SetValue(_root, "Scrinia:Embeddings:OllamaModel", "nomic-embed-text");

        ScriniaCommands.ClearStaleOllamaConfig(_root);

        WorkspaceConfig.GetValue(_root, "Scrinia:Embeddings:Provider").Should().BeNull();
        WorkspaceConfig.GetValue(_root, "Scrinia:Embeddings:OllamaBaseUrl").Should().BeNull();
        WorkspaceConfig.GetValue(_root, "Scrinia:Embeddings:OllamaModel").Should().BeNull();
    }

    [Fact]
    public void ClearsLlmKeys_WhenLlmPointedAtLocalhostOllama()
    {
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:Provider", "openai");
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:BaseUrl", "http://localhost:11434/v1");
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:Model", "lfm2:1.2b");

        ScriniaCommands.ClearStaleOllamaConfig(_root);

        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:Provider").Should().BeNull();
        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:BaseUrl").Should().BeNull();
        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:Model").Should().BeNull();
    }

    [Fact]
    public void PreservesUserOpenAiConfig_PointingAtApiOpenAi()
    {
        // A user explicitly pointed Llm at the real OpenAI API — Ollama cleanup must
        // not touch it, even though Provider value matches the Ollama-installed pattern.
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:Provider", "openai");
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:BaseUrl", "https://api.openai.com/v1");
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:Model", "gpt-4o-mini");

        ScriniaCommands.ClearStaleOllamaConfig(_root);

        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:Provider").Should().Be("openai");
        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:BaseUrl").Should().Be("https://api.openai.com/v1");
        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:Model").Should().Be("gpt-4o-mini");
    }

    [Fact]
    public void PreservesAnthropicConfig()
    {
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:Provider", "anthropic");
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:AnthropicApiKey", "sk-ant-xxxxx");
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:Model", "claude-haiku-4-5");

        ScriniaCommands.ClearStaleOllamaConfig(_root);

        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:Provider").Should().Be("anthropic");
        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:AnthropicApiKey").Should().Be("sk-ant-xxxxx");
        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:Model").Should().Be("claude-haiku-4-5");
    }

    [Fact]
    public void PreservesNonOllamaEmbeddingsProvider()
    {
        WorkspaceConfig.SetValue(_root, "Scrinia:Embeddings:Provider", "voyageai");
        WorkspaceConfig.SetValue(_root, "Scrinia:Embeddings:VoyageAiApiKey", "pa-xxx");

        ScriniaCommands.ClearStaleOllamaConfig(_root);

        WorkspaceConfig.GetValue(_root, "Scrinia:Embeddings:Provider").Should().Be("voyageai");
        WorkspaceConfig.GetValue(_root, "Scrinia:Embeddings:VoyageAiApiKey").Should().Be("pa-xxx");
    }

    [Fact]
    public void NoOp_WhenNothingOllamaInstalled()
    {
        WorkspaceConfig.SetValue(_root, "Scrinia:Embeddings:Provider", "model2vec");
        ScriniaCommands.ClearStaleOllamaConfig(_root);
        WorkspaceConfig.GetValue(_root, "Scrinia:Embeddings:Provider").Should().Be("model2vec");
    }

    [Fact]
    public void ClearsLlm_EvenWhenEmbeddingsWasAlreadyMigrated()
    {
        // User manually fixed Embeddings:Provider but the LLM keys were left pointing
        // at localhost:11434 from the original Ollama install — cleanup should still
        // catch the LLM half.
        WorkspaceConfig.SetValue(_root, "Scrinia:Embeddings:Provider", "model2vec");
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:Provider", "openai");
        WorkspaceConfig.SetValue(_root, "Scrinia:Llm:BaseUrl", "http://localhost:11434/v1");

        ScriniaCommands.ClearStaleOllamaConfig(_root);

        WorkspaceConfig.GetValue(_root, "Scrinia:Embeddings:Provider").Should().Be("model2vec");
        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:Provider").Should().BeNull();
        WorkspaceConfig.GetValue(_root, "Scrinia:Llm:BaseUrl").Should().BeNull();
    }
}
