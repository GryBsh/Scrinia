using System.Text;
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
    /// <summary>Initializes a categorized write-policy refusal.</summary>
    public InvalidMemoryWriteException(MemoryError error)
        : base(error.Message, error.Field)
    {
        Error = error;
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

    /// <summary>Initializes the raw write input.</summary>
    public MemoryWrite(
        string Name,
        string Scope,
        string[] Content,
        string? Description = null,
        Dictionary<string, string[]>? Attributes = null,
        string[]? Keywords = null)
    {
        ArgumentNullException.ThrowIfNull(Content);
        Address = MemoryAddress.Parse(Name);
        this.Name = Name;
        this.Scope = Scope;
        RawContent = Content;
        this.Content = Content.Select(System.Text.Encoding.UTF8.GetBytes).Select(bytes => (ReadOnlyMemory<byte>)bytes).ToArray();
        this.Description = Description;
        this.Attributes = Attributes?.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value) ?? EmptyAttributes;
        this.Keywords = Keywords ?? [];
    }

    private MemoryWrite(MemoryAddress address, IReadOnlyList<ReadOnlyMemory<byte>> content,
        IReadOnlyDictionary<string, IReadOnlyList<string>> attributes, IReadOnlyList<string> keywords,
        string? description, string[] rawContent)
    {
        Address = address;
        Content = content;
        Attributes = attributes;
        Keywords = keywords;
        Description = description;
        RawContent = rawContent;
        Name = address.ToString();
        Scope = "local";
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
            attributes ?? EmptyAttributes, keywords ?? Array.Empty<string>(), description, rawContent), null);
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
        int addressBytes = System.Text.Encoding.UTF8.GetByteCount(address.ToString());
        if (addressBytes > options.MaxAddressBytes)
            return Error("address_too_large", "Address exceeds its UTF-8 byte limit.", "address", options.MaxAddressBytes, addressBytes);
        if (content is null || content.Count == 0)
            return Error("empty_content", "Content must contain at least one chunk.", "content", 1, 0);
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
            for (int index = 0; index < attribute.Value.Count; index++)
                if (string.IsNullOrWhiteSpace(attribute.Value[index]) || attribute.Value[index].Length > options.MaxAttributeValueLength)
                    return Error("attribute_value_invalid", "Attribute value is invalid or too long.", $"attributes[{attribute.Key}][{index}]", options.MaxAttributeValueLength, attribute.Value[index]?.Length ?? 0);
        }
        for (int index = 0; index < (keywords?.Count ?? 0); index++)
            if (string.IsNullOrWhiteSpace(keywords![index]) || keywords[index].Length > options.MaxKeywordLength)
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
    public MemoryAddress Address { get; init; }
    public string Name { get; init; }

    /// <summary>Gets or initializes the resolved memory scope.</summary>
    public string Scope { get; init; }

    /// <summary>Gets or initializes the raw content chunks.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Content { get; init; }

    /// <summary>Text chunks retained solely for the existing derivation pipeline.</summary>
    public string[] RawContent { get; init; }

    /// <summary>Gets or initializes the optional curated description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets or initializes explicit caller attributes.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes { get; init; }

    /// <summary>Gets or initializes raw caller keywords.</summary>
    public IReadOnlyList<string> Keywords { get; init; }

    /// <summary>Deconstructs the write input into its public constructor values.</summary>
    public void Deconstruct(
        out string name,
        out string scope,
        out string[] content,
        out string? description,
        out Dictionary<string, string[]>? attributes,
        out string[]? keywords)
    {
        name = Name;
        scope = Scope;
        content = RawContent;
        description = Description;
        attributes = Attributes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        keywords = Keywords.ToArray();
    }
}

/// <summary>The result of validating a write at the core boundary.</summary>
public sealed record MemoryWriteValidationResult(MemoryWrite? Write, MemoryError? Error)
{
    public bool IsValid => Write is not null && Error is null;
}
