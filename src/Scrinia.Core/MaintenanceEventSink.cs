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
/// <para>Entries written before this carry the legacy <c>orphan</c> KEYWORD. Marking keys off the
/// attribute alone, so a legacy orphan migrates onto the new carrier; clearing removes both, so a
/// stale keyword goes once the entry gains an inbound reference. An earlier version of this change
/// left the keyword entirely alone as "inert stale noise" — it is not inert: a bare <c>orphan</c>
/// query scores the keyword form higher than a genuine attribute-era orphan
/// (<c>WeightedAttributeScorer.ScoreEntryTerm</c> matches attribute VALUES, never keys), so an entry
/// with demonstrable inbound refs outranked the real orphans.</para>
///
/// <para><b>The clear is two writes, not one.</b> Because marking leaves the keyword alone,
/// dual-marked entries are the normal migration outcome, so the clear is the common path rather than
/// a rare one. Bundling the keyword removal into the same mutation as the attribute removal made a
/// remote store refuse BOTH — <c>PATCH</c> rejects a <c>Keywords</c> change — leaving a non-orphan
/// marked <c>orphan:true</c> indefinitely, worse than the behaviour it replaced. The attribute is
/// cleared first and the keyword attempted separately, best-effort, so the remote floor is the
/// attribute always clearing and only the legacy keyword lingering.</para>
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

    /// <summary>True when the source entry still carries the pre-attribute <c>orphan</c> KEYWORD.
    /// Runs its own lazy <see cref="IMemoryStore.AnyScoped"/> scan — short-circuiting at the first
    /// matching row — rather than querying a listing materialized elsewhere, so a store with no
    /// legacy entries never pays for a full scan to discover it has nothing to clean up; the write
    /// that follows re-checks against its own freshly-read entry.</summary>
    private static bool CarriesLegacyOrphanKeyword(IMemoryStore store, string scope, string subject) =>
        store.AnyScoped(null, sa =>
            string.Equals(sa.Scope, scope, StringComparison.OrdinalIgnoreCase)
            && store.SanitizeName(sa.Entry.Name)
                .Equals(store.SanitizeName(subject), StringComparison.OrdinalIgnoreCase)
            && sa.Entry.Keywords?.Any(k => k.Equals(OrphanKey, StringComparison.OrdinalIgnoreCase)) == true);

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
            // entry once, atomically, instead of the old racy load → mutate → Upsert. AnyScoped
            // instead of ListScoped(null).Any(...): a lazy scan that stops at the first matching
            // referrer, rather than materializing every entry in the store to answer one boolean.
            bool hasInboundRefs = store.AnyScoped(null, sa =>
                (sa.Entry.Keywords?.Any(k => k.Equals(inboundRefKey, StringComparison.OrdinalIgnoreCase)) == true)
                || (sa.Entry.Attributes is not null
                    && sa.Entry.Attributes.TryGetValue("ref", out var refs)
                    && refs.Any(v => v.Equals(sourceQualified, StringComparison.OrdinalIgnoreCase))));

            // ── Write 1: the attribute, which is the marker on both backends ──────────
            //
            // Deliberately does NOT touch Keywords, and that separation is the point. Marking
            // leaves a legacy entry's keyword in place, so dual-marked entries are the NORMAL
            // migration outcome — which means the clear below is the common case, not a rare one.
            // Bundling the keyword removal into this same mutation made a remote store refuse the
            // whole thing (PATCH rejects a Keywords change), so NEITHER carrier was cleared and a
            // non-orphan kept `orphan:true` forever, retrying and warning on every later store
            // event. Splitting the writes restores the floor: the attribute clears remotely even
            // when the keyword cannot.
            bool attributeWriteAttempted = false;
            bool markerDroppedByTheKeyCap = false;
            bool attributeWritePersisted = store.TryUpdateEntry(sourceSubject, sourceScope, sourceEntry =>
            {
                bool markedByAttribute = sourceEntry.Attributes is not null
                    && sourceEntry.Attributes.ContainsKey(OrphanKey);

                if (!hasInboundRefs && !markedByAttribute)
                {
                    attributeWriteAttempted = true;
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

                if (hasInboundRefs && markedByAttribute)
                {
                    attributeWriteAttempted = true;
                    var cleared = new Dictionary<string, string[]>(
                        sourceEntry.Attributes!, StringComparer.OrdinalIgnoreCase);
                    cleared.Remove(OrphanKey);
                    return sourceEntry with { Attributes = cleared.Count > 0 ? cleared : null };
                }
                return null; // nothing to change
            });

            // ── Write 2: the legacy keyword carrier, best-effort ──────────────────────
            //
            // Only ever a REMOVAL, only when the entry has inbound refs, and only for an entry that
            // actually carries the pre-attribute marker. Leaving it is not inert:
            // WeightedAttributeScorer.ScoreEntryTerm matches attribute VALUES and never keys, so a
            // stale "orphan" keyword scores above a genuine attribute-era orphan on a bare "orphan"
            // query — an entry with demonstrable inbound refs outranking the real ones.
            //
            // Gated on hasInboundRefs, not on a listing shared with the check above: each question
            // now runs its own lazy AnyScoped pass rather than both querying one materialized
            // List<ScopedArtifact>. A store with no inbound refs never starts this second scan at
            // all, and when it does run, it short-circuits at the first matching row exactly like
            // the pass above — two lazy scans, gated so the second runs only when it can matter,
            // beat one full listing. The callback re-checks against the freshly-read entry, so the
            // gate is an optimization and not the correctness condition.
            bool keywordWriteAttempted = false;
            bool keywordWritePersisted = true;
            if (hasInboundRefs && CarriesLegacyOrphanKeyword(store, sourceScope, sourceSubject))
            {
                keywordWritePersisted = store.TryUpdateEntry(sourceSubject, sourceScope, sourceEntry =>
                {
                    if (sourceEntry.Keywords is null
                        || !sourceEntry.Keywords.Any(k => k.Equals(OrphanKey, StringComparison.OrdinalIgnoreCase)))
                        return null;

                    keywordWriteAttempted = true;
                    return sourceEntry with
                    {
                        Keywords = sourceEntry.Keywords
                            .Where(k => !k.Equals(OrphanKey, StringComparison.OrdinalIgnoreCase))
                            .ToArray(),
                    };
                });
            }

            // A refusal must not be silent. Everything else this sink does is best-effort by
            // design, but "the store would not take the write" is a different thing from "there
            // was nothing to write", and the two used to be indistinguishable because the result
            // was discarded. Same channel this class already reports its own failures on. The two
            // writes report separately: a refused keyword clean-up on a remote store is expected
            // (PATCH will not take a Keywords change) and must not read as the attribute having
            // failed, which is the far more serious condition.
            if (markerDroppedByTheKeyCap)
                Console.Error.WriteLine(
                    $"[scrinia:warn] MaintenanceEventSink: '{sourceQualified}' already carries " +
                    $"{KnownAttributes.MaxAttributes} attribute keys, so its '{OrphanKey}' marker " +
                    "could not be added.");
            else if (attributeWriteAttempted && !attributeWritePersisted)
                Console.Error.WriteLine(
                    $"[scrinia:warn] MaintenanceEventSink: the store refused the orphan update for " +
                    $"'{sourceQualified}'. Its '{OrphanKey}' marker is now out of date.");

            if (keywordWriteAttempted && !keywordWritePersisted)
                Console.Error.WriteLine(
                    $"[scrinia:warn] MaintenanceEventSink: the store refused to remove the legacy " +
                    $"'{OrphanKey}' keyword from '{sourceQualified}'. Its '{OrphanKey}' attribute is " +
                    "correct; the stale keyword remains until the memory is re-stored.");
        }
        catch
        {
            // orphan detection is best-effort — silently skip
        }

        return Task.CompletedTask;
    }
}
