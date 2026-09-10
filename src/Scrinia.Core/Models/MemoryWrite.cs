using System.Text;
using System.Collections.ObjectModel;
using Scrinia.Core.Encoding;

namespace Scrinia.Core.Models;

/// <summary>
/// The raw inputs a store derives a memory from — and deliberately nothing the store computes for
/// itself. There is no <c>TermFrequencies</c>, no <c>ChunkEntries</c>, no <c>ContentPreview</c> and
/// no <c>FileHashes</c> on this record, because a caller that could supply them would be deriving.
///
/// <para><b>Raw in, derived out (D-01/D-29).</b> <see cref="Keywords"/> is the CALLER's own keyword
/// list. Handing back a list a store previously derived reclassifies every auto-extracted term as
    /// agent-supplied and boosts it +5 instead of +2. The shape of this record makes that
    /// provenance error hard to express: there is no
/// field through which the rest of a derived entry could come back in with them.</para>
///
/// <para><b><see cref="Name"/> and <see cref="Scope"/> are the resolved pair</b>, as every other
/// store member takes them (<c>WriteArtifactAsync(subject, scope, …)</c>,
/// <c>ArchiveVersion(subject, scope)</c>, <c>LoadIndex(scope)</c>) — not a qualified path the store
/// re-parses. A caller holding a user-facing path resolves it once through
/// <c>IMemoryStore.ParseQualifiedName</c> and passes both halves. The one exception is an ephemeral
/// target, whose <c>~</c>-prefixed <see cref="Name"/> is the whole address and whose
/// <see cref="Scope"/> is ignored.</para>
/// </summary>
/// <param name="Name">The memory's subject, or a <c>~name</c> for an ephemeral target.</param>
/// <param name="Scope">The resolved scope (<c>"local"</c>, <c>"local:topic"</c>). Ignored for an
/// ephemeral <paramref name="Name"/>.</param>
/// <param name="Content">The chunks to write. One element per independently retrievable chunk;
/// an append writes exactly one.</param>
/// <param name="Description">A curated description. Becomes the preview, winning over the auto
/// snippet.</param>
/// <param name="Attributes">Explicit caller-supplied attributes. Tags, anchors and review
/// conditions are ordinary keys in this one flat map.</param>
/// <param name="Keywords">The caller's RAW keywords — never a list a store derived (D-01).</param>
/// <summary>Describes a persistent subject that cannot safely produce every durable filename.</summary>
public sealed class InvalidMemoryWriteException : ArgumentException
{
    /// <summary>Correlation identifier assigned when append validation began.</summary>
    public string? AppendOperationId { get; set; }
    /// <summary>Initializes a categorized write-policy refusal.</summary>
    public InvalidMemoryWriteException(MemoryError error)
        : base(error.Message, error.Field)
    {
        Error = error;
        MaxPersistentSubjectLength = error.Limit is { } limit ? (int)limit : 0;
        SanitizedSubjectLength = error.Actual is { } actual ? (int)actual : 0;
    }

    /// <summary>Initializes a filesystem-safe write refusal.</summary>
    public InvalidMemoryWriteException(int maxPersistentSubjectLength, int sanitizedSubjectLength)
        : base(
            $"Persistent memory subject exceeds {maxPersistentSubjectLength} sanitized characters " +
            $"(received {sanitizedSubjectLength}).",
            "Name")
    {
        MaxPersistentSubjectLength = maxPersistentSubjectLength;
        SanitizedSubjectLength = sanitizedSubjectLength;
    }

    /// <summary>Gets the maximum durable subject length.</summary>
    public int MaxPersistentSubjectLength { get; }

    /// <summary>Gets the rejected sanitized subject length.</summary>
    public int SanitizedSubjectLength { get; }

    /// <summary>Gets the structured policy error, when the refusal came from shared validation.</summary>
    public MemoryError? Error { get; }
}

public sealed record MemoryWrite
{
    /// <summary>Maximum length of a sanitized persistent subject.</summary>
    public const int MaxPersistentSubjectLength = 182;

