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

        // Unified fields: key:value-shaped agent keywords route to Fields; only plain
        // keywords participate in the keyword merge.
        var (routedFields, plainKeywords) = FieldExtractor.RouteKeywords(req.Keywords);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(plainKeywords, autoKeywords);
        // Typed keywords (prefix:value) seed their tokenized VALUE so the parts are
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

        var refFields = FieldExtractor.ExtractRefFields(joined);
        var fields = KnownFields.Merge(KnownFields.Merge(routedFields, FieldExtractor.Extract(joined)), refFields);
        FieldExtractor.SeedTermFrequencies(fields, tf);

        ChunkEntry[]? chunkEntries = req.Content.Length > 1
            ? TextAnalysis.ComputeChunkEntries(store, req.Content)
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
                : store.GenerateContentPreview(joined);

            var existingEph = store.GetEphemeral(key);
            DateTimeOffset ephCreatedAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            DateTimeOffset? ephUpdatedAt = existingEph is not null ? DateTimeOffset.UtcNow : null;

            // Route req.Tags -> Attributes["tag"] at write time (req.Tags is the StoreRequest
            // DTO field, not an entry member). EphemeralEntry carries no other request-supplied
            // attribute source on this path, so tag is the only carrier to route here.
            var ephIncoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (req.Tags is { Length: > 0 }) ephIncoming[KnownFields.TagKey] = req.Tags;
            var ephFields = ephIncoming.Count > 0 ? KnownFields.Merge(fields, ephIncoming) : fields;

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
                Attributes: ephFields);

            store.RememberEphemeral(key, ephEntry);

            return new StoreResponse(key, $"~{key}", ephChunkCount, ephBytes,
                $"Remembered: ~{key} ({ephChunkCount} chunk(s), {ephBytes} B) [ephemeral]");
        }

        // ── Persistent path ──────────────────────────────────────────────
        var (scope, subject) = store.ParseQualifiedName(req.Name);

        var existingEntries = store.LoadIndex(scope);
        var existingEntry = existingEntries.FirstOrDefault(e => e.Name == subject);
        DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;
        DateTimeOffset? updatedAt = existingEntry is not null ? DateTimeOffset.UtcNow : null;

        if (existingEntry is not null)
            store.ArchiveVersion(subject, scope);

        string artifact = req.Content.Length == 1
            ? Nmp2ChunkedEncoder.Encode(req.Content[0])
            : Nmp2ChunkedEncoder.EncodeChunks(req.Content);

        await store.WriteArtifactAsync(subject, scope, artifact, ct);

        string uri = store.ArtifactUri(subject, scope);

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
        // Merge 3 Task 2b: an explicit curated description becomes the displayed
        // ContentPreview (option A) — it wins over the auto content snippet.
        string contentPreview = !string.IsNullOrWhiteSpace(req.Description)
            ? req.Description
            : store.GenerateContentPreview(joined);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        DateTimeOffset? parsedReviewAfter = null;
        if (!string.IsNullOrWhiteSpace(req.ReviewAfter) && DateTimeOffset.TryParse(req.ReviewAfter, out var ra))
            parsedReviewAfter = ra;

        var carrier = AttributeMigration.BuildCarrierAttributes(
            req.Tags, fileKeys: null, parsedReviewAfter,
            string.IsNullOrWhiteSpace(req.ReviewWhen) ? null : req.ReviewWhen, anchors: null);
        if (carrier.Count > 0)
            fields = KnownFields.Merge(fields, carrier);

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
            Attributes: fields);

        // Keyword/file safety-net: Forward folds recognized key:value keywords and FileHashes->file
        // into Attributes. (tag/review were already folded into Attributes from the request params
        // above; the former legacy entry members no longer exist.)
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
        string? existingArtifact = null;
        try
        {
            existingArtifact = await store.ResolveArtifactAsync(name, ct);
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
            ContentPreview: store.GenerateContentPreview(content),
            Keywords: newKw.Length > 0 ? newKw : null,
            TermFrequencies: newTf.Count > 0 ? newTf : null);

        string qualifiedName;

        if (store.IsEphemeral(name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset createdAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            ChunkEntry[] updatedChunks = existingEph?.ChunkEntries is not null
                ? [.. existingEph.ChunkEntries, newChunkEntry]
                : [newChunkEntry];

            string contentPreview = existingEph?.ContentPreview ?? store.GenerateContentPreview(fullText);

            var incoming = KnownFields.Merge(
                FieldExtractor.Extract(content), FieldExtractor.ExtractRefFields(content));
            var mergedFields = KnownFields.Merge(existingEph?.Attributes, incoming);
            FieldExtractor.SeedTermFrequencies(mergedFields, tf);

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
                Attributes: mergedFields);

            store.RememberEphemeral(key, ephEntry);
            qualifiedName = $"~{key}";
        }
        else
        {
            var (scope, subject) = store.ParseQualifiedName(name);
            var existingEntries = store.LoadIndex(scope);
            var existingEntry = existingEntries.FirstOrDefault(e => e.Name == subject);
            DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;
            ChunkEntry[] updatedChunks = existingEntry?.ChunkEntries is not null
                ? [.. existingEntry.ChunkEntries, newChunkEntry]
                : [newChunkEntry];

            string contentPreview = existingEntry?.ContentPreview ?? store.GenerateContentPreview(fullText);

            if (existingEntry is not null)
                store.ArchiveVersion(subject, scope);

            await store.WriteArtifactAsync(subject, scope, newArtifact, ct);

            string uri = store.ArtifactUri(subject, scope);
            qualifiedName = store.FormatQualifiedName(scope, subject);

            var incoming = KnownFields.Merge(
                FieldExtractor.Extract(content), FieldExtractor.ExtractRefFields(content));
            var mergedFields = KnownFields.Merge(existingEntry?.Attributes, incoming);
            FieldExtractor.SeedTermFrequencies(mergedFields, tf);

            AttributeReaders.TryReadReview(existingEntry?.Attributes, out var existingReviewDate, out var existingReviewWhen);
            var carrier = AttributeMigration.BuildCarrierAttributes(
                tags: null, fileKeys: null, existingReviewDate, existingReviewWhen, anchors: null);
            if (carrier.Count > 0)
                mergedFields = KnownFields.Merge(mergedFields, carrier);

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
                Attributes: mergedFields);

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
                // Rollback: remove orphaned artifact
                try { store.DeleteArtifact(subject, scope); } catch { }
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
