using System.Globalization;
using System.Text.RegularExpressions;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// The single derivation pipeline: raw caller inputs in, derived metadata out.
///
/// <para>It replaces four hand-rolled implementations of the same contract — MCP store
/// (<c>MemoryTools.Core.cs</c> <c>Remember</c>) and the append paths — which had drifted, in several
/// places far enough
/// to lose data. Every behavioural question this class answers was ruled in
/// <c>docs/superpowers/analysis/2026-08-11-derivation-inventory.md</c>; each ruling is cited by
/// its D-nn id at the line that implements it, so a later reader can trace behaviour back to the
/// decision rather than re-deriving it from four dead call sites.</para>
///
/// <para><b>Pure by construction.</b> No I/O, no store, no ambient state — that is what lets both
/// stores call it and what lets the tests exercise it directly. The one thing derivation needs
/// from the filesystem (drift-baseline hashes, D-04) arrives as a caller-supplied delegate rather
/// than as a file read taken here.</para>
///
/// <para><b>Raw in, derived out (D-01/D-29).</b> The keyword parameters carry the CALLER's raw
/// keywords. A derived keyword list must never be handed back in as input: doing so reclassifies
/// every auto-extracted term as agent-supplied and boosts it +5 instead of +2. That was the mistake
/// an older write path made by sending already-merged keywords instead of raw caller keywords, and
/// it is still the mistake the one compact would make by round-tripping its
/// stored keyword list. There
/// is deliberately no parameter on <see cref="ForStore"/> through which a previously-derived list
/// could enter. (<see cref="ForAppend"/>'s <see cref="ExistingMetadata.Keywords"/> is the one
/// carry-forward, and it is a ruled data-loss fix — D-12 — not a round-trip: an append that
/// dropped the memory's curated keywords would destroy them.)</para>
/// </summary>
public static partial class MemoryDerivation
{
    /// <summary>Agent-supplied (curated) keyword terms seed the BM25 index at this weight.</summary>
    private const int AgentKeywordBoost = 5;

    /// <summary>Auto-extracted content terms seed the BM25 index at this weight.</summary>
    private const int AutoKeywordBoost = 2;

