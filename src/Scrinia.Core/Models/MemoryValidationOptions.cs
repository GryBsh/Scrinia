namespace Scrinia.Core.Models;

/// <summary>Limits applied to a typed memory write before it reaches a store.</summary>
public sealed record MemoryValidationOptions
{
    public const int DefaultMaxContentBytesPerChunk = 5 * 1024 * 1024;
    public const int DefaultMaxTotalContentBytes = 64 * 1024 * 1024;
    public const int DefaultMaxChunks = 100_000;
    public const int DefaultMaxEncodedChunkBytes = 8 * 1024 * 1024;
    public const int DefaultMaxAttributeKeyLength = 128;
    public const int DefaultMaxAttributeValueLength = 4096;
    public const int DefaultMaxKeywordLength = 4096;

    public int MaxAddressBytes { get; init; } = MemoryAddress.MaxUtf8Bytes;
    public int MaxContentBytesPerChunk { get; init; } = DefaultMaxContentBytesPerChunk;
    public int MaxTotalContentBytes { get; init; } = DefaultMaxTotalContentBytes;
    public int MaxChunks { get; init; } = DefaultMaxChunks;
    public int MaxEncodedChunkBytes { get; init; } = DefaultMaxEncodedChunkBytes;
    public int MaxAttributeKeyLength { get; init; } = DefaultMaxAttributeKeyLength;
    public int MaxAttributeValueLength { get; init; } = DefaultMaxAttributeValueLength;
    public int MaxKeywordLength { get; init; } = DefaultMaxKeywordLength;
}
