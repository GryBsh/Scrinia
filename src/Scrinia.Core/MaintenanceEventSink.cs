using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Automatic memory maintenance: creates bidirectional ref links (via <c>Attributes["ref"]</c>)
/// when content references other memories, and flags orphan memories with no inbound
/// references (dual-reading legacy <c>ref:</c> keywords alongside the ref attribute).
///
/// <para><b>The orphan marker is an ATTRIBUTE, not a keyword.</b> It used to be the bare keyword
/// <c>"orphan"</c>, which made this sink unable to do half its job against a remote store:
/// <c>PATCH /memories/{name}</c> refuses a body that changes <c>Keywords</c> — deliberately, and
/// that refusal stays, because keywords are derived from a memory's CONTENT and a metadata
/// endpoint has none to re-derive from — so every orphan write over HTTP came back 400 and was
/// discarded, while the identical local write succeeded. Same tool, same input, two corpora that
/// disagree, with nothing to explain why.</para>
///
/// <para>Orphan-ness is not content-derived; it is a corpus-level fact about inbound references,
/// and it was only ever in the keyword list because that list happened to be searchable. As an
/// attribute it is caller-owned metadata that <c>PATCH</c> accepts, so local and remote now behave
/// identically — and it gains an <c>attributeFilter</c> the magic keyword never had. It stays
/// searchable by the word "orphan" because attribute keys and values seed the term-frequency index
/// (D-08), which a metadata update now re-derives (<see cref="MemoryDerivation.ForMetadataUpdate"/>).</para>
/// <para>Entries written before this carry the legacy <c>orphan</c> KEYWORD, and it is dual-read for
/// CLEARING but not for marking. Marking keys off the attribute alone, so a legacy orphan migrates
/// onto the new carrier; clearing keys off either, so a stale keyword is removed once the entry
/// gains an inbound reference. An earlier version of this change left the keyword entirely alone as
/// "inert stale noise" — it is not inert: a bare <c>orphan</c> query scores the keyword form higher
/// than a genuine attribute-era orphan (<c>WeightedAttributeScorer.ScoreEntryTerm</c> matches
/// attribute VALUES, never keys), so an entry with demonstrable inbound refs outranked the real
/// orphans. The clearing branch is the only place this class writes <c>Keywords</c>, and only for a
/// legacy entry; a remote store refuses it, which is reported rather than swallowed.</para>
///
/// <para>The marker is a <see cref="KnownAttributes.StateMarkerKeys">state marker</see>, so it is
/// excluded from the entity graph: every entry sharing one boolean value would otherwise form a
/// hub node linking every unrelated orphan to every other. See that set for the measurements.</para>
/// </summary>
public sealed class MaintenanceEventSink : IMemoryEventSink
{
    /// <summary>Attribute key carrying the orphan marker. Scalar (not in
    /// <c>KnownAttributes</c>'s multi-valued set), so marking replaces rather than accumulates.
    /// Defined at the schema point, not here: this sink writes the marker but does not own what the
    /// key means, and <see cref="KnownAttributes.StateMarkerKeys"/> has to name it too.</summary>
    private const string OrphanKey = KnownAttributes.OrphanKey;