    // Content whose claims are counts goes stale silently, so a count pattern auto-derives a
    // review condition (D-05 — kept when the two derivation paths were reconciled; the server's
    // never implemented it). Copied from
    // ScriniaMcpTools.CountPattern; that copy dies with the MCP derivation block it serves.
    [GeneratedRegex(@"\b\d+\s+(tests?|tools?|skills?|endpoints?|routes?|models?|memories)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex CountPattern();

    /// <summary>The review condition <see cref="ForStore"/> derives for count-shaped content (D-05).</summary>
    public const string CountReviewCondition = "when counts in this memory change";

    /// <summary>
    /// Derives metadata for a whole-memory write: store, re-store, or compact.
    /// </summary>
    /// <param name="content">The memory's chunks. One element per independently retrievable chunk.</param>
    /// <param name="description">A curated description. Wins over the auto snippet as the preview.</param>
    /// <param name="attributes">Explicit caller-supplied attributes. Fold in last; win scalar collisions.</param>
    /// <param name="keywords">The caller's RAW keywords — never a previously derived list (D-01).</param>
    /// <param name="isEphemeral">True for a <c>~name</c> / <c>/temp/</c> target; suppresses D-05.</param>
    /// <param name="fileHasher">
    /// Resolves a <c>file</c> attribute value to a content hash, or null when it cannot be hashed
    /// (missing file, no local workspace). Supplied by the store; null means no drift baseline is
    /// computed. This delegate is the whole of the pipeline's contact with the filesystem (D-04).
    /// </param>
    public static DerivedMetadata ForStore(
        string[] content,
        string? description = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        string[]? keywords = null,
        bool isEphemeral = false,
        Func<string, string?>? fileHasher = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        // The chunk join for text analysis is Concat, matching the artifact decoder: a multi-chunk
        // NMP/2 artifact decodes to the chunks concatenated with no separator, so this is the same
        // text an append later re-analyzes. D-02 changes the REF-extraction join only (below); it
        // explicitly does NOT change AnalyzeText/Extract/ExtractMentionSpans, because that would
        // re-tokenize every memory already stored.
        string joined = string.Concat(content);

        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);

        // Key:value-shaped agent keywords route to attributes; only the plain remainder takes part
        // in the keyword merge. `keywords` is the caller's raw list by contract (D-01).
        var (routedAttributes, plainKeywords) = AttributeExtractor.RouteKeywords(keywords);
        var (mergedKeywords, agentKeywordSet) =
            TextAnalysis.MergeKeywordsWithSource(plainKeywords, autoKeywords);
        BoostKeywords(mergedKeywords, agentKeywordSet, tf);

        // D-02: ref extraction reads the chunks joined by newline, not concatenated. Concat glues
        // the last token of chunk N to the first of chunk N+1, which can fabricate or destroy a
        // path match at a chunk boundary; a separator cannot.
        var refAttributes = AttributeExtractor.ExtractRefAttributes(string.Join("\n", content));

        var derived = KnownAttributes.Merge(
            KnownAttributes.Merge(
                KnownAttributes.Merge(routedAttributes, AttributeExtractor.Extract(joined)),
                refAttributes),
            AttributeExtractor.ExtractMentionSpans(joined));

        // Explicit agent attributes fold in last so they win scalar collisions. Under the one flat
        // attribute map, tags and anchors are ordinary keys here — there is no separate carrier to
        // re-fold afterwards (D-03, D-07).
        var attrs = KnownAttributes.Merge(derived, KnownAttributes.Normalize(attributes));

        // A typed "person:jolene" collides with a mention:jolene span from the same content; the
        // typed key wins. Unconditional — a no-op when nothing shadows (D-09).
        attrs = AttributeExtractor.DropMentionsShadowedByTypedKeys(attrs);

        // D-06: canonicalize `review`. A bare date is re-emitted in ISO-8601 round-trip form so a
        // Every write of "2026-01-01" persists the same value — `review` is scalar,
        // so the canonical form REPLACES the raw one in the carrier merge below.
        var (reviewAfter, reviewWhen) = SplitReview(attrs);

        // D-05: count-shaped content reviews itself when the counts change. Never for ephemerals,
        // and never over an explicit condition.
        if (string.IsNullOrWhiteSpace(reviewWhen) && !isEphemeral && CountPattern().IsMatch(joined))
            reviewWhen = CountReviewCondition;

    // D-04: the drift baseline is always computed by the workspace-backed store through its
    // caller-supplied hasher.
        var fileHashes = HashFileRefs(attrs, fileHasher);

        var carrier = AttributeMigration.BuildCarrierAttributes(
            tags: null, fileHashes?.Keys, reviewAfter,
            string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen, anchors: null);
        if (carrier.Count > 0)
            attrs = KnownAttributes.Merge(attrs, carrier);

        // D-08 — assemble the complete attribute map FIRST, then seed exactly ONCE as the last
        // step of attribute handling. A flat map has no separate carrier phase, so this ordering
        // keeps every attribute value searchable.
        // Deliberate, user-visible ranking change: tag/review/file 0 → +3, anchor +2 → +3.
        AttributeExtractor.SeedTermFrequencies(attrs, tf);

        // D-11 / D-23: one function produces chunk entries, and it guards the empty preview
        // (null, not "") so a sidecar round-trip cannot tell the paths apart.
        ChunkEntry[]? chunkEntries = content.Length > 1
            ? TextAnalysis.ComputeChunkEntries(content)
            : null;

        string preview = !string.IsNullOrWhiteSpace(description)
            ? description
            : ContentPreview.Generate(joined);

        // The provenance, recorded rather than left to be guessed at later. This is the one moment
        // it is known for free — `keywords` is the caller's own list by contract — and a later
        // content rewrite has no way to reconstruct it from the merged result (ArtifactEntry
        // .AgentKeywords). Key:value keywords are absent by construction: RouteKeywords sent them
        // to attributes before the merge, so they are not in `mergedKeywords` to be marked.
        return new DerivedMetadata(mergedKeywords, tf, attrs, chunkEntries, preview, fileHashes,
            AgentKeywords: [.. mergedKeywords.Where(agentKeywordSet.Contains)]);
    }

    /// <summary>
    /// Derives metadata for an append: the memory's full post-append content plus what the
    /// existing entry carries forward.
    /// </summary>
    /// <param name="fullContent">
    /// Every chunk after the append, the newly appended one LAST. The last element is the
    /// appended chunk — attribute extraction and the new chunk entry read only it (D-22).
    /// </param>
    /// <param name="description">A new curated description. Wins; else the existing preview (D-20).</param>
    /// <param name="attributes">Explicit caller-supplied attributes for this append (D-18).</param>
    /// <param name="keywords">The caller's RAW new keywords (D-19 routes the key:value ones).</param>
    /// <param name="existing">What the memory already carries. Null for an append onto nothing.</param>
    /// <param name="fileHasher">
    /// Resolves a <c>file</c> attribute value to a content hash, or null when it cannot be hashed.
    /// Supplied by the store; null means no new baseline is computed. Same delegate
    /// <see cref="ForStore"/> takes, and the pipeline's only contact with the filesystem (D-04).
    /// </param>
    public static DerivedMetadata ForAppend(
        string[] fullContent,
        string? description = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        string[]? keywords = null,
        ExistingMetadata? existing = null,
        Func<string, string?>? fileHasher = null)
    {
        ArgumentNullException.ThrowIfNull(fullContent);
        if (fullContent.Length == 0)
            throw new ArgumentException("An append derives from at least one chunk.", nameof(fullContent));

        // Concat, matching the decoded artifact this used to be handed as a single string.
        string joined = string.Concat(fullContent);
        string appended = fullContent[^1];

        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);

        var (routedNewAttributes, plainNewKeywords) = AttributeExtractor.RouteKeywords(keywords);

        // D-12 — a data-loss fix, not a ranking tweak. The memory's own
        // keywords plus the caller's additions lead the merged list, so nothing the memory carried
        // can be crowded out of it by an auto term; that is the whole of what D-12 guarantees.
        string[] carriedKeywords = UnionDistinct(existing?.Keywords, plainNewKeywords);
        var (mergedKeywords, _) = TextAnalysis.MergeKeywordsWithSource(
            carriedKeywords.Length > 0 ? carriedKeywords : null, autoKeywords);

        // The agent set is the RECORDED provenance plus this append's own keywords — not the whole
        // carried list. Leading the list and being agent-supplied are different facts, and
        // conflating them is what re-boosted every auto-extracted keyword to +5 on every append,
        // compounding: three appends made an incidental content term outrank a curated one. D-12's
        // membership guarantee is untouched by separating them; only the weight moves.
        //
        // UNKNOWN provenance (a pre-provenance entry) falls back to treating every carried keyword
        // as agent-supplied, which is exactly what this path did before the field existed. That
        // keeps an old entry's behaviour identical until something rewrites it, rather than
        // silently demoting a curated keyword the moment this code ships.
        var agentKeywordSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string kw in existing?.AgentKeywords ?? existing?.Keywords ?? [])
            if (!string.IsNullOrWhiteSpace(kw)) agentKeywordSet.Add(kw.Trim());
        foreach (string kw in plainNewKeywords ?? [])
            if (!string.IsNullOrWhiteSpace(kw)) agentKeywordSet.Add(kw.Trim());

