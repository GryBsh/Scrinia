using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core.Models;

/// <summary>The two logical namespaces in which a memory may live.</summary>
public enum MemoryAddressKind
{
    Persistent,
    Ephemeral
}

/// <summary>A validated, canonical logical memory address.</summary>
[JsonConverter(typeof(MemoryAddressJsonConverter))]
public readonly record struct MemoryAddress
{
    /// <summary>Maximum UTF-8 encoded size of a logical address.</summary>
    public const int MaxUtf8Bytes = 4096;

    private MemoryAddress(string value, MemoryAddressKind kind)
    {
        Value = value;
        Kind = kind;
    }

    /// <summary>The canonical logical value, without the ephemeral display prefix.</summary>
    public string Value { get; }

    /// <summary>The namespace represented by this address.</summary>
    public MemoryAddressKind Kind { get; }

    /// <summary>Whether this value was produced by the validated parser.</summary>
    public bool IsValid => Value is not null &&
        (Kind is MemoryAddressKind.Persistent or MemoryAddressKind.Ephemeral);

    /// <summary>Parses and canonicalizes a raw logical address.</summary>
    public static MemoryAddress Parse(string raw)
    {
        if (TryParse(raw, out var address, out var error))
            return address;

        throw new MemoryAddressException(error);
    }

    /// <summary>Attempts to parse a raw logical address without producing a partial value.</summary>
    public static bool TryParse(string? raw, out MemoryAddress address, out MemoryAddressError error)
    {
        address = default;
        error = MemoryAddressError.Create("Address is malformed.");

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = MemoryAddressError.Create("Address must not be empty or whitespace-only.");
            return false;
        }

        var trimmed = raw.Trim();
        if (System.Text.Encoding.UTF8.GetByteCount(trimmed) > MaxUtf8Bytes)
        {
            error = MemoryAddressError.Create("Address exceeds the UTF-8 byte limit.", MaxUtf8Bytes,
                System.Text.Encoding.UTF8.GetByteCount(trimmed));
            return false;
        }

        if (ContainsControlCharacters(trimmed) || ContainsPhysicalOrUriSyntax(trimmed))
        {
            error = MemoryAddressError.Create("Address contains invalid path, URI, or control syntax.");
            return false;
        }

        if (MemoryNaming.IsEphemeralName(trimmed))
            return TryParseEphemeralName(MemoryNaming.StripEphemeralPrefix(trimmed), out address, out error);

        if (trimmed[0] != '/' || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;

        var segments = trimmed[1..].Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => !IsLegalSegment(segment)))
            return false;

        if (segments[0].Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length != 2)
                return false;
            return TryParseEphemeralName(segments[1], out address, out error);
        }

        address = new MemoryAddress(trimmed, MemoryAddressKind.Persistent);
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (!IsValid)
            throw new InvalidOperationException("The memory address is invalid; use Parse or TryParse to create a canonical address.");

        return Kind == MemoryAddressKind.Ephemeral ? $"~{Value}" : Value;
    }

    private static bool TryParseEphemeralName(string name, out MemoryAddress address, out MemoryAddressError error)
    {
        address = default;
        error = MemoryAddressError.Create("Ephemeral address must contain one legal name.");
        if (!IsLegalSegment(name))
            return false;

        address = new MemoryAddress(name, MemoryAddressKind.Ephemeral);
        return true;
    }

    private static bool IsLegalSegment(string segment) =>
        !string.IsNullOrEmpty(segment) && segment is not "." and not ".." &&
        segment.All(character => character is not '/' and not '\\' and not ':' and not '?' and not '#' and
            not '%' and not '*' and not '<' and not '>' and not '|' and not '"');

    private static bool ContainsPhysicalOrUriSyntax(string value) => MemoryNaming.IsUriLike(value);

    private static bool ContainsControlCharacters(string value) => value.Any(char.IsControl);
}

internal sealed class MemoryAddressJsonConverter : JsonConverter<MemoryAddress>
{
    public override MemoryAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Memory addresses must be JSON strings.");

        try
        {
            return MemoryAddress.Parse(reader.GetString()!);
        }
        catch (MemoryAddressException exception)
        {
            throw new JsonException(exception.Message, exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, MemoryAddress value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
