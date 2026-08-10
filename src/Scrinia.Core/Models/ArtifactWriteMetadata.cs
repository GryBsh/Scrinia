namespace Scrinia.Core.Models;

/// <summary>
/// Metadata that must accompany an artifact write across a process boundary.
///
/// <para>This exists for exactly one reason: a remote store cannot persist an entry itself. The
/// local store receives its metadata through <c>Upsert</c> after the write and ignores this
/// parameter entirely; the remote store has no such second channel — its <c>Upsert</c> is a
/// no-op — so without this the caller's attributes, keywords and curated description were
/// assembled and then dropped on the floor.</para>
///
/// <para>Deliberately not the whole <see cref="ArtifactEntry"/>: the fields a server derives for
/// itself (chunk counts, byte counts, timestamps, term frequencies) must not be client-supplied,
/// and sending them would invite a caller to think they are authoritative.</para>
/// </summary>
/// <param name="Attributes">The agent-supplied attributes map — entities, anchors, file refs,
/// tag and review keys. The whole map, not a flattened projection of it.</param>
/// <param name="Keywords">Merged agent-supplied and auto-extracted keywords.</param>
/// <param name="Description">Curated content preview, when the caller supplied one. Wins over
/// the auto-generated snippet.</param>
public sealed record ArtifactWriteMetadata(
    IReadOnlyDictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null,
    string? Description = null);
