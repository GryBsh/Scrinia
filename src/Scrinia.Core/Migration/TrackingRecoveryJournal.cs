using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Configuration;
using Scrinia.Core.Formats;

namespace Scrinia.Core.Migration;

internal sealed record TrackingState(
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("bytes")] byte[]? Bytes);

internal sealed record TrackingMember(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("before")] TrackingState Before,
    [property: JsonPropertyName("after")] TrackingState After);

internal sealed record TrackingJournal(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("members")] TrackingMember[] Members);

/// <summary>Bounded, strict evidence codec. Reading never mutates or replays a journal.</summary>
internal static class TrackingRecoveryJournal
{
    internal const long MaxStateBytes = 16L * 1024 * 1024;
    internal const long MaxTotalBytes = 128L * 1024 * 1024;
    internal const long MaxJournalBytes = 192L * 1024 * 1024;
    internal const string FileName = ".tracking-recovery.json";
    private static readonly string[] MemberNames = ["config.json", ".gitignore", ".gitattributes", "merge.config"];
    private static readonly string[] JournalProperties = ["version", "operationId", "operation", "decision", "members"];
    private static readonly string[] MemberProperties = ["path", "before", "after"];
    private static readonly string[] StateProperties = ["exists", "length", "sha256", "bytes"];

    internal static byte[] Serialize(TrackingJournal journal)
    {
        Validate(journal);
        return JsonSerializer.SerializeToUtf8Bytes(journal, ConfigJsonContext.Default.TrackingJournal);
    }