    /// <summary>Initializes an immutable, validated UTF-8 memory write.</summary>
    public MemoryWrite(MemoryAddress address, IReadOnlyList<ReadOnlyMemory<byte>> content,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
        IReadOnlyList<string> keywords, string? description = null)
    {
        if (content is null)
            throw new InvalidMemoryWriteException(Error("content_null", "Content must not be null.", "content"));

        string[] text = new string[content.Count];
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        for (int index = 0; index < content.Count; index++)
        {
            try
            {
                text[index] = strictUtf8.GetString(content[index].Span);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidMemoryWriteException(Error(
                    "invalid_utf8", "Content contains malformed UTF-8 bytes.", $"content[{index}]"));
            }
        }
        var validation = TryCreate(address, text, attributes, keywords, description: description);
        if (!validation.IsValid)
            throw new InvalidMemoryWriteException(validation.Error!);

        var valid = validation.Write!;
        Address = valid.Address;
        Name = valid.Name;
        Scope = valid.Scope;
        Content = valid.Content;
        RawContent = valid.RawContent;
        Description = valid.Description;
        Attributes = valid.Attributes;
        Keywords = valid.Keywords;
    }

    /// <summary>Creates a write from already encoded content.</summary>
    internal static MemoryWrite Create(MemoryAddress address,
        IReadOnlyList<ReadOnlyMemory<byte>> content,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
        IReadOnlyList<string> keywords, string? description = null) =>
        new(address, content, attributes, keywords, description);

