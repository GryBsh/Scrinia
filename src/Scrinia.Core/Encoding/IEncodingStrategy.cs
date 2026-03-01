namespace Scrinia.Core.Encoding;

public interface IEncodingStrategy
{
    string StrategyId { get; }
    string Description { get; }
    EncodingResult Encode(ReadOnlySpan<byte> input, EncodingOptions options);
    /// <summary>Returns the original bytes decoded from the artifact.</summary>
    byte[] Decode(string artifact);
    bool CanDecode(string artifact);
    ArtifactMetadata ParseHeader(string artifact);
}

/// <summary>Options for NMP/2 encoding.</summary>
/// <param name="CharsPerLine">Max Base64 characters per line (PEM-style). Default 76.</param>
public record EncodingOptions(int CharsPerLine = 76);

public record EncodingResult(
    string Artifact,
    long OriginalBytes,
    long ArtifactChars,
    int EstimatedTokens,
    double BitsPerToken,
    string StrategyId);

public record ArtifactMetadata(
    string StrategyId,
    int OriginalBytes,
    uint? Crc32);
