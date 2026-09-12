using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Core;

[JsonConverter(typeof(RestoreJournalStateJsonConverter))]
internal enum RestoreJournalState
{
    Prepared,
    Completed
}

internal sealed record RestoreJournal(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("state")] RestoreJournalState State,
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("entries")] IReadOnlyList<RestoreJournalEntry> Entries);

internal sealed record RestoreJournalEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bodyFile")] string BodyFile,
    [property: JsonPropertyName("bodyBase64")] string BodyBase64,
    [property: JsonPropertyName("bodySha256")] string BodySha256,
    [property: JsonPropertyName("sidecarFile")] string SidecarFile,
    [property: JsonPropertyName("sidecarBase64")] string SidecarBase64,
    [property: JsonPropertyName("sidecarSha256")] string SidecarSha256);

internal sealed record RestoreJournalPayload(ArtifactEntry Entry, byte[] BodyBytes, byte[] SidecarBytes);

internal sealed record RestoreEnvelopeAdapterResult(
    RestoreRecoveryAdapter Adapter,
    RecoveryEnvelopeDocument Envelope);

internal enum RestoreRecoveryFailure
{
    UnsupportedVersion,
    MalformedOrIntegrityFailed,
    ReplayIoFailed
}

internal sealed class RestoreRecoveryException : IOException
{
    public RestoreRecoveryException(string scope, string journalPath, RestoreRecoveryFailure failure, string message,
        Exception? innerException = null, int? unsupportedVersion = null, RecoveryBlock? discoveryBlock = null)
        : base(message, innerException)
    {
        Scope = scope;
        JournalPath = journalPath;
        Failure = failure;
        UnsupportedVersion = unsupportedVersion;
        DiscoveryBlock = discoveryBlock;
    }

    public string Scope { get; }
    public string JournalPath { get; }
    public RestoreRecoveryFailure Failure { get; }
    public int? UnsupportedVersion { get; }
    public RecoveryBlock? DiscoveryBlock { get; }
}

internal sealed class RestoreJournalStateJsonConverter : JsonConverter<RestoreJournalState>
{
    public override RestoreJournalState Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Restore journal state must be a lowercase string.");

        return reader.GetString() switch
        {
            "prepared" => RestoreJournalState.Prepared,
            "completed" => RestoreJournalState.Completed,
            _ => throw new JsonException("Restore journal state is invalid.")
        };
    }

    public override void Write(Utf8JsonWriter writer, RestoreJournalState value, JsonSerializerOptions options)
    {
        string wireValue = value switch
        {
            RestoreJournalState.Prepared => "prepared",
            RestoreJournalState.Completed => "completed",
            _ => throw new JsonException("Restore journal state is invalid.")
        };
        writer.WriteStringValue(wireValue);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RestoreJournal))]
[JsonSerializable(typeof(RestoreJournalEntry))]
[JsonSerializable(typeof(RestoreJournalState))]
internal partial class RestoreJournalJsonContext : JsonSerializerContext;

internal static class RestoreJournalCodec
{
    internal const int MaxJournalEntryCount = 1_000;
    internal const long MaxRawNmp2BodyBytes = 96L * 1024 * 1024;
    internal const long MaxSidecarBytes = 16L * 1024 * 1024;
    internal const long MaxAggregatePayloadBytes = 128L * 1024 * 1024;
    private const long PerEntryJsonOverheadBytes = 2_048;
    private const long DocumentJsonOverheadBytes = 4_096;
    private const string FormatName = "scrinia.restore-journal";

    /// <summary>Test-only observation seam proving decode happens after global admission.</summary>
    internal static AsyncLocal<Action?> DecodeObserver { get; } = new();

    // 128 MiB of payload expands to unpadded Base64 plus a bounded 2 KiB envelope per entry and
    // 4 KiB for the outer document: 178,956,972 + 2,048,000 + 4,096 = 181,009,068.
    internal static long MaxJournalJsonBytes => checked(
        GetPaddedBase64Length(MaxAggregatePayloadBytes) +
        checked((long)MaxJournalEntryCount * PerEntryJsonOverheadBytes) +
        DocumentJsonOverheadBytes);