        // D-13 (agent +5, auto +2) and D-14 (boost the INDEX TOKENS of the keyword, not the raw
        // string). D-14 only becomes observable once D-12 lands: before it, every append keyword
        // was AnalyzeText output — already tokenized and colon-free — so the two were the same
        // function. A carried-forward "person:jolene" is where they differ, and seeding the raw
        // string would put dead weight in the index that no tokenized query can ever match.
        BoostKeywords(mergedKeywords, agentKeywordSet, tf);

        // D-22: attribute extraction reads the APPENDED chunk only — the earlier chunks'
        // attributes are already on the entry. Mention spans are deliberately not re-extracted on
        // append; neither pre-collapse append path did, and doing so would grow the mention set on
        // every append of ordinary prose.
        var incoming = KnownAttributes.Merge(
            KnownAttributes.Merge(routedNewAttributes, AttributeExtractor.Extract(appended)),
            AttributeExtractor.ExtractRefAttributes(appended));
        // D-18: explicit caller attributes win over what the entry already holds.
        incoming = KnownAttributes.Merge(incoming, KnownAttributes.Normalize(attributes));

        var attrs = KnownAttributes.Merge(existing?.Attributes, incoming);

        // D-17: `tag` and `anchor` are SCALAR in KnownAttributes, so a plain merge would let a
        // caller-supplied tag on an append silently replace the memory's whole tag set. Store
        // replaces (deliberately — tags must stay clearable on a re-store) and append unions; the
        // asymmetry is real and lives here, as an append-path rule. The rejected alternative was
        // adding these keys to IsMultiValued, which would take the store's clear away.
        UnionCarriedKey(attrs, existing?.Attributes, incoming, KnownAttributes.TagKey);
        UnionCarriedKey(attrs, existing?.Attributes, incoming, KnownAttributes.AnchorKey);

