namespace Scrinia.Core.Models;

/// <summary>
/// The raw inputs a store derives a memory from — and deliberately nothing the store computes for
/// itself. There is no <c>TermFrequencies</c>, no <c>ChunkEntries</c>, no <c>ContentPreview</c> and
/// no <c>FileHashes</c> on this record, because a caller that could supply them would be deriving.
///
/// <para><b>Raw in, derived out (D-01/D-29).</b> <see cref="Keywords"/> is the CALLER's own keyword
/// list. Handing back a list a store previously derived reclassifies every auto-extracted term as
/// agent-supplied and boosts it +5 instead of +2 — the mistake
/// <c>HttpMemoryStore.WriteArtifactAsync</c> used to make by sending MCP's already-merged keywords
/// over the wire, before it was cut over to sending only <c>StoreApiRequest([text], qualifiedName)</c>
/// (see <c>CHANGELOG.md</c>'s "remote writes silently discarded the agent's metadata" entry). The
/// shape of this record is what makes that mistake hard to express: there is no
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
public sealed record MemoryWrite(
    string Name,
    string Scope,
    string[] Content,
    string? Description = null,
    Dictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null);