    internal static bool IsJournalDocumentLengthAdmitted(long length) =>
        length >= 0 && length <= MaxJournalJsonBytes;

    internal static bool IsEncodedPayloadLengthAdmitted(long encodedLength, long maximumDecodedBytes) =>
        encodedLength >= 0 && encodedLength <= GetPaddedBase64Length(maximumDecodedBytes);

    internal static bool IsDecodedPayloadLengthAdmitted(long decodedLength, long maximumDecodedBytes) =>
        decodedLength >= 0 && decodedLength <= maximumDecodedBytes;

    internal static long GetPaddedBase64Length(long byteCount) =>
        checked(((byteCount + 2) / 3) * 4);

    internal static RestoreJournal CreatePrepared(string scope, IReadOnlyList<RestoreJournalPayload> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(entries);

        var journalEntries = new List<RestoreJournalEntry>(entries.Count);
        foreach (RestoreJournalPayload payload in entries)
        {
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(payload.Entry);
            ArgumentNullException.ThrowIfNull(payload.BodyBytes);
            ArgumentNullException.ThrowIfNull(payload.SidecarBytes);

            string safeName = MemoryNaming.SanitizeSegment(payload.Entry.Name);
            journalEntries.Add(new RestoreJournalEntry(
                payload.Entry.Name,
                safeName + ".nmp2",
                Convert.ToBase64String(payload.BodyBytes),
                Convert.ToHexStringLower(SHA256.HashData(payload.BodyBytes)),
                safeName + ".meta.json",
                Convert.ToBase64String(payload.SidecarBytes),
                Convert.ToHexStringLower(SHA256.HashData(payload.SidecarBytes))));
        }

        var journal = new RestoreJournal(
            FormatName,
            DurableFormats.RestoreJournal.Current,
            RestoreJournalState.Prepared,
            Guid.NewGuid().ToString("N"),
            scope,
            journalEntries);
        ValidateAdmission(journal, scope, "<new restore journal>");
        return journal;
    }

    internal static RecoveryEnvelopeDocument ToEnvelope(RestoreJournal journal) =>
        RecoveryEnvelopeCodec.FromRestore(journal);

    internal static RestoreJournal ParseAndValidate(ReadOnlySpan<byte> json, string expectedScope, string journalPath)
    {
        if (!IsJournalDocumentLengthAdmitted(json.Length))
            throw Malformed(expectedScope, journalPath, "Restore journal exceeds the maximum document size.");
        if (json.StartsWith(System.Text.Encoding.UTF8.Preamble))
            throw Malformed(expectedScope, journalPath, "Restore journal must not include a UTF-8 byte-order mark.");
        try
        {
            if (!HasExplicitStateProperty(json))
                throw Malformed(expectedScope, journalPath, "Restore journal state is required.");
        }
        catch (JsonException exception)
        {
            throw Malformed(expectedScope, journalPath, "Restore journal is not valid JSON.", exception);
        }

        RestoreJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize(json, RestoreJournalJsonContext.Default.RestoreJournal);
        }
        catch (JsonException exception)
        {
            throw Malformed(expectedScope, journalPath, "Restore journal is not valid JSON.", exception);
        }

        if (journal is null)
            throw Malformed(expectedScope, journalPath, "Restore journal must be a JSON object.");

