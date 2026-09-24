namespace Scrinia.Core;

/// <summary>
/// Shared binary-encoding primitives for the durable recovery codecs (AppendJournal, RestoreJournal,
/// RecoveryEnvelope, FileEntity). Each codec keeps its own typed exception and message; this type
/// owns the raw base64/SHA256 logic they all delegate to, so the primitives have a single definition.
/// </summary>
internal static class RecoveryPayload
{
    /// <summary>True when <paramref name="value"/> is a 64-character lowercase hex SHA256 digest.</summary>
    public static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Lowercase hex SHA256 of <paramref name="bytes"/>.</summary>
    public static string Sha256HexLower(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>Decodes canonical (padded) base64. Throws <see cref="FormatException"/> on malformed input;
    /// each codec catches that and wraps it in its own typed exception and message.</summary>
    public static byte[] DecodeBase64(string encoded) => Convert.FromBase64String(encoded);

    /// <summary>Unpadded-to-padded base64 length of a <paramref name="byteCount"/> payload.</summary>
    public static long PaddedBase64Length(long byteCount) => ((byteCount + 2) / 3) * 4;

    /// <summary>Decoded byte length of a canonical base64 <paramref name="encoded"/> string.</summary>
    public static long DecodedBase64Length(string encoded)
    {
        int padding = encoded.EndsWith("==", StringComparison.Ordinal) ? 2 : encoded.EndsWith('=') ? 1 : 0;
        return ((long)encoded.Length / 4 * 3) - padding;
    }
}