    /// <summary>Creates a validated write for a resolved address and physical scope.</summary>
    internal static MemoryWrite Create(MemoryAddress address, string scope, string[] content,
        string? description = null, Dictionary<string, string[]>? attributes = null,
        string[]? keywords = null)
    {
        var effectiveAddress = address.Kind == MemoryAddressKind.Ephemeral || scope == "local"
            ? address
            : ResolveScopedAddress(address, scope);
        var write = Create(effectiveAddress, content,
            attributes?.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value)
                ?? new Dictionary<string, IReadOnlyList<string>>(),
            keywords ?? [], description: description);
        return write.Readdress(effectiveAddress, scope, effectiveAddress.Kind == MemoryAddressKind.Ephemeral
            ? effectiveAddress.Value
             : effectiveAddress.Value[(effectiveAddress.Value.LastIndexOf('/') + 1)..]);
    }

    /// <summary>Creates a validated write for a resolved address and physical scope from logical text.</summary>
    internal static MemoryWrite Create(MemoryAddress address, string scope,
        IReadOnlyList<string?>? content,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes,
        IReadOnlyList<string>? keywords,
        bool isAppend = false, string? description = null)
    {
        var effectiveAddress = address.Kind == MemoryAddressKind.Ephemeral || scope == "local"
            ? address
            : ResolveScopedAddress(address, scope);
        var write = Create(effectiveAddress, content, attributes, keywords,
            isAppend: isAppend, description: description);
        return write.Readdress(effectiveAddress, scope, effectiveAddress.Kind == MemoryAddressKind.Ephemeral
            ? address.ToString()
            : effectiveAddress.Value[(effectiveAddress.Value.LastIndexOf('/') + 1)..]);
    }

    private MemoryWrite(MemoryAddress address, IReadOnlyList<ReadOnlyMemory<byte>> content,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes, IReadOnlyList<string> keywords,
        string? description, string[] rawContent, string scope = "local", string? name = null)
    {
        Address = address;
        Content = new ReadOnlyCollection<ReadOnlyMemory<byte>>(
            content.Select(chunk => (ReadOnlyMemory<byte>)chunk.ToArray()).ToArray());
        Attributes = new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            attributes.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<string>)new ReadOnlyCollection<string>(pair.Value.ToArray()),
                StringComparer.OrdinalIgnoreCase));
        Keywords = new ReadOnlyCollection<string>(keywords.ToArray());
        Description = description;
        RawContent = rawContent.ToArray();
        Name = name ?? address.ToString();
        Scope = scope;
    }

    internal MemoryWrite Readdress(MemoryAddress address, string scope, string subject) =>
        new(address, Content, Attributes, Keywords, Description, RawContent, scope, subject);

    private static MemoryAddress ResolveScopedAddress(MemoryAddress address, string scope)
    {
        string topicPath = scope["local:".Length..];
        string qualifiedPrefix = "/" + topicPath + "/";
        if (address.Value.StartsWith(qualifiedPrefix, StringComparison.OrdinalIgnoreCase))
            return address;

        string subject = address.Value[(address.Value.LastIndexOf('/') + 1)..];
        return MemoryAddress.Parse(qualifiedPrefix + subject);
    }

    /// <summary>Creates a validated write and reports the first policy violation.</summary>
    public static MemoryWriteValidationResult TryCreate(
        MemoryAddress address,
        IReadOnlyList<string?>? content,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? attributes,
        IReadOnlyList<string>? keywords,
        MemoryValidationOptions? options = null,
        bool isAppend = false,
        string? description = null)
    {
        options ??= new MemoryValidationOptions();
        var error = Validate(address, content, attributes, keywords, options, isAppend);
        if (error is not null)
            return new(null, error);

        var rawContent = content!.Select(value => value!).ToArray();
        var bytes = rawContent.Select(value => (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(value)).ToArray();
        return new(new MemoryWrite(address, (IReadOnlyList<ReadOnlyMemory<byte>>)bytes,
            attributes!, keywords!, description, rawContent), null);
    }

    /// <summary>Creates a validated UTF-8 write from logical text content.</summary>
    public static MemoryWrite Create(
        MemoryAddress address,
        IReadOnlyList<string?>? content,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? attributes,
        IReadOnlyList<string>? keywords,
        MemoryValidationOptions? options = null,
        bool isAppend = false,
        string? description = null)
    {
        var result = TryCreate(address, content, attributes, keywords, options, isAppend, description);
        if (result.Write is not null)
            return result.Write;

        throw new InvalidMemoryWriteException(result.Error!);
    }

    /// <summary>Validates this write and throws the established write exception on refusal.</summary>
    public void ValidateOrThrow(MemoryValidationOptions? options = null, bool isAppend = false)
    {
        var error = Validate(Address, RawContent, Attributes, Keywords, options ?? new MemoryValidationOptions(), isAppend);
        if (error is not null)
            throw new InvalidMemoryWriteException(error);
    }

    /// <summary>Validates a write without invoking a store or plugin.</summary>
    public static MemoryError? Validate(
        MemoryAddress address,
        IReadOnlyList<string?>? content,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? attributes,
        IReadOnlyList<string>? keywords,
        MemoryValidationOptions options,
        bool isAppend = false)
    {
        if (!address.IsValid)
            return Error("invalid_address", "Address is invalid.", "address");
        if (address.Kind == MemoryAddressKind.Persistent)
        {
            int sanitizedSubjectLength = MemoryNaming.SanitizeSegment(
                address.Value[(address.Value.LastIndexOf('/') + 1)..]).Length;
            if (sanitizedSubjectLength > MaxPersistentSubjectLength)
                return new MemoryError(
                    MemoryErrorCategory.InvalidWriteContent,
                    "persistent_subject_too_long",
                    $"Persistent memory subject exceeds {MaxPersistentSubjectLength} sanitized characters.",
                    "address", MaxPersistentSubjectLength, sanitizedSubjectLength);
        }
        int addressBytes = System.Text.Encoding.UTF8.GetByteCount(address.ToString());
        if (addressBytes > options.MaxAddressBytes)
            return Error("address_too_large", "Address exceeds its UTF-8 byte limit.", "address", options.MaxAddressBytes, addressBytes);
        if (content is null || content.Count == 0)
            return Error("empty_content", "Content must contain at least one chunk.", "content", 1, 0);
        if (attributes is null)
            return Error("attributes_null", "Attributes must not be null.", "attributes");
        if (keywords is null)
            return Error("keywords_null", "Keywords must not be null.", "keywords");
        if (isAppend && content.Count != 1)
            return Error("append_requires_one_chunk", "An append must contain exactly one chunk.", "content", 1, content.Count);
        if (content.Count > options.MaxChunks)
            return Error("too_many_chunks", "Content contains too many chunks.", "content", options.MaxChunks, content.Count);

        long total = 0;
        for (int index = 0; index < content.Count; index++)
        {
            string? chunk = content[index];
            if (chunk is null)
                return Error("null_content_chunk", "Content chunks must not be null.", $"content[{index}]");
            int bytes = System.Text.Encoding.UTF8.GetByteCount(chunk);
            if (bytes > options.MaxContentBytesPerChunk)
                return Error("content_chunk_too_large", "Content chunk exceeds its UTF-8 byte limit.", $"content[{index}]", options.MaxContentBytesPerChunk, bytes);
            int encoded = System.Text.Encoding.UTF8.GetByteCount(Nmp2ChunkedEncoder.Encode(chunk));
            if (encoded > options.MaxEncodedChunkBytes)
                return Error("encoded_chunk_too_large", "Encoded content chunk exceeds its byte limit.", $"content[{index}].encoded", options.MaxEncodedChunkBytes, encoded);
            total += bytes;
            if (total > options.MaxTotalContentBytes)
                return Error("content_total_too_large", "Total content exceeds its UTF-8 byte limit.", "content", options.MaxTotalContentBytes, total);
        }

        foreach (var attribute in attributes ?? EmptyAttributes)
        {
            if (string.IsNullOrWhiteSpace(attribute.Key) || attribute.Key.Length > options.MaxAttributeKeyLength)
                return Error("attribute_key_invalid", "Attribute key is invalid or too long.", "attributes.key", options.MaxAttributeKeyLength, attribute.Key?.Length ?? 0);
            if (attribute.Value is null)
                return Error("attribute_values_null", "Attribute values must not be null.", $"attributes[{attribute.Key}]");
            for (int index = 0; index < attribute.Value.Count; index++)
                if (string.IsNullOrWhiteSpace(attribute.Value[index]) || attribute.Value[index].Length > options.MaxAttributeValueLength)
                    return Error("attribute_value_invalid", "Attribute value is invalid or too long.", $"attributes[{attribute.Key}][{index}]", options.MaxAttributeValueLength, attribute.Value[index]?.Length ?? 0);
        }
        for (int index = 0; index < (keywords?.Count ?? 0); index++)
            if (keywords![index] is null || string.IsNullOrWhiteSpace(keywords[index]) || keywords[index].Length > options.MaxKeywordLength)
                return Error("keyword_invalid", "Keyword is invalid or too long.", $"keywords[{index}]", options.MaxKeywordLength, keywords[index]?.Length ?? 0);
        return null;
    }

    private static MemoryError Error(string code, string message, string field, long? limit = null, long? actual = null) =>
        new(MemoryErrorCategory.InvalidWriteContent, code, message, field, limit, actual);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyAttributes =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Rejects a persistent subject that cannot produce every durable filename.</summary>
    public static void ValidatePersistentSubject(string name)
    {
        if (MemoryNaming.IsEphemeralName(name)) return;

        int sanitizedSubjectLength = MemoryNaming.SanitizeSegment(name).Length;
        if (sanitizedSubjectLength > MaxPersistentSubjectLength)
            throw new InvalidMemoryWriteException(MaxPersistentSubjectLength, sanitizedSubjectLength);
    }

    /// <summary>Gets or initializes the resolved memory subject.</summary>
    public MemoryAddress Address { get; }
    public string Name { get; }

    /// <summary>Gets or initializes the resolved memory scope.</summary>
    public string Scope { get; }

    /// <summary>Gets or initializes the raw content chunks.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Content { get; }

    /// <summary>Text chunks retained solely for the existing derivation pipeline.</summary>
    internal string[] RawContent { get; }

    /// <summary>Gets or initializes the optional curated description.</summary>
    public string? Description { get; }

    /// <summary>Gets or initializes explicit caller attributes.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes { get; }

    /// <summary>Gets or initializes raw caller keywords.</summary>
    public IReadOnlyList<string> Keywords { get; }

}

/// <summary>The result of validating a write at the core boundary.</summary>
public sealed record MemoryWriteValidationResult(MemoryWrite? Write, MemoryError? Error)
{
    public bool IsValid => Write is not null && Error is null;
}
