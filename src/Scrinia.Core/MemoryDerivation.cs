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
/// (<c>MemoryTools.Core.cs</c> <c>Remember</c>), server store
/// (<c>MemoryOrchestrator.StoreAsync</c>), MCP append (<c>BuildAppendMetadata</c>) and server
/// append (<c>MemoryOrchestrator.AppendAsync</c>) — which had drifted, in four places far enough
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
/// every auto-extracted term as agent-supplied and boosts it +5 instead of +2. That is the mistake
/// <c>HttpMemoryStore.WriteArtifactAsync</c> makes today by sending MCP's already-merged keywords
/// over the wire, and the one compact would make by round-tripping its stored keyword list. There
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
    // review condition (D-05 — kept; the server never implemented it). Copied from
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
        // REST write and an MCP write of "2026-01-01" persist the same value — `review` is scalar,
        // so the canonical form REPLACES the raw one in the carrier merge below.
        var (reviewAfter, reviewWhen) = SplitReview(attrs);

        // D-05: count-shaped content reviews itself when the counts change. Never for ephemerals,
        // and never over an explicit condition.
        if (string.IsNullOrWhiteSpace(reviewWhen) && !isEphemeral && CountPattern().IsMatch(joined))
            reviewWhen = CountReviewCondition;

        // D-04: the drift baseline is computed on BOTH store modes now. It used to be skipped
        // whenever the store was remote, so a remote write had no baseline at all; the pipeline
        // runs inside the store, so whoever has the workspace supplies the hasher.
        var fileHashes = HashFileRefs(attrs, fileHasher);

        var carrier = AttributeMigration.BuildCarrierAttributes(
            tags: null, fileHashes?.Keys, reviewAfter,
            string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen, anchors: null);
        if (carrier.Count > 0)
            attrs = KnownAttributes.Merge(attrs, carrier);

        // D-08 — the one deliberate ordering choice in the pipeline, and it inverts the "MCP wins"
        // default. The complete attribute map is assembled FIRST, then seeded exactly ONCE, here,
        // as the last step of attribute handling. This is the server's order: MCP seeded before
        // folding its carrier, so tag/review/file values never seeded at all and anchor values
        // seeded +2 from a separate pass. MCP's split is not expressible over one flat map — a
        // wire map cannot say which keys were "carrier" — so the two paths converge on this one.
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

        return new DerivedMetadata(mergedKeywords, tf, attrs, chunkEntries, preview, fileHashes);
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
    public static DerivedMetadata ForAppend(
        string[] fullContent,
        string? description = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        string[]? keywords = null,
        ExistingMetadata? existing = null)
    {
        ArgumentNullException.ThrowIfNull(fullContent);
        if (fullContent.Length == 0)
            throw new ArgumentException("An append derives from at least one chunk.", nameof(fullContent));

        // Concat, matching the decoded artifact this used to be handed as a single string.
        string joined = string.Concat(fullContent);
        string appended = fullContent[^1];

        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);

        var (routedNewAttributes, plainNewKeywords) = AttributeExtractor.RouteKeywords(keywords);

        // D-12 — a data-loss fix, not a ranking tweak. The server's append passed null here, so
        // every curated keyword on the memory was destroyed by a REST append. The memory's own
        // keywords plus the caller's additions are the agent set; auto terms from the full text
        // fill the rest. A previously-auto keyword folded into the curated set gets the agent
        // boost too — an intentional over-weight, in exchange for never losing a curated term.
        string[] curatedKeywords = UnionDistinct(existing?.Keywords, plainNewKeywords);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(
            curatedKeywords.Length > 0 ? curatedKeywords : null, autoKeywords);

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

        // D-21 — the second data-loss fix. The server's append had no FileHashes at all: a REST
        // append destroyed the memory's drift baseline. The baseline is carried forward, and its
        // path keys are re-folded into `file` so the searchable key never falls behind the hashes.
        var fileHashes = existing?.FileHashes;

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

        return new DerivedMetadata(mergedKeywords, tf, attrs, chunkEntries, preview, fileHashes);
    }

    /// <summary>
    /// The subset of <paramref name="keywords"/> that is PROVABLY agent-supplied, recovered by
    /// subtracting what <see cref="TextAnalysis.AnalyzeText"/> extracts from the content the list
    /// was derived over. For a rewrite of a memory's own content — compaction — this is the answer
    /// to "which of these did the agent actually give me?", and it is the FIRST option D-29 names
    /// ("only the memory's genuinely-curated keywords, or none").
    ///
    /// <para><b>Why a caller cannot just hand its stored list back.</b> A persisted
    /// <c>Keywords</c> array is the MERGE of the agent's list with the auto-extracted terms, with
    /// no record of which was which. Re-entering it as <see cref="MemoryWrite.Keywords"/>
    /// reclassifies every auto term as agent-supplied and seeds it at +5 instead of +2 — on every
    /// rewrite, compounding (D-01). Passing nothing avoids that but drops the curated terms
    /// outright. This is the third answer: hand back only what is provably not auto.</para>
    ///
    /// <para><b>What it recovers, exactly.</b> Every returned keyword is certainly curated: a term
    /// <see cref="TextAnalysis.AnalyzeText"/> does not extract from this content cannot have
    /// entered the list any other way. The converse does NOT hold, and this is the limit worth
    /// knowing: a curated keyword that is ALSO one of the content's top-ranked terms is
    /// indistinguishable from an auto one and is not returned, so it keeps its place in the
    /// rewritten list (the content still yields it) but at auto weight. The provenance to do
    /// better was destroyed when the merged list was persisted; recovering it would need a stored
    /// agent-set flag, not a cleverer subtraction. Reconstructing the boost arithmetically from
    /// <c>TermFrequencies</c> is NOT a sound instrument either — attribute seeding (+3) lands in
    /// the same dictionary, and a multi-word keyword seeds its index tokens rather than itself.</para>
    /// </summary>
    /// <param name="keywords">The entry's persisted keyword list.</param>
    /// <param name="content">The chunks the list was derived over — for a compaction, the memory's
    /// content BEFORE it is rewritten. Joined the way <see cref="ForStore"/> joins it.</param>
    public static string[] CuratedKeywords(string[]? keywords, string[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (keywords is not { Length: > 0 })
            return [];

        var (autoKeywords, _) = TextAnalysis.AnalyzeText(string.Concat(content));
        var auto = new HashSet<string>(autoKeywords, StringComparer.OrdinalIgnoreCase);

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
    /// over HTTP the caller-computed map could not travel at all — <c>PATCH</c> refuses
    /// <c>TermFrequencies</c> from a body, correctly — so the same edit indexed locally and did not
    /// index remotely, with a success reported both times.</para>
    ///
    /// <para><b>The rule is here and applied by whichever side holds the store</b>, i.e. inside
    /// <see cref="IMemoryStore.TryUpdateEntry"/>. A local session applies it in
    /// <c>FileMemoryStore</c>; a remote one sends only the attributes, and the server applies it in
    /// the <c>FileMemoryStore</c> behind <c>PATCH</c>. No derived value crosses the wire, one
    /// implementation serves both, and every mutation route gets it.</para>
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

        return updated with { TermFrequencies = tf };
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
    /// ISO-8601 round-trip form — the canonicalization that closes the MCP/REST disagreement over
    /// a bare date.</summary>
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
    /// <para>RULING (Task 2 review, 2026-08-12): invariant culture, and a bare date is UTC. MCP
    /// used a bare <c>DateTimeOffset.TryParse</c>, which reads under <c>CurrentCulture</c> and
    /// assumes the machine's LOCAL zone — so <c>ToString("o")</c> embedded the writer's UTC offset
    /// and a store in UTC and a client in UTC+10 persisted different strings for the same
    /// <c>"2026-01-01"</c>. D-06 exists to close "MCP and REST disagree on a bare date in a review
    /// attribute"; keeping local-zone parsing would reopen it in a new coat, one machine at a time.
    /// This technically departs from D-06's "keep MCP", on the grounds that MCP's output is
    /// machine-dependent and therefore not a well-defined target to keep.</para>
    ///
    /// <para><b>Public because a review date has one parse, not one per writer.</b> Every path
    /// that turns caller text into a persisted <c>review</c> value calls this: the two derivation
    /// entry points above, and <c>MemoryTools.UpdateMeta</c>, which is the metadata-only writer and
    /// therefore reaches none of them. It kept its own <c>DateTimeOffset.TryParse</c> for one task
    /// longer than the rest, which meant the same <c>"2026-08-01"</c> persisted as UTC through
    /// <c>remember()</c> and as the machine's local zone through <c>update()</c> — the same defect
    /// this ruling exists to remove, one caller over.</para>
    /// </summary>
    public static bool TryParseReviewDate(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);

    /// <summary>Hashes every <c>file</c> attribute value through the caller's resolver, keeping
    /// only the ones it could hash (D-04). Null when there is no resolver or nothing hashed.</summary>
    private static Dictionary<string, string>? HashFileRefs(
        Dictionary<string, string[]>? attributes, Func<string, string?>? fileHasher)
    {
        if (fileHasher is null
            || attributes is null
            || !attributes.TryGetValue("file", out var refs))
            return null;

        Dictionary<string, string>? hashes = null;
        foreach (string path in refs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
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
    Dictionary<string, string>? FileHashes = null);

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
    Dictionary<string, string>? FileHashes = null);
