using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Server.Models;

namespace Scrinia.Server.Services;

/// <summary>
/// Stateless business logic for memory operations — extracted from ScriniaMcpTools.
/// Takes <see cref="IMemoryStore"/> as parameter for testability.
/// </summary>
public static class MemoryOrchestrator
{
    public static async Task<StoreResponse> StoreAsync(
        IMemoryStore store, StoreRequest req, CancellationToken ct = default)
    {
        string joined = string.Concat(req.Content);

        // Text analysis (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);

        // Unified attributes: key:value-shaped agent keywords route to Attributes; only plain
        // keywords participate in the keyword merge.
        var (routedAttributes, plainKeywords) = AttributeExtractor.RouteKeywords(req.Keywords);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(plainKeywords, autoKeywords);
        // Key:value keywords (prefix:value) seed their tokenized VALUE so the parts are
        // BM25-matchable; see TextAnalysis.IndexTokensForKeyword.
        foreach (string kw in mergedKeywords)
        {
            int boost = agentKeywordSet.Contains(kw) ? 5 : 2;
            foreach (string token in TextAnalysis.IndexTokensForKeyword(kw))
            {
                tf.TryGetValue(token, out int count);
                tf[token] = count + boost;
            }
        }

        var refAttributes = AttributeExtractor.ExtractRefAttributes(joined);
        var mentionAttributes = AttributeExtractor.ExtractMentionSpans(joined);
        var attributes = KnownAttributes.Merge(
            KnownAttributes.Merge(
                KnownAttributes.Merge(routedAttributes, AttributeExtractor.Extract(joined)),
                refAttributes),
            mentionAttributes);
        // Explicit agent-supplied attributes fold in last so they win scalar collisions — the same
        // position and the same Normalize call as the MCP write path, so the resulting ATTRIBUTE MAP
        // matches a local write of the same input. Merged before the shadow-drop and the TF seeding
        // below so both see the agent's keys.
        //
        // TermFrequencies do NOT match a local write, and that is a known residual rather than an
        // oversight. Over the wire the carrier keys (tag/review/anchor/file) arrive inside this one
        // flat map and get seeded here; locally SeedTermFrequencies runs before the carrier merge,
        // so those keys are never seeded. The server also cannot tell an agent-supplied attribute
        // from a carrier one — a wire map is flat by construction — so it treats the caller's
        // keywords as agent-supplied and boosts them accordingly. Net effect: BM25 ranking for the
        // same memory differs slightly by store mode. Reproducing local's split would mean sending
        // the two maps separately, which is a wire-shape change, not a reordering here.
        attributes = KnownAttributes.Merge(attributes, KnownAttributes.Normalize(req.Attributes));
        // An agent-supplied (or req.Keywords-routed) "person:jolene" collides with a mention:jolene
        // span extracted from the same content. Shadow-drop unconditionally; it's a no-op when
        // nothing shadows.
        attributes = AttributeExtractor.DropMentionsShadowedByTypedKeys(attributes);
        AttributeExtractor.SeedTermFrequencies(attributes, tf);

        ChunkEntry[]? chunkEntries = req.Content.Length > 1
            ? TextAnalysis.ComputeChunkEntries(req.Content)
            : null;

        // ── Ephemeral path ───────────────────────────────────────────────
        if (store.IsEphemeral(req.Name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(req.Name);
            string ephArtifact = req.Content.Length == 1
                ? Nmp2ChunkedEncoder.Encode(req.Content[0])
                : Nmp2ChunkedEncoder.EncodeChunks(req.Content);
            int ephChunkCount = Nmp2ChunkedEncoder.GetChunkCount(ephArtifact);
            long ephBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
            // Merge 3 Task 2b: an explicit curated description becomes the displayed
            // ContentPreview (option A) — it wins over the auto content snippet.
            string ephPreview = !string.IsNullOrWhiteSpace(req.Description)
                ? req.Description
                : ContentPreview.Generate(joined);

            var existingEph = store.GetEphemeral(key);
            DateTimeOffset ephCreatedAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            DateTimeOffset? ephUpdatedAt = existingEph is not null ? DateTimeOffset.UtcNow : null;

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: ephArtifact,
                OriginalBytes: ephBytes,
                ChunkCount: ephChunkCount,
                CreatedAt: ephCreatedAt,
                ContentPreview: ephPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: ephUpdatedAt,
                ChunkEntries: chunkEntries,
                Attributes: attributes);

            store.RememberEphemeral(key, ephEntry);

            return new StoreResponse(key, $"~{key}", ephChunkCount, ephBytes,
                $"Remembered: ~{key} ({ephChunkCount} chunk(s), {ephBytes} B) [ephemeral]");
        }

        // ── Persistent path ──────────────────────────────────────────────
        var (scope, subject) = store.ParseQualifiedName(req.Name);

        var existingEntries = store.LoadIndex(scope);
        // Case-insensitive: `subject` comes from ParseQualifiedName and `e.Name` from the index,
        // and neither folds case. An ordinal match here reported "new memory" for a re-store under
        // different casing, which skipped ArchiveVersion below and reset CreatedAt — while the
        // write still landed on the same file on a case-insensitive filesystem, losing the prior
        // version with no archive. The same shape existed at six other sites; all are fixed.
        //
        // Matching the name is only half of it. Everything below turns `subject` back into a file
        // path — ArchiveVersion, WriteArtifactAsync, and the sidecar Upsert writes — so a resolved
        // entry has to hand its own casing forward too. Leaving the caller's casing here would
        // leave the fix depending on the filesystem to fold case on its behalf: right on NTFS,
        // and a second entry alongside the first anywhere case-sensitive.
        var existingEntry = existingEntries.FirstOrDefault(
            e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        if (existingEntry is not null)
            subject = existingEntry.Name;

        DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;
        DateTimeOffset? updatedAt = existingEntry is not null ? DateTimeOffset.UtcNow : null;

        if (existingEntry is not null)
            store.ArchiveVersion(subject, scope);

        string artifact = req.Content.Length == 1
            ? Nmp2ChunkedEncoder.Encode(req.Content[0])
            : Nmp2ChunkedEncoder.EncodeChunks(req.Content);

        await store.WriteArtifactAsync(subject, scope, artifact, ct: ct);

        string uri = store.ArtifactUri(subject, scope);

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
        // Merge 3 Task 2b: an explicit curated description becomes the displayed
        // ContentPreview (option A) — it wins over the auto content snippet.
        string contentPreview = !string.IsNullOrWhiteSpace(req.Description)
            ? req.Description
            : ContentPreview.Generate(joined);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        var entry = new ArtifactEntry(
            Name: subject,
            Uri: uri,
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            ContentPreview: contentPreview,
            Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
            TermFrequencies: tf.Count > 0 ? tf : null,
            UpdatedAt: updatedAt,
            ChunkEntries: chunkEntries,
            Attributes: attributes);

        // Keyword/file safety-net: Forward folds recognized key:value keywords and FileHashes->file
        // into Attributes. Load-bearing for remote writes: HttpMemoryStore hands us the caller's
        // PRE-Forward attribute map plus its keywords, so this is where an agent-supplied
        // "person:maria"-shaped keyword gets re-folded into Attributes on this side of the wire.
        entry = AttributeMigration.Forward(entry);

        try
        {
            store.Upsert(entry, scope);
        }
        catch
        {
            // Rollback: remove orphaned artifact
            try { store.DeleteArtifact(subject, scope); } catch { }
            throw;
        }

        return new StoreResponse(subject, qualifiedName, chunkCount, originalBytes,
            $"Remembered: {qualifiedName} ({chunkCount} chunk(s), {originalBytes} B)");
    }

    public static async Task<AppendResponse> AppendAsync(
        IMemoryStore store, string name, string content, CancellationToken ct = default)
    {
        bool ephemeral = store.IsEphemeral(name);

        // RULING (remote-mode-leftovers, final fixes, Finding 1): the index lookup below is
        // HOISTED ABOVE the artifact read deliberately, and the ORDER is the fix.
        //
        // What it prevents: the read used to run on the caller's casing. SanitizeName does not fold
        // case, so on a case-sensitive filesystem append("mixedcase", ...) against a memory stored
        // as "MixedCase" got FileNotFoundException, fell through to the store path below, and that
        // path's lookup — which this branch made case-insensitive — then found "MixedCase" and
        // overwrote it with the appended chunk alone. append() silently behaving as replace().
        // Resolving identity from the index FIRST and reading with the entry's own casing means the
        // read succeeds and the real append path runs.
        //
        // How it's pinned: this is no longer an unpinnable ruling — NTFS supports per-directory case
        // sensitivity, and tests/Scrinia.Server.Tests/CaseSensitiveAppendTests.cs constructs a
        // FileMemoryStore rooted in a CaseSensitiveDirectory and asserts the OUTCOME directly: after
        // append("mixedcase", ...) against a memory stored as "MixedCase", the resulting content
        // contains both the original body and the appended body. With this hoist reverted, that
        // assertion fails — the content becomes the appended body alone, reproducing the defect
        // exactly. That test SKIPS rather than silently passing on a host that cannot provide a
        // case-sensitive directory. Complementing it, the pre-existing ORDERING guard —
        // AppendUnderADifferentCasing_ReadsTheArtifactUnderTheEntrysOwnCasing
        // (tests/Scrinia.Server.Tests/MemoryOrchestratorAttributesTests.cs) — runs on every host,
        // case-sensitive filesystem or not, and records which read seam was reached and under which
        // name. Its load-bearing assertion is that ResolveArtifactAsync is NEVER reached: the
        // rollback capture further down this method also calls ReadArtifactAsync(subject, scope), so
        // "ReadArtifactAsync saw the canonical name" is satisfied either way and cannot be the
        // discriminator there. The two guards are complementary, not redundant: the ordering guard
        // fires everywhere but only proves the order; the outcome guard proves the fix actually
        // prevents content loss, but only where a case-sensitive directory is available.
        //
        // Ephemeral names keep ResolveArtifactAsync: they live in an in-memory dictionary, not on
        // disk, there is no index entry to resolve identity from, and its "~key" branch is the only
        // thing that reads them. Non-ephemeral names now take ReadArtifactAsync(subject, scope) so
        // the canonical name travels as (scope, subject) rather than through a FormatQualifiedName
        // round trip — same call EmbeddingReindexer makes, and HttpMemoryStore.ReadArtifactAsync is
        // a thin wrapper over ResolveArtifactAsync, so remote behaviour is unchanged.
        string scope = "";
        string subject = "";
        ArtifactEntry? existingEntry = null;
        if (!ephemeral)
        {
            (scope, subject) = store.ParseQualifiedName(name);
            existingEntry = store.LoadIndex(scope).FirstOrDefault(
                e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
            if (existingEntry is not null)
                subject = existingEntry.Name;
        }

        string? existingArtifact = null;
        try
        {
            existingArtifact = ephemeral
                ? await store.ResolveArtifactAsync(name, ct)
                : await store.ReadArtifactAsync(subject, scope, ct);
        }
        catch (FileNotFoundException)
        {
            // Will create new
        }

        if (existingArtifact is null)
        {
            var storeResult = await StoreAsync(store, new StoreRequest([content], name), ct);
            return new AppendResponse(storeResult.Name, storeResult.ChunkCount, storeResult.OriginalBytes, storeResult.Message);
        }

        string newArtifact = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, content);

        byte[] fullBytes = Nmp2Strategy.Instance.Decode(newArtifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        long originalBytes = fullBytes.LongLength;

        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(fullText);
        var mergedKeywords = TextAnalysis.MergeKeywords(null, autoKeywords);
        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + 2;
        }

        // Merge 3 Task 2b: AppendAsync has no description param, so option A's "new description
        // wins" clause doesn't apply here — only "preserve the existing ContentPreview rather
        // than regenerating from content" does (regenerating would drop a curated description
        // folded in by a prior Store/UpdateMeta call). Computed per-branch below since
        // existingEph/existingEntry are branch-local.

        var (newKw, newTf) = TextAnalysis.AnalyzeText(content);
        foreach (string k in newKw) { newTf.TryGetValue(k, out int c); newTf[k] = c + 2; }
        var newChunkEntry = new ChunkEntry(
            ChunkIndex: chunkCount,
            ContentPreview: ContentPreview.Generate(content),
            Keywords: newKw.Length > 0 ? newKw : null,
            TermFrequencies: newTf.Count > 0 ? newTf : null);

        string qualifiedName;

        if (ephemeral)
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset createdAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            ChunkEntry[] updatedChunks = existingEph?.ChunkEntries is not null
                ? [.. existingEph.ChunkEntries, newChunkEntry]
                : [newChunkEntry];

            string contentPreview = existingEph?.ContentPreview ?? ContentPreview.Generate(fullText);

            var incoming = KnownAttributes.Merge(
                AttributeExtractor.Extract(content), AttributeExtractor.ExtractRefAttributes(content));
            var mergedAttributes = KnownAttributes.Merge(existingEph?.Attributes, incoming);
            AttributeExtractor.SeedTermFrequencies(mergedAttributes, tf);

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: newArtifact,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                ContentPreview: contentPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: DateTimeOffset.UtcNow,
                ChunkEntries: updatedChunks,
                Attributes: mergedAttributes);

            store.RememberEphemeral(key, ephEntry);
            qualifiedName = $"~{key}";
        }
        else
        {
            // `scope`, `subject` and `existingEntry` were resolved at the top of this method — the
            // case-insensitive match and the canonical-casing carry-forward both live there now,
            // because the artifact read needs the canonical name too. See StoreAsync above for why
            // both halves are needed; here the loss was every value the append carries over: the
            // prior chunk entries, the merged attributes, the curated preview.
            DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;
            ChunkEntry[] updatedChunks = existingEntry?.ChunkEntries is not null
                ? [.. existingEntry.ChunkEntries, newChunkEntry]
                : [newChunkEntry];

            string contentPreview = existingEntry?.ContentPreview ?? ContentPreview.Generate(fullText);

            // Captured for the rollback below: appending to an existing memory must revert to
            // this content, not delete it. Nothing reads the versions/ archive back, so a
            // delete here would leave the user no way to recover.
            string? previousArtifact = null;
            if (existingEntry is not null)
            {
                try { previousArtifact = await store.ReadArtifactAsync(subject, scope, ct); }
                catch (FileNotFoundException) { /* index knows the entry but the file is gone; nothing to restore */ }
                store.ArchiveVersion(subject, scope);
            }

            await store.WriteArtifactAsync(subject, scope, newArtifact, ct: ct);

            string uri = store.ArtifactUri(subject, scope);
            qualifiedName = store.FormatQualifiedName(scope, subject);

            var incoming = KnownAttributes.Merge(
                AttributeExtractor.Extract(content), AttributeExtractor.ExtractRefAttributes(content));
            var mergedAttributes = KnownAttributes.Merge(existingEntry?.Attributes, incoming);
            AttributeExtractor.SeedTermFrequencies(mergedAttributes, tf);

            AttributeReaders.TryReadReview(existingEntry?.Attributes, out var existingReviewDate, out var existingReviewWhen);
            var carrier = AttributeMigration.BuildCarrierAttributes(
                tags: null, fileKeys: null, existingReviewDate, existingReviewWhen, anchors: null);
            if (carrier.Count > 0)
                mergedAttributes = KnownAttributes.Merge(mergedAttributes, carrier);

            var entry = new ArtifactEntry(
                Name: subject,
                Uri: uri,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                ContentPreview: contentPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: DateTimeOffset.UtcNow,
                ChunkEntries: updatedChunks,
                Attributes: mergedAttributes);

            // Keyword/file safety-net: Forward folds recognized key:value keywords and FileHashes->file
            // into Attributes. Review carried over from existingEntry is already folded into Attributes
            // above; this also covers pre-Attributes entries appended for the first time.
            entry = AttributeMigration.Forward(entry);

            try
            {
                store.Upsert(entry, scope);
            }
            catch
            {
                // A brand-new memory leaves an orphaned artifact, so removing it is the revert.
                // An existing one is restored instead — Upsert does real file I/O and can fail
                // transiently, and deleting would make that permanent.
                // CancellationToken.None: a cancelled append must still be rolled back.
                try
                {
                    if (previousArtifact is not null)
                        await store.WriteArtifactAsync(subject, scope, previousArtifact, ct: CancellationToken.None);
                    else
                        store.DeleteArtifact(subject, scope);
                }
                catch { }
                throw;
            }
        }

        return new AppendResponse(qualifiedName, chunkCount, originalBytes,
            $"Appended chunk {chunkCount} to {qualifiedName} ({chunkCount} chunk(s), {originalBytes} B)");
    }

    public static async Task<ShowResponse?> ShowAsync(
        IMemoryStore store, string name, CancellationToken ct = default)
    {
        string artifact;
        try
        {
            artifact = await store.ResolveArtifactAsync(name, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (!artifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return null;

        byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
        string decoded = System.Text.Encoding.UTF8.GetString(bytes);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        return new ShowResponse(name, decoded, chunkCount, bytes.LongLength);
    }

    public static async Task<bool> ForgetAsync(
        IMemoryStore store, string name, CancellationToken ct = default)
    {
        if (store.IsEphemeral(name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            return store.ForgetEphemeral(key);
        }

        var (scope, subject) = store.ParseQualifiedName(name);
        bool removed = store.Remove(subject, scope);
        bool deleted = store.DeleteArtifact(subject, scope);
        return deleted || removed;
    }

}
