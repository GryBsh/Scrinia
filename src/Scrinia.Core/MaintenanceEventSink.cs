using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Automatic memory maintenance: creates bidirectional ref links (via <c>Attributes["ref"]</c>)
/// when content references other memories, and flags orphan memories with no inbound
/// references (dual-reading legacy <c>ref:</c> keywords alongside the ref attribute).
///
/// <para><b>The orphan marker is an ATTRIBUTE, not a keyword.</b> It used to be the bare keyword
/// <c>"orphan"</c>. The defect that forced the change was a remote one — the metadata endpoint
/// refused a body that changed <c>Keywords</c>, so every orphan write over HTTP came back 400 while
/// the identical local write succeeded — and that endpoint is gone. The change stands on its own
/// terms anyway: orphan-ness is not content-derived, it is a corpus-level fact about inbound
/// references, and it was only ever in the keyword list because that list happened to be
/// searchable. As an attribute it gains an <c>attributeFilter</c> the magic keyword never had, and
/// it stays searchable by the word "orphan" because attribute keys and values seed the
/// term-frequency index (D-08), which a metadata update re-derives
/// (<see cref="MemoryDerivation.ForMetadataUpdate"/>).</para>
/// <para>Entries written before this carry the legacy <c>orphan</c> KEYWORD. Marking keys off the
/// attribute alone, so a legacy orphan migrates onto the new carrier; clearing removes both, so a
/// stale keyword goes once the entry gains an inbound reference. An earlier version of this change
/// left the keyword entirely alone as "inert stale noise" — it is not inert: a bare <c>orphan</c>
/// query scores the keyword form higher than a genuine attribute-era orphan
/// (<c>WeightedAttributeScorer.ScoreEntryTerm</c> matches attribute VALUES, never keys), so an entry
/// with demonstrable inbound refs outranked the real orphans.</para>
///
/// <para><b>The clear is one write.</b> It was two, and the reason was entirely remote: the
/// metadata endpoint refused a body that changed <c>Keywords</c>, so bundling the keyword removal
/// into the attribute's mutation made the server refuse BOTH and leave a non-orphan marked
/// <c>orphan:true</c> indefinitely. Splitting them guaranteed the attribute cleared even when the
/// keyword could not. With one store there is one sidecar and the entry is already in hand inside
/// the callback, so both carriers come off together — which matters because this is the COMMON
/// path, not a corner: marking deliberately leaves a legacy entry's keyword alone, so dual-marked
/// entries are the normal outcome of migration.</para>
///
/// <para>Merging them also retired the pre-gate that stood in front of the second write. It read
/// one scope's index to decide whether a legacy keyword was there at all, which was worth doing
/// when the alternative was a wasted lock acquisition and entry read; inside a callback that
/// already holds the entry, the same question is a field access.</para>
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

    public async Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
    {
        try
        {
            var full = CoreSinkStore.Require(store, nameof(MaintenanceEventSink));
            string joined = string.Join("\n", content);
            await AutoLinkAndDetectOrphans(address, joined, full);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scrinia:warn] MaintenanceEventSink error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
    {
        try
        {
            var full = CoreSinkStore.Require(store, nameof(MaintenanceEventSink));
            await AutoLinkAndDetectOrphans(address, content, full);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scrinia:warn] MaintenanceEventSink error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
        => Task.CompletedTask; // no-op for now

    private static Task AutoLinkAndDetectOrphans(MemoryAddress address, string content, IMemoryStore store)
    {
        string qualifiedName = address.ToString();
        // 1. Extract memory references from content
        var memoryRefs = ReferenceExtractor.ExtractMemoryRefs(content);

        // 2. For each referenced memory that exists, add the source to Attributes["ref"] on the target
        foreach (var refName in memoryRefs)
        {
            if (!TryParseReferenceAddress(refName, out var referencedAddress))
                continue;

            // Skip self-references by canonical logical identity. Surface aliases such as
            // /temp/probe and ~probe must never create a self-link.
            if (referencedAddress == address)
                continue;

            try
            {
                var (targetScope, targetSubject) = store.ResolveAddress(referencedAddress);

                // TryUpdateEntry, not LoadIndex → mutate → Upsert: it runs under the per-scope
                // write lock, and doing the "already linked?" check inside the callback keeps this
                // to ONE index read. (The shape it replaced also discarded the link silently on the
                // remote store, where Upsert was a no-op.)
                ArtifactEntry? AddReference(ArtifactEntry targetEntry)
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
                }

                if (referencedAddress.Kind == MemoryAddressKind.Ephemeral)
                {
                    store.TryUpdateEphemeral(referencedAddress, current =>
                    {
                        var updated = AddReference(current.ToArtifactEntry());
                        return updated is null ? null : current.WithMetadataFrom(updated);
                    });
                }
                else
                {
                    store.TryUpdateEntry(referencedAddress, AddReference);
                }
            }
            catch
            {
                // target scope may not exist — silently skip
            }
        }

        // 3. Orphan detection — check if this entry has inbound refs
        try
        {
            var (sourceScope, sourceSubject) = store.ResolveAddress(address);

            string sourceQualified = store.FormatQualifiedName(sourceScope, sourceSubject);
            string inboundRefKey = $"ref:{sourceQualified}";

            // Computed before the update rather than after a separate LoadIndex: neither this nor
            // the qualified name needs the source entry itself, and TryUpdateEntry below reads the
            // entry once, atomically, instead of the old racy load → mutate → Upsert. AnyScoped
            // instead of ListScoped(null).Any(...): this genuinely is a whole-store question, and
            // FileMemoryStore's override makes it a lazy scan stopping at the first matching
            // referrer rather than a materialized listing of every entry. A store that does not
            // override it still answers correctly, just without the short-circuit — see
            // IMemoryStore.AnyScoped.
            bool hasInboundRefs = store.AnyScoped(null, sa =>
                (sa.Entry.Keywords?.Any(k => k.Equals(inboundRefKey, StringComparison.OrdinalIgnoreCase)) == true)
                || (sa.Entry.Attributes is not null
                    && sa.Entry.Attributes.TryGetValue("ref", out var refs)
                    && refs.Any(v => v.Equals(sourceQualified, StringComparison.OrdinalIgnoreCase))));

            // ── One write, covering both carriers ─────────────────────────────────────
            //
            // MARKING adds the attribute and deliberately does NOT touch Keywords: an entry that
            // still qualifies keeps the spelling every pre-migration entry uses, so it stays
            // findable while it migrates onto the new carrier. That is what makes the dual-marked
            // state the normal outcome, and therefore what makes CLEARING the common path.
            //
            // CLEARING removes whichever carriers are present, together. It used to be two writes
            // because a remote PATCH refused Keywords changes; see the type summary.
            bool writeAttempted = false;
            bool markerDroppedByTheKeyCap = false;
            bool writePersisted = store.TryUpdateEntry(address, sourceEntry =>
            {
                bool markedByAttribute = sourceEntry.Attributes is not null
                    && sourceEntry.Attributes.ContainsKey(OrphanKey);

                if (!hasInboundRefs)
                {
                    if (markedByAttribute) return null; // already marked — nothing to change

                    writeAttempted = true;
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

                // Has inbound refs: strip every marker carrier the entry still holds. Both
                // conditions are checked against THIS entry rather than a gate computed outside,
                // so the decision is made against the value about to be written.
                bool carriesLegacyKeyword = sourceEntry.Keywords is not null
                    && sourceEntry.Keywords.Any(k => k.Equals(OrphanKey, StringComparison.OrdinalIgnoreCase));

                if (!markedByAttribute && !carriesLegacyKeyword)
                    return null; // not marked either way — nothing to change

                writeAttempted = true;
                var updated = sourceEntry;

                if (markedByAttribute)
                {
                    var cleared = new Dictionary<string, string[]>(
                        sourceEntry.Attributes!, StringComparer.OrdinalIgnoreCase);
                    cleared.Remove(OrphanKey);
                    updated = updated with { Attributes = cleared.Count > 0 ? cleared : null };
                }

                if (carriesLegacyKeyword)
                {
                    // Only the marker goes. Leaving it is not inert:
                    // WeightedAttributeScorer.ScoreEntryTerm matches attribute VALUES and never
                    // keys, so a stale "orphan" keyword scores above a genuine attribute-era orphan
                    // on a bare "orphan" query — an entry with demonstrable inbound refs
                    // outranking the real ones.
                    updated = updated with
                    {
                        Keywords = [.. sourceEntry.Keywords!.Where(
                            k => !k.Equals(OrphanKey, StringComparison.OrdinalIgnoreCase))],
                    };
                }

                return updated;
            });

            // A refusal must not be silent. Everything else this sink does is best-effort by
            // design, but "the store would not take the write" is a different thing from "there
            // was nothing to write", and the two used to be indistinguishable because the result
            // was discarded. Same channel this class already reports its own failures on.
            //
            // One message now, because there is one write. There were two, reporting separately,
            // from when a refused keyword clean-up was the EXPECTED remote outcome and must not
            // read as the attribute having failed. With one store a refusal is a refusal, and it
            // leaves every carrier the entry had exactly as it was.
            if (markerDroppedByTheKeyCap)
                Console.Error.WriteLine(
                    $"[scrinia:warn] MaintenanceEventSink: '{sourceQualified}' already carries " +
                    $"{KnownAttributes.MaxAttributes} attribute keys, so its '{OrphanKey}' marker " +
                    "could not be added.");
            else if (writeAttempted && !writePersisted)
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

    private static bool TryParseReferenceAddress(string rawReference, out MemoryAddress address)
    {
        if (MemoryAddress.TryParse(rawReference, out address, out _))
            return true;

        return !string.IsNullOrEmpty(rawReference)
            && rawReference[0] is not ('/' or '~')
            && MemoryAddress.TryParse('/' + rawReference, out address, out _);
    }
}
