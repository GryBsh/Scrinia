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
        ValidatePersistentSubject(Name);

        this.Name = Name;
        this.Scope = Scope;
        this.Content = Content;
        this.Description = Description;
        this.Attributes = Attributes;
        this.Keywords = Keywords;
    }

    /// <summary>Rejects a persistent subject that cannot produce every durable filename.</summary>
    public static void ValidatePersistentSubject(string name)
    {
        if (MemoryNaming.IsEphemeralName(name)) return;

        int sanitizedSubjectLength = MemoryNaming.SanitizeSegment(name).Length;
        if (sanitizedSubjectLength > MaxPersistentSubjectLength)
            throw new InvalidMemoryWriteException(MaxPersistentSubjectLength, sanitizedSubjectLength);
    }

    /// <summary>Gets or initializes the resolved memory subject.</summary>
    public string Name { get; init; }

    /// <summary>Gets or initializes the resolved memory scope.</summary>
    public string Scope { get; init; }

    /// <summary>Gets or initializes the raw content chunks.</summary>
    public string[] Content { get; init; }

    /// <summary>Gets or initializes the optional curated description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets or initializes explicit caller attributes.</summary>
    public Dictionary<string, string[]>? Attributes { get; init; }

    /// <summary>Gets or initializes raw caller keywords.</summary>
    public string[]? Keywords { get; init; }

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
        content = Content;
        description = Description;
        attributes = Attributes;
        keywords = Keywords;
    }
}
