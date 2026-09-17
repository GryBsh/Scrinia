namespace Scrinia.Core.Embeddings;

/// <summary>Canonical identity and verified publication contract for the Vulkan GGUF model.</summary>
public static class VulkanModelAssets
{
    public const string Identifier = "all-MiniLM-L6-v2";
    public const string ModelUrl =
        "https://huggingface.co/second-state/All-MiniLM-L6-v2-Embedding-GGUF/resolve/main/all-MiniLM-L6-v2-Q8_0.gguf";
    public const string ModelFilename = "all-MiniLM-L6-v2-Q8_0.gguf";
    public const string ExpectedSha256 =
        "263215c3cadd6e16740741a7624ab4cbb6c8e777688bd5331ecfbf5681c2f8ed";

    /// <summary>The single asset definition consumed by setup and the Vulkan manager.</summary>
    public static DownloadableAsset DefaultAsset { get; } =
        new(Identifier, ModelUrl, ModelFilename, ExpectedSha256);
}