    internal static TrackingJournal Read(string journalPath, string workspaceRoot)
    {
        try
        {
            string root = Path.Combine(ResolveWorkspaceRoot(workspaceRoot), ".scrinia");
            string expected = Path.Combine(root, FileName);
            string supplied = Path.GetFullPath(journalPath);
            string aliasExpected = Path.GetFullPath(Path.Combine(workspaceRoot, ".scrinia", FileName));
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(supplied, expected, comparison) && !string.Equals(supplied, aliasExpected, comparison))
                throw Invalid("Tracking journal is outside its fixed workspace location.");
            InspectPath(expected, file: true);
            foreach (string name in MemberNames)
            {
                string target = Path.GetFullPath(Path.Combine(root, name));
                if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.Ordinal))
                    throw Invalid("Tracking target is outside its parent.");
                InspectPath(target, file: true);
            }
            using var stream = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var input = ReadBounded(stream);
            ReadOnlySpan<byte> json = input.GetBuffer().AsSpan(0, checked((int)input.Length));
            Preflight(json);
            var journal = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.TrackingJournal)
                ?? throw Invalid("Tracking journal is null.");
            Validate(journal);
            return journal;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                                   or ArgumentException or NotSupportedException or OverflowException or InvalidOperationException)
        {
            throw new TrackingRecoveryRequiredException("Tracking evidence cannot be safely read.", journalPath, ex);
        }
    }

    internal static void ThrowIfPresent(string workspaceRoot)
    {
        string path = Path.Combine(workspaceRoot, ".scrinia", FileName);
        try
        {
            string canonicalPath = Path.Combine(ResolveWorkspaceRoot(workspaceRoot), ".scrinia", FileName);
            if (InspectPath(canonicalPath, file: true))
                throw Invalid("Tracking recovery evidence is retained.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TrackingRecoveryRequiredException("Tracking recovery is required.", path, ex);
        }
    }

    // Resolve ownership once at the workspace boundary; never canonicalize descendant links into permission.
    // Transaction/admission callers must use this same root while preserving their explicit lease identity.
    internal static string ResolveWorkspaceRoot(string workspaceRoot)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(PathContainment.ResolveCanonical(workspaceRoot));
            InspectPath(root, file: false);
            return root;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TrackingRecoveryRequiredException("Tracking workspace ownership cannot be inspected.", innerException: ex);
        }
    }

    internal static void Validate(TrackingJournal journal)
    {
        if (journal is null || FormatVersion.Evaluate(DurableFormats.TrackingJournal, journal.Version) != FormatDisposition.Load)
            throw Invalid("Unsupported tracking journal version.");
        if (string.IsNullOrEmpty(journal.OperationId) || System.Text.Encoding.UTF8.GetByteCount(journal.OperationId) > 64)
            throw Invalid("Invalid tracking operation ID.");
        if (journal.Operation is not ("set" or "migrate") || journal.Decision is not ("prepared" or "committed"))
            throw Invalid("Unknown tracking operation or decision.");
        if (journal.Members is null || journal.Members.Length != MemberNames.Length)
            throw Invalid("Tracking journal must contain exactly four members.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (TrackingMember member in journal.Members)
        {
            if (member is null || !MemberNames.Contains(member.Path, StringComparer.Ordinal) || !names.Add(member.Path))
                throw Invalid("Invalid or duplicate tracking member.");
            ValidateState(member.Before);
            ValidateState(member.After);
            total = checked(total + member.Before.Length + member.After.Length);
            if (total > MaxTotalBytes)
                throw Invalid("Tracking journal exceeds its state limit.");
        }
    }

    private static void ValidateState(TrackingState state)
    {
        if (state is null || state.Length is < 0 or > MaxStateBytes)
            throw Invalid("Invalid tracking state length.");
        if (!state.Exists)
        {
            if (state.Length != 0 || state.Sha256 is not null || state.Bytes is not null)
                throw Invalid("Absent tracking state has content.");
            return;
        }
        if (state.Bytes is null || state.Bytes.LongLength != state.Length || !IsHash(state.Sha256)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(state.Bytes)), state.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid("Tracking state content does not match its length or digest.");
    }

    private static bool IsHash(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static MemoryStream ReadBounded(Stream stream)
    {
        if (stream.CanSeek && stream.Length > MaxJournalBytes)
            throw Invalid("Tracking journal exceeds its serialized limit.");
        var result = new MemoryStream();
        try
        {
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                int requested = (int)Math.Min(buffer.Length, MaxJournalBytes - result.Length + 1);
                int count = stream.Read(buffer, 0, requested);
                if (count == 0) return result;
                if (result.Length + count > MaxJournalBytes)
                    throw Invalid("Tracking journal exceeds its serialized limit.");
                result.Write(buffer, 0, count);
            }
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    // Inspect every existing ancestor; File.Exists/Directory.Exists suppress access and I/O failures.
    // A missing descendant is safe only after its complete parent chain has been inspected.
    private static bool InspectPath(string path, bool file)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !InspectPath(parent, file: false))
            return false;
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).LinkTarget is not null)
            throw Invalid("Tracking paths cannot traverse links or reparse points.");
        bool directory = (attributes & FileAttributes.Directory) != 0;
        if (file == directory)
            throw Invalid("Tracking path has the wrong filesystem type.");
        if (file) RecoveryFileType.RequireRegularFile(path);
        return true;
    }

    private static void Preflight(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8 });
        Next(ref reader, JsonTokenType.StartObject);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        while (NextProperty(ref reader, JournalProperties, properties) is { } property)
        {
            switch (property)
            {
                case "version":
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int version)
                        || FormatVersion.Evaluate(DurableFormats.TrackingJournal, version) != FormatDisposition.Load)
                        throw Invalid("Unsupported tracking journal version.");
                    break;
                case "operationId":
                    string id = SmallString(ref reader, 384);
                    if (id.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(id) > 64)
                        throw Invalid("Invalid tracking operation ID.");
                    break;
                case "operation":
                    if (SmallString(ref reader, 42) is not ("set" or "migrate"))
                        throw Invalid("Unknown tracking operation.");
                    break;
                case "decision":
                    if (SmallString(ref reader, 54) is not ("prepared" or "committed"))
                        throw Invalid("Unknown tracking decision.");
                    break;
                case "members":
                    Require(reader.TokenType == JsonTokenType.StartArray);
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    int count = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (++count > 4) throw Invalid("Too many tracking members.");
                        PreflightMember(ref reader, names, ref total);
                    }
                    Require(reader.TokenType == JsonTokenType.EndArray && count == 4);
                    break;
            }
        }
        Require(!reader.Read());
    }

    private static void PreflightMember(ref Utf8JsonReader reader, HashSet<string> names, ref long total)
    {
        Require(reader.TokenType == JsonTokenType.StartObject);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        while (NextProperty(ref reader, MemberProperties, properties) is { } property)
        {
            if (property == "path")
            {
                string name = SmallString(ref reader, 84);
                if (!MemberNames.Contains(name, StringComparer.Ordinal) || !names.Add(name))
                    throw Invalid("Invalid or duplicate tracking member.");
            }
            else
                PreflightState(ref reader, ref total);
        }
    }

    private static void PreflightState(ref Utf8JsonReader reader, ref long total)
    {
        Require(reader.TokenType == JsonTokenType.StartObject);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        bool exists = false, nullBytes = true;
        long length = 0;
        string? hash = null;
        ReadOnlySpan<byte> payload = default;
        while (NextProperty(ref reader, StateProperties, properties) is { } property)
        {
            switch (property)
            {
                case "exists":
                    Require(reader.TokenType is JsonTokenType.True or JsonTokenType.False);
                    exists = reader.GetBoolean();
                    break;
                case "length":
                    Require(reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out length)
                        && length is >= 0 and <= MaxStateBytes);
                    break;
                case "sha256":
                    hash = reader.TokenType == JsonTokenType.Null ? null : SmallString(ref reader, 384);
                    break;
                case "bytes":
                    Require(reader.TokenType is JsonTokenType.Null or JsonTokenType.String);
                    nullBytes = reader.TokenType == JsonTokenType.Null;
                    payload = reader.ValueSpan;
                    break;
            }
        }
        // Absence is checked before interpreting even one payload character, regardless of property order.
        if (!exists)
            Require(length == 0 && hash is null && nullBytes);
        else
            Require(!nullBytes && IsHash(hash) && Base64Length(payload) == length);
        total = checked(total + length);
        if (total > MaxTotalBytes) throw Invalid("Tracking journal exceeds its state limit.");
    }

    // Validate the alphabet, padding and unused bits without allocating decoded bytes.
    // JSON escapes are interpreted directly, so an escaped base64 character cannot hide extra content.
    private static long Base64Length(ReadOnlySpan<byte> raw)
    {
        long count = 0;
        int padding = 0, lastValue = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            int c = raw[i];
            if (c == '\\')
            {
                if (++i >= raw.Length) throw Invalid("Invalid base64 escape.");
                c = raw[i];
                if (c == 'u')
                {
                    if (i + 4 >= raw.Length) throw Invalid("Invalid base64 escape.");
                    c = 0;
                    for (int n = 0; n < 4; n++)
                    {
                        int digit = Hex(raw[++i]);
                        if (digit < 0) throw Invalid("Invalid base64 escape.");
                        c = c * 16 + digit;
                    }
                }
                else if (c != '/') throw Invalid("Invalid base64 escape.");
            }
            count++;
            if (count > ((MaxStateBytes + 2) / 3) * 4) throw Invalid("Tracking payload exceeds its state limit.");
            if (c == '=')
            {
                if (++padding > 2) throw Invalid("Invalid base64 padding.");
                continue;
            }
            if (padding != 0) throw Invalid("Invalid base64 padding.");
            lastValue = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= 'a' and <= 'z' => c - 'a' + 26,
                >= '0' and <= '9' => c - '0' + 52,
                '+' => 62,
                '/' => 63,
                _ => -1
            };
            if (lastValue < 0) throw Invalid("Invalid base64 alphabet.");
        }
        if (count % 4 != 0 || (padding == 1 && (lastValue & 3) != 0) || (padding == 2 && (lastValue & 15) != 0))
            throw Invalid("Invalid base64 encoding.");
        long decoded = count / 4 * 3 - padding;
        if (decoded < 0 || decoded > MaxStateBytes) throw Invalid("Tracking payload exceeds its state limit.");
        return decoded;
    }

    private static int Hex(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1
    };

    private static string? NextProperty(ref Utf8JsonReader reader, string[] allowed, HashSet<string> seen)
    {
        Require(reader.Read());
        if (reader.TokenType == JsonTokenType.EndObject)
        {
            Require(seen.Count == allowed.Length);
            return null;
        }
        Require(reader.TokenType == JsonTokenType.PropertyName);
        string? name = null;
        foreach (string candidate in allowed)
            if (reader.ValueTextEquals(candidate)) { name = candidate; break; }
        if (name is null || !seen.Add(name)) throw Invalid("Unknown or duplicate tracking property.");
        Require(reader.Read());
        return name;
    }

    private static string SmallString(ref Utf8JsonReader reader, int maximumEncodedLength)
    {
        Require(reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length <= maximumEncodedLength);
        return reader.GetString()!;
    }

    private static void Next(ref Utf8JsonReader reader, JsonTokenType type) =>
        Require(reader.Read() && reader.TokenType == type);

    private static void Require(bool condition)
    {
        if (!condition) throw Invalid("Invalid tracking journal structure or state.");
    }

    private static TrackingRecoveryRequiredException Invalid(string message) => new(message);
}