        // D-21 — the baseline is carried forward, and its
        // path keys are re-folded into `file` so the searchable key never falls behind the hashes.
        // This adds to it: a file the appended chunk references for the first time gets hashed
        // now, because otherwise its path reaches the searchable `file` attribute through
        // attribute extraction above (`incoming`, folded into `attrs`) — not through the carrier
        // below, which is built FROM `fileHashes.Keys` and by definition cannot introduce a path
        // that isn't already in it — while drift detection has nothing to compare the new path
        // against and reports it clean forever.
        //
        // ONLY NEW PATHS ARE HASHED — literally, not just kept: `skip` below excludes every
        // already-baselined path from `HashFileRefs` before the hasher ever runs on it. Hashing an
        // already-baselined path and discarding the result would mean every append pays a full
        // file read (`HashWorkspaceFile` is an uncached `File.ReadAllBytes` + SHA-256) for every
        // path the memory has EVER referenced, to keep at most the handful that are new. Re-hashing
        // would also re-baseline an existing path to its current content on every append, so it
        // could never be reported as drifted — the append would destroy the very signal the
        // baseline exists to provide. A path present in `file` but absent from the baseline because
        // it was unhashable at store time is hashed here if it now resolves: it was never
        // baselined, and this is the first opportunity.
        var fileHashes = existing?.FileHashes;
        var newHashes = HashFileRefs(attrs, fileHasher, skip: fileHashes);
        if (newHashes is not null)
        {
            // Loop-copy, not `new Dictionary<>(fileHashes, StringComparer.OrdinalIgnoreCase)`: that
            // constructor throws ArgumentException if `fileHashes` holds two keys differing only in
            // case — not reachable from what Scrinia writes today, but a hand-edited or legacy
            // sidecar could hold one, and the constructor throwing would fail every future append
            // to that memory. The loop is last-wins instead, same as the constructor would be if it
            // didn't throw.
            var combined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fileHashes is not null)
                foreach (var (path, hash) in fileHashes)
                    combined[path] = hash;
            // No ContainsKey guard needed here: `skip` already kept every already-baselined path
            // out of `newHashes`, so nothing here can collide with what was just copied in.
            foreach (var (path, hash) in newHashes)
                combined[path] = hash;
            fileHashes = combined;
        }

        // Review is canonicalized here on the same rule as the store path (D-06) rather than being
        // re-emitted from the EXISTING entry the way MCP's append did: re-emitting the old value
        // would silently overwrite a caller-supplied review, which D-18 says must win.
        var (reviewAfter, reviewWhen) = SplitReview(attrs);

        var carrier = AttributeMigration.BuildCarrierAttributes(
            tags: null, fileHashes?.Keys, reviewAfter,
            string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen, anchors: null);
        if (carrier.Count > 0)
            attrs = KnownAttributes.Merge(attrs, carrier);

        // D-08 again: complete map first, then one seed, last. MCP's append folded this carrier
        // AFTER seeding, so a carried-forward review or file key was never seeded on an append.
        // The term frequencies are rebuilt from scratch on every append, so everything the map
        // carries — including values that arrived on an earlier write — has to re-seed here.
        AttributeExtractor.SeedTermFrequencies(attrs, tf);

        // D-23/D-24/D-25: the appended chunk's entry comes from the same function the store path
        // uses, so the empty-preview guard and the keyword boost cannot drift from it again. Its
        // index is the post-append chunk count, contiguous with ComputeChunkEntries' 1-based
        // numbering over the earlier chunks.
        var newChunkEntry = TextAnalysis.ComputeChunkEntries([appended])[0]
            with { ChunkIndex = fullContent.Length };
        ChunkEntry[] chunkEntries = existing?.ChunkEntries is not null
            ? [.. existing.ChunkEntries, newChunkEntry]
            : [newChunkEntry];

        // D-20: a new description wins; else the existing preview survives — regenerating from
        // content would silently drop a curated description on every append.
        string preview = !string.IsNullOrWhiteSpace(description)
            ? description
            : existing?.ContentPreview ?? ContentPreview.Generate(joined);

        return new DerivedMetadata(mergedKeywords, tf, attrs, chunkEntries, preview, fileHashes,
            AgentKeywords: [.. mergedKeywords.Where(agentKeywordSet.Contains)]);
    }

    /// <summary>
    /// <b>A HEURISTIC, for entries whose <see cref="ArtifactEntry.AgentKeywords"/> is null only.</b>
    /// Guesses which of <paramref name="keywords"/> the agent supplied, by subtracting the terms
    /// the content itself yields. Provenance is RECORDED now (see
    /// <see cref="ArtifactEntry.AgentKeywords"/>) and any store, append or compact writes it, so
    /// this exists for one thing: an entry last written before that field existed, which must not
    /// have its curated keywords thrown away just because nobody wrote down which they were.
    ///
    /// <para><b>It is not exact, in either direction, and no subtraction can be.</b>
    /// <list type="bullet">
    /// <item><b>It under-recovers, usually a lot.</b> <see cref="TextAnalysis.AnalyzeText"/> takes
    /// the top 25 terms, so in a memory with 25 or fewer distinct tokens EVERY term is an auto
    /// keyword and nothing is recovered at all. A curated keyword the content also contains is
    /// indistinguishable from an auto one after the merge, so it is not returned — it keeps its
    /// place in the rewritten list, but at auto weight.</item>
    /// <item><b>It can over-recover.</b> A term this content does not yield is not necessarily the
    /// agent's: an entry that was APPENDED to before the provenance field existed had its whole
    /// keyword list re-boosted to agent weight by the old union, and an auto keyword whose text is
    /// no longer top-ranked in the joined content looks curated here. The subtraction below is
    /// therefore over the joined text AND over each chunk separately — a strictly larger subtracted
    /// set, which cannot recover less than the caller's real list contains and closes the case
    /// where an appended chunk's terms crowd the earlier chunks' out of the joined top 25.</item>
    /// </list>
    /// Reconstructing the boost arithmetically from <c>TermFrequencies</c> instead is not sound
    /// either: attribute seeding (+3) lands in the same dictionary, and a multi-word keyword seeds
    /// its index tokens rather than itself.</para>
    /// </summary>
    /// <param name="keywords">The entry's persisted keyword list.</param>
    /// <param name="content">The chunks the list was derived over — for a compaction, the memory's
    /// content BEFORE it is rewritten, one element per chunk. Pass the real chunks: collapsing
    /// them into one element loses the per-chunk half of the subtraction.</param>
    public static string[] CuratedKeywords(string[]? keywords, string[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (keywords is not { Length: > 0 })
            return [];

        var auto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Subtract(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var (extracted, _) = TextAnalysis.AnalyzeText(text);
            foreach (string term in extracted) auto.Add(term);
        }

        Subtract(string.Concat(content));
        if (content.Length > 1)
            foreach (string chunk in content) Subtract(chunk);

        return [.. keywords
            .Where(k => !string.IsNullOrWhiteSpace(k) && !auto.Contains(k.Trim()))
            .Select(k => k.Trim())];
    }

    /// <summary>
    /// Re-derives an entry's term frequencies after a METADATA-ONLY change — an attribute or
    /// keyword edit applied through <see cref="IMemoryStore.TryUpdateEntry"/>, with no new content.
    ///
    /// <para><b>Why this exists.</b> Attribute values feed the BM25 index (D-08:
    /// <see cref="AttributeExtractor.SeedTermFrequencies"/> seeds keys and value tokens at +3), so
    /// an attribute change that leaves term frequencies alone silently makes the entry unfindable
    /// by its own new attribute. <c>MemoryTools.UpdateMeta</c> used to compute that boost for
    /// itself — caller-side derivation, at a weight (+5) that disagreed with the store path's, and
    /// invisible to every other mutation route. <c>EntityTypingEventHandler.PromoteAsync</c> and
    /// <c>MaintenanceEventSink</c>'s ref-linking change attributes too and computed nothing, so
    /// whether an attribute reached the index depended on which caller you came through. Worse,
    /// caller-computed term frequencies could be stale or incomplete, so deriving them here keeps
    /// the same edit indexed consistently.</para>
    ///
    /// <para><b>The rule is here and applied by whoever holds the store</b>, i.e. inside
    /// <see cref="IMemoryStore.TryUpdateEntry"/>, which is what makes every mutation route get it.
    /// The store derives from the mutation rather than trusting caller-computed index data.</para>
    ///
    /// <para><b>Only the DELTA seeds.</b> Term frequencies are cumulative, so re-seeding the whole
    /// attribute map on every update would inflate every token without bound and make an unedited
    /// complete-entry round-trip — which the PATCH contract depends on being a no-op — change the
    /// index. Only values not already present under that key are seeded. The corollary is that
    /// REMOVING an attribute does not remove its boost: undoing a seed needs the content-derived
    /// baseline this call does not have, and re-deriving that needs the memory's content, which a
    /// metadata update has none of. A re-store rebuilds term frequencies from scratch and clears
    /// it. Pre-existing behaviour, preserved deliberately rather than half-fixed here.</para>
    /// </summary>
    /// <returns><paramref name="updated"/> unchanged when nothing seed-worthy changed — including
    /// the same <c>TermFrequencies</c> reference, so a caller can tell a no-op apart.</returns>
    public static ArtifactEntry ForMetadataUpdate(ArtifactEntry current, ArtifactEntry updated)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(updated);

        var addedAttributes = NewAttributeValues(current.Attributes, updated.Attributes);
        string[] addedKeywords = NewKeywords(current.Keywords, updated.Keywords);

        if (addedAttributes is null && addedKeywords.Length == 0)
            return updated;

        var tf = updated.TermFrequencies is not null
            ? new Dictionary<string, int>(updated.TermFrequencies, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // The same +3 seeding every write path uses — the function itself, not a copy of its rule.
        AttributeExtractor.SeedTermFrequencies(addedAttributes, tf);

        // A keyword added by a caller is agent-supplied by definition (+5, D-13), and it seeds its
        // INDEX TOKENS rather than the raw string (D-14) — UpdateMeta seeded the raw string, which
        // no tokenized query term can ever equal for a prefixed keyword.
        if (addedKeywords.Length > 0)
            BoostKeywords(addedKeywords, [.. addedKeywords], tf);

        // ...and it is recorded as such, or the next content rewrite has to guess at it. Only when
        // the entry already knows its provenance: promoting a null (UNKNOWN) to a list naming just
        // these would assert that everything else was auto-extracted, which is precisely the thing
        // nobody wrote down. An unknown entry stays unknown and keeps the heuristic.
        string[]? agentKeywords = updated.AgentKeywords is not null && addedKeywords.Length > 0
            ? UnionDistinct(updated.AgentKeywords, addedKeywords)
            : updated.AgentKeywords;

        return updated with { TermFrequencies = tf, AgentKeywords = agentKeywords };
    }

    /// <summary>The attribute values in <paramref name="next"/> that <paramref name="previous"/>
    /// did not already carry under the same key, or null when there are none.</summary>
    private static Dictionary<string, string[]>? NewAttributeValues(
        Dictionary<string, string[]>? previous, Dictionary<string, string[]>? next)
    {
        if (next is not { Count: > 0 }) return null;

        Dictionary<string, string[]>? added = null;
        foreach (var (key, values) in next)
        {
            string[] existing = previous is not null && previous.TryGetValue(key, out var v) ? v : [];
            string[] fresh = values
                .Where(value => !string.IsNullOrWhiteSpace(value)
                             && !existing.Contains(value, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (fresh.Length == 0) continue;
            added ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            added[key] = fresh;
        }
        return added;
    }

    /// <summary>The keywords in <paramref name="next"/> that <paramref name="previous"/> did not
    /// already carry.</summary>
    private static string[] NewKeywords(string[]? previous, string[]? next)
    {
        if (next is not { Length: > 0 }) return [];
        var existing = previous ?? [];
        return next
            .Where(k => !string.IsNullOrWhiteSpace(k)
                     && !existing.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>Seeds each keyword's index tokens into <paramref name="tf"/> — agent terms at
    /// <see cref="AgentKeywordBoost"/>, auto-extracted terms at <see cref="AutoKeywordBoost"/>
    /// (D-13). A <c>prefix:value</c> keyword seeds its tokenized VALUE, never the monolithic
    /// string, which no tokenized query term can equal (D-14).</summary>
    private static void BoostKeywords(
        string[] mergedKeywords, HashSet<string> agentKeywords, Dictionary<string, int> tf)
    {
        foreach (string kw in mergedKeywords)
        {
            int boost = agentKeywords.Contains(kw) ? AgentKeywordBoost : AutoKeywordBoost;
            foreach (string token in TextAnalysis.IndexTokensForKeyword(kw))
            {
                tf.TryGetValue(token, out int count);
                tf[token] = count + boost;
            }
        }
    }

    /// <summary>Back-parses <c>Attributes["review"]</c> into its date and free-text halves: the
    /// first date-parseable value is the review date, the rest join into the condition (D-06).
    /// The caller re-emits them through <c>BuildCarrierAttributes</c>, which writes the date in
    /// ISO-8601 round-trip form — the canonical representation for a bare date.</summary>
    private static (DateTimeOffset? ReviewAfter, string? ReviewWhen) SplitReview(
        Dictionary<string, string[]>? attributes)
    {
        if (attributes is null || !attributes.TryGetValue(KnownAttributes.ReviewKey, out var values))
            return (null, null);

        string? rawDate = null;
        var conditions = new List<string>(values.Length);
        foreach (string value in values)
        {
            if (rawDate is null && TryParseReviewDate(value, out _))
                rawDate = value;
            else if (!string.IsNullOrWhiteSpace(value))
                conditions.Add(value);
        }

        DateTimeOffset? parsed = rawDate is not null && TryParseReviewDate(rawDate, out var date)
            ? date
            : null;
        return (parsed, conditions.Count > 0 ? string.Join("; ", conditions) : null);
    }

    /// <summary>
    /// Parses a review value to an instant, zone- and culture-independently.
    ///
    /// <para>RULING (Task 2 review, 2026-08-12): invariant culture, and a bare date is UTC.
    /// Parsing under the current culture or local time zone would persist different values on
    /// different machines.</para>
    ///
    /// <para><b>Public because a review date has one parse, not one per writer.</b> Every path
    /// that turns caller text into a persisted <c>review</c> value calls this: the two derivation
    /// entry points above, and <c>MemoryTools.UpdateMeta</c>, which is the metadata-only writer and
    /// therefore reaches none of them. It kept its own <c>DateTimeOffset.TryParse</c> for one task
    /// longer than the rest, which meant the same <c>"2026-08-01"</c> persisted as UTC through
    /// <c>remember()</c> and as the machine's local zone through <c>MemoryTools.UpdateMeta</c>
    /// (no caller outside <c>link()</c> and the tests) — the same defect this ruling exists to
    /// remove, one caller over.</para>
    /// </summary>
    public static bool TryParseReviewDate(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);

    /// <summary>Hashes every <c>file</c> attribute value through the caller's resolver, keeping
    /// only the ones it could hash (D-04). Null when there is no resolver or nothing hashed.
    /// <paramref name="skip"/>, when given, excludes its keys from being hashed AT ALL — not just
    /// from the returned result — so a caller carrying forward an existing baseline
    /// (<see cref="ForAppend"/>) never pays a file read for a path it already has a hash for.
    /// Defaulted null so <see cref="ForStore"/>'s call, which has no baseline to carry, is
    /// unaffected.</summary>
    private static Dictionary<string, string>? HashFileRefs(
        Dictionary<string, string[]>? attributes, Func<string, string?>? fileHasher,
        Dictionary<string, string>? skip = null)
    {
        if (fileHasher is null
            || attributes is null
            || !attributes.TryGetValue("file", out var refs))
            return null;

        Dictionary<string, string>? hashes = null;
        foreach (string path in refs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (skip is not null && skip.ContainsKey(path.Trim())) continue;
            string? hash = fileHasher(path);
            if (hash is null) continue;
            hashes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            hashes[path.Trim()] = hash;
        }
        return hashes;
    }

    /// <summary>Replaces <paramref name="key"/> in the merged map with the union of the existing
    /// and incoming values, defeating the scalar replace <c>KnownAttributes.Merge</c> would
    /// otherwise apply (D-17). No-op when either side is absent.</summary>
    private static void UnionCarriedKey(
        Dictionary<string, string[]>? merged,
        Dictionary<string, string[]>? existing,
        Dictionary<string, string[]>? incoming,
        string key)
    {
        if (merged is null
            || existing is null || !existing.TryGetValue(key, out var existingValues)
            || incoming is null || !incoming.TryGetValue(key, out var incomingValues))
            return;

        merged[key] = UnionDistinct(existingValues, incomingValues);
    }

    /// <summary>Case-insensitive union preserving first-seen casing and order; drops blanks.</summary>
    private static string[] UnionDistinct(string[]? existing, string[]? added) =>
        (existing ?? []).Concat(added ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// Everything a memory already carries that an append must not destroy. Ephemeral and persistent
/// entries are separate types with no common interface, so the pipeline takes the fields both
/// carry rather than either entry type — which also keeps it free of the storage model.
/// </summary>
public sealed record ExistingMetadata(
    Dictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null,
    string? ContentPreview = null,
    ChunkEntry[]? ChunkEntries = null,
    Dictionary<string, string>? FileHashes = null,
    /// <inheritdoc cref="ArtifactEntry.AgentKeywords"/>
    string[]? AgentKeywords = null);

/// <summary>
/// The derived half of a memory entry: everything the write paths used to compute for themselves.
/// <see cref="Keywords"/> and <see cref="TermFrequencies"/> are never null — empty means empty —
/// so a caller decides for itself whether to persist an empty collection as null.
/// </summary>
public sealed record DerivedMetadata(
    string[] Keywords,
    Dictionary<string, int> TermFrequencies,
    Dictionary<string, string[]>? Attributes,
    ChunkEntry[]? ChunkEntries,
    string ContentPreview,
    Dictionary<string, string>? FileHashes = null,
    /// <summary>Which of <see cref="Keywords"/> were caller-supplied. Never null out of the
    /// pipeline — it always knows — so a null on a persisted entry means the entry predates the
    /// field. See <see cref="ArtifactEntry.AgentKeywords"/>.</summary>
    string[]? AgentKeywords = null);