        ValidateAdmission(journal, expectedScope, journalPath);
        VerifyAllPayloads(journal, expectedScope, journalPath);
        return journal;
    }

    private static bool HasExplicitStateProperty(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 &&
                reader.ValueTextEquals("state"u8))
                return true;
        }

        return false;
    }

    internal static byte[] Serialize(RestoreJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ValidateAdmission(journal, journal.Scope, "<new restore journal>");
        VerifyAllPayloads(journal, journal.Scope, "<new restore journal>");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(journal, RestoreJournalJsonContext.Default.RestoreJournal);
        if (!IsJournalDocumentLengthAdmitted(json.Length))
            throw Malformed(journal.Scope, "<new restore journal>", "Restore journal exceeds the maximum document size.");
        return json;
    }

    internal static RestoreJournal WithCompleted(RestoreJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal with { State = RestoreJournalState.Completed };
    }

    internal static (byte[] Body, byte[] Sidecar) DecodeAndVerifyPayloads(
        RestoreJournalEntry entry, string scope, string journalPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (
            DecodeAndVerify(entry.BodyBase64, entry.BodySha256, scope, journalPath, "body"),
            DecodeAndVerify(entry.SidecarBase64, entry.SidecarSha256, scope, journalPath, "sidecar"));
    }

    private static void ValidateAdmission(RestoreJournal journal, string expectedScope, string journalPath)
    {
        if (!string.Equals(journal.Format, FormatName, StringComparison.Ordinal))
            throw Malformed(expectedScope, journalPath, "Restore journal format is invalid.");
        if (journal.Version > DurableFormats.RestoreJournal.Current)
            throw Unsupported(expectedScope, journalPath, journal.Version);
        if (journal.Version != DurableFormats.RestoreJournal.Current)
            throw Malformed(expectedScope, journalPath, "Restore journal version is invalid.");
        if (!Enum.IsDefined(journal.State))
            throw Malformed(expectedScope, journalPath, "Restore journal state is invalid.");
        if (!Guid.TryParseExact(journal.TransactionId, "N", out _))
            throw Malformed(expectedScope, journalPath, "Restore journal transaction ID is invalid.");
        if (!string.Equals(journal.Scope, expectedScope, StringComparison.Ordinal))
            throw Malformed(expectedScope, journalPath, "Restore journal scope does not match its directory.");
        if (journal.Entries is not { Count: > 0 } || journal.Entries.Count > MaxJournalEntryCount)
            throw Malformed(expectedScope, journalPath, "Restore journal entry count is invalid.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregatePayloadBytes = 0;
        foreach (RestoreJournalEntry entry in journal.Entries)
        {
            ValidateEntryAdmission(entry, expectedScope, journalPath, names, files, ref aggregatePayloadBytes);
        }
    }

    private static void ValidateEntryAdmission(RestoreJournalEntry entry, string scope, string journalPath,
        HashSet<string> names, HashSet<string> files, ref long aggregatePayloadBytes)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Name) ||
            string.IsNullOrEmpty(entry.BodyFile) || string.IsNullOrEmpty(entry.SidecarFile))
            throw Malformed(scope, journalPath, "Restore journal entry name is invalid.");

        string safeName;
        try
        {
            safeName = MemoryNaming.SanitizeSegment(entry.Name);
        }
        catch (ArgumentException exception)
        {
            throw Malformed(scope, journalPath, "Restore journal entry name is invalid.", exception);
        }

        if (!names.Add(entry.Name) ||
            !string.Equals(entry.BodyFile, safeName + ".nmp2", StringComparison.Ordinal) ||
            !string.Equals(entry.SidecarFile, safeName + ".meta.json", StringComparison.Ordinal) ||
            !files.Add(entry.BodyFile) || !files.Add(entry.SidecarFile))
            throw Malformed(scope, journalPath, "Restore journal entry targets are invalid or duplicate.");

        long bodyLength = ValidateEncodedPayload(entry.BodyBase64, entry.BodySha256, MaxRawNmp2BodyBytes, scope, journalPath, "body");
        long sidecarLength = ValidateEncodedPayload(entry.SidecarBase64, entry.SidecarSha256, MaxSidecarBytes, scope, journalPath, "sidecar");
        aggregatePayloadBytes = AddDecodedPayloadLength(aggregatePayloadBytes, bodyLength, scope, journalPath);
        aggregatePayloadBytes = AddDecodedPayloadLength(aggregatePayloadBytes, sidecarLength, scope, journalPath);
    }

    internal static long AddDecodedPayloadLength(long aggregate, long payloadLength, string scope, string journalPath)
    {
        try
        {
            long total = checked(aggregate + payloadLength);
            if (total > MaxAggregatePayloadBytes)
                throw Malformed(scope, journalPath, "Restore journal payload exceeds the aggregate size limit.");
            return total;
        }
        catch (OverflowException exception)
        {
            throw Malformed(scope, journalPath, "Restore journal payload size overflowed.", exception);
        }
    }

    private static void VerifyAllPayloads(RestoreJournal journal, string scope, string journalPath)
    {
        foreach (RestoreJournalEntry entry in journal.Entries)
        {
            DecodeAndVerify(entry.BodyBase64, entry.BodySha256, scope, journalPath, "body");
            DecodeAndVerify(entry.SidecarBase64, entry.SidecarSha256, scope, journalPath, "sidecar");
        }
    }

    private static long ValidateEncodedPayload(string encoded, string digest, long maximumBytes, string scope,
        string journalPath, string payloadName)
    {
        if (!IsLowercaseSha256(digest) || !IsCanonicalBase64(encoded, maximumBytes))
            throw Malformed(scope, journalPath, $"Restore journal {payloadName} encoding is invalid.");

        long decodedLength = GetDecodedBase64Length(encoded);
        if (!IsDecodedPayloadLengthAdmitted(decodedLength, maximumBytes))
            throw Malformed(scope, journalPath, $"Restore journal {payloadName} exceeds its size limit.");
        return decodedLength;
    }

    private static byte[] DecodeAndVerify(string encoded, string digest, string scope, string journalPath,
        string payloadName)
    {
        byte[] payload;
        try
        {
            DecodeObserver.Value?.Invoke();
            payload = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw Malformed(scope, journalPath, $"Restore journal {payloadName} encoding is invalid.", exception);
        }

        string actualDigest = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (!string.Equals(actualDigest, digest, StringComparison.Ordinal))
            throw Malformed(scope, journalPath, $"Restore journal {payloadName} failed integrity verification.");
        return payload;
    }

    private static bool IsLowercaseSha256(string? digest)
    {
        if (digest is not { Length: 64 }) return false;
        return digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsCanonicalBase64(string? value, long maximumBytes)
    {
        if (value is null || value.Length % 4 != 0) return false;
        if (!IsEncodedPayloadLengthAdmitted(value.Length, maximumBytes)) return false;
        int padding = value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0;
        for (int index = 0; index < value.Length - padding; index++)
        {
            char character = value[index];
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/'))
                return false;
        }
        if (padding > 0 && !value[(value.Length - padding)..].All(character => character == '=')) return false;
        if (value.Length == 0 || padding == 0) return true;

        int finalValue = GetBase64Value(value[value.Length - padding - 1]);
        return padding == 1 ? (finalValue & 0b11) == 0 : (finalValue & 0b1111) == 0;
    }

    private static int GetBase64Value(char character) => character switch
    {
        >= 'A' and <= 'Z' => character - 'A',
        >= 'a' and <= 'z' => character - 'a' + 26,
        >= '0' and <= '9' => character - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1
    };

    private static long GetDecodedBase64Length(string value)
    {
        int padding = value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0;
        return checked(((long)value.Length / 4 * 3) - padding);
    }

    private static RestoreRecoveryException Unsupported(string scope, string journalPath, int version) =>
        new(scope, journalPath, RestoreRecoveryFailure.UnsupportedVersion,
            $"Restore journal version {version} is unsupported.", unsupportedVersion: version);

    private static RestoreRecoveryException Malformed(string scope, string journalPath, string message,
        Exception? innerException = null) =>
        new(scope, journalPath, RestoreRecoveryFailure.MalformedOrIntegrityFailed, message, innerException);
}