    /// <summary>The marker's only value. Presence of the key is what makes an entry an orphan; the
    /// value exists so the attribute has something to match in an <c>attributeFilter</c>.</summary>
    internal const string OrphanValue = "true";

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        try
        {
            string joined = string.Join("\n", content);
            await AutoLinkAndDetectOrphans(qualifiedName, joined, store);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scrinia:warn] MaintenanceEventSink error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
    {
        try
        {
            await AutoLinkAndDetectOrphans(qualifiedName, content, store);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scrinia:warn] MaintenanceEventSink error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
        => Task.CompletedTask; // no-op for now

    private static Task AutoLinkAndDetectOrphans(string qualifiedName, string content, IMemoryStore store)
    {
        // 1. Extract memory references from content
        var memoryRefs = ReferenceExtractor.ExtractMemoryRefs(content);

        // 2. For each referenced memory that exists, add the source to Attributes["ref"] on the target
        foreach (var refName in memoryRefs)
        {
            // Skip self-references
            if (refName.Equals(qualifiedName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var (targetScope, targetSubject) = store.ParseQualifiedName(refName);

                // TryUpdateEntry, not LoadIndex → mutate → Upsert: remotely Upsert is a no-op, so
                // that shape discarded the link silently; TryUpdateEntry fails closed there and runs
                // under the per-scope write lock locally. Doing the "already linked?" check inside
                // the callback also keeps this to ONE index read — hoisting it back out would cost a
                // second network round trip per referenced memory on a remote store.
                store.TryUpdateEntry(targetSubject, targetScope, targetEntry =>
                {
                    // Write the ref attribute (unified attributes) — no more ref: keyword emission.
                    string[] existingRefs = targetEntry.Attributes is not null
                        && targetEntry.Attributes.TryGetValue("ref", out var r) ? r : [];
                    if (existingRefs.Contains(qualifiedName, StringComparer.OrdinalIgnoreCase)
                        || targetEntry.Keywords?.Contains($"ref:{qualifiedName}", StringComparer.OrdinalIgnoreCase) == true)
                        return null; // already linked (either shape) — no write

                    var updatedAttributes = KnownAttributes.Merge(
                        targetEntry.Attributes, new() { ["ref"] = [qualifiedName] });
                    return targetEntry with { Attributes = updatedAttributes };
                });
            }
            catch
            {
                // target scope may not exist — silently skip
            }
        }

        // 3. Orphan detection — check if this entry has inbound refs
        try
        {
            var (sourceScope, sourceSubject) = store.ParseQualifiedName(qualifiedName);

            string sourceQualified = store.FormatQualifiedName(sourceScope, sourceSubject);
            string inboundRefKey = $"ref:{sourceQualified}";

            // Computed before the update rather than after a separate LoadIndex: neither this nor
            // the qualified name needs the source entry itself, and TryUpdateEntry below reads the
            // entry once, atomically, instead of the old racy load → mutate → Upsert.
            var allEntries = store.ListScoped(null);
            bool hasInboundRefs = allEntries.Any(sa =>
                (sa.Entry.Keywords?.Any(k => k.Equals(inboundRefKey, StringComparison.OrdinalIgnoreCase)) == true)
                || (sa.Entry.Attributes is not null
                    && sa.Entry.Attributes.TryGetValue("ref", out var refs)
                    && refs.Any(v => v.Equals(sourceQualified, StringComparison.OrdinalIgnoreCase))));

            bool orphanWriteAttempted = false;
            bool markerDroppedByTheKeyCap = false;
            bool orphanWritePersisted = store.TryUpdateEntry(sourceSubject, sourceScope, sourceEntry =>
            {
                bool markedByAttribute = sourceEntry.Attributes is not null
                    && sourceEntry.Attributes.ContainsKey(OrphanKey);
                // Read-only dual-read of the legacy carrier. Marking below keys off the ATTRIBUTE
                // alone so a legacy orphan migrates onto it; clearing keys off either, so a stale
                // legacy keyword is removed rather than left to outrank the truth.
                bool markedByLegacyKeyword = sourceEntry.Keywords?
                    .Any(k => k.Equals(OrphanKey, StringComparison.OrdinalIgnoreCase)) == true;

                if (!hasInboundRefs && !markedByAttribute)
                {
                    orphanWriteAttempted = true;
                    var merged = KnownAttributes.Merge(
                        sourceEntry.Attributes, new() { [OrphanKey] = [OrphanValue] });

                    // KnownAttributes.Merge caps at MaxAttributes and truncates past it with NO
                    // signal, so an entry already at the cap loses the marker while TryUpdateEntry
                    // still reports success. Detect it and decline the write: persisting an entry
                    // whose marker was silently dropped buys nothing and reads as a success.
                    if (merged is null || !merged.ContainsKey(OrphanKey))
                    {
                        markerDroppedByTheKeyCap = true;
                        return null;
                    }
                    return sourceEntry with { Attributes = merged };
                }

                if (hasInboundRefs && (markedByAttribute || markedByLegacyKeyword))
                {
                    orphanWriteAttempted = true;
                    var next = sourceEntry;

                    if (markedByAttribute)
                    {
                        var cleared = new Dictionary<string, string[]>(
                            sourceEntry.Attributes!, StringComparer.OrdinalIgnoreCase);
                        cleared.Remove(OrphanKey);
                        next = next with { Attributes = cleared.Count > 0 ? cleared : null };
                    }

                    // The legacy carrier is cleared too — restoring behaviour the move to an
                    // attribute dropped. Leaving it is not inert: a stale "orphan" keyword scores
                    // HIGHER on a bare "orphan" query than a genuine attribute-era orphan does
                    // (WeightedAttributeScorer.ScoreEntryTerm matches attribute values, never keys),
                    // so an entry that demonstrably has inbound refs outranks the real orphans.
                    // This is the ONLY branch that writes Keywords, and only for a legacy entry —
                    // a remote store's PATCH refuses a keyword change, so this is refused there and
                    // reported below rather than failing silently.
                    if (markedByLegacyKeyword)
                        next = next with
                        {
                            Keywords = sourceEntry.Keywords!
                                .Where(k => !k.Equals(OrphanKey, StringComparison.OrdinalIgnoreCase))
                                .ToArray(),
                        };

                    return next;
                }
                return null; // nothing to change
            });

            // A refusal must not be silent. Everything else this sink does is best-effort by
            // design, but "the store would not take the write" is a different thing from "there
            // was nothing to write", and the two used to be indistinguishable because the result
            // was discarded. Same channel this class already reports its own failures on.
            if (markerDroppedByTheKeyCap)
                Console.Error.WriteLine(
                    $"[scrinia:warn] MaintenanceEventSink: '{sourceQualified}' already carries " +
                    $"{KnownAttributes.MaxAttributes} attribute keys, so its '{OrphanKey}' marker " +
                    "could not be added.");
            else if (orphanWriteAttempted && !orphanWritePersisted)
                Console.Error.WriteLine(
                    $"[scrinia:warn] MaintenanceEventSink: the store refused the orphan update for " +
                    $"'{sourceQualified}'. Its '{OrphanKey}' marker is now out of date.");
        }
        catch
        {
            // orphan detection is best-effort — silently skip
        }

        return Task.CompletedTask;
    }
}
