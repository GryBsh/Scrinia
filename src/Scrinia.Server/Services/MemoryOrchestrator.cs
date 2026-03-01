using Scrinia.Core;
using Scrinia.Core.Encoding;
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

        // Text analysis
        var autoKeywords = TextAnalysis.ExtractKeywords(joined);
        var mergedKeywords = TextAnalysis.MergeKeywords(req.Keywords, autoKeywords);
        var tf = TextAnalysis.ComputeTermFrequencies(joined);
        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + 3;
        }

        ChunkEntry[]? chunkEntries = req.Content.Length > 1
            ? ComputeChunkEntries(store, req.Content)
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
            string ephPreview = store.GenerateContentPreview(joined);
            string ephDesc = string.IsNullOrWhiteSpace(req.Description)
                ? joined[..Math.Min(200, joined.Length)]
                : req.Description;

            var existingEph = store.GetEphemeral(key);
            DateTimeOffset ephCreatedAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            DateTimeOffset? ephUpdatedAt = existingEph is not null ? DateTimeOffset.UtcNow : null;

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: ephArtifact,
                OriginalBytes: ephBytes,
                ChunkCount: ephChunkCount,
                CreatedAt: ephCreatedAt,
                Description: ephDesc,
                Tags: req.Tags,
                ContentPreview: ephPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: ephUpdatedAt,
                ChunkEntries: chunkEntries);

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
        string desc = string.IsNullOrWhiteSpace(req.Description)
            ? joined[..Math.Min(200, joined.Length)]
            : req.Description;

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
        string contentPreview = store.GenerateContentPreview(joined);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        DateTimeOffset? parsedReviewAfter = null;
        if (!string.IsNullOrWhiteSpace(req.ReviewAfter) && DateTimeOffset.TryParse(req.ReviewAfter, out var ra))
            parsedReviewAfter = ra;

        var entry = new ArtifactEntry(
            Name: subject,
            Uri: uri,
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            Description: desc,
            Tags: req.Tags,
            ContentPreview: contentPreview,
            Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
            TermFrequencies: tf.Count > 0 ? tf : null,
            UpdatedAt: updatedAt,
            ReviewAfter: parsedReviewAfter,
            ReviewWhen: string.IsNullOrWhiteSpace(req.ReviewWhen) ? null : req.ReviewWhen,
            ChunkEntries: chunkEntries);

        store.Upsert(entry, scope);

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

        byte[] fullBytes = new Nmp2Strategy().Decode(newArtifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        long originalBytes = fullBytes.LongLength;

        var autoKeywords = TextAnalysis.ExtractKeywords(fullText);
        var mergedKeywords = TextAnalysis.MergeKeywords(null, autoKeywords);
        var tf = TextAnalysis.ComputeTermFrequencies(fullText);
        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + 3;
        }

        string contentPreview = store.GenerateContentPreview(fullText);

        var newKw = TextAnalysis.ExtractKeywords(content);
        var newTf = TextAnalysis.ComputeTermFrequencies(content);
        foreach (string k in newKw) { newTf.TryGetValue(k, out int c); newTf[k] = c + 3; }
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

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: newArtifact,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                Description: fullText[..Math.Min(200, fullText.Length)],
                Tags: null,
                ContentPreview: contentPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: DateTimeOffset.UtcNow,
                ChunkEntries: updatedChunks);

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

            if (existingEntry is not null)
                store.ArchiveVersion(subject, scope);

            await store.WriteArtifactAsync(subject, scope, newArtifact, ct);

            string uri = store.ArtifactUri(subject, scope);
            qualifiedName = store.FormatQualifiedName(scope, subject);

            var entry = new ArtifactEntry(
                Name: subject,
                Uri: uri,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                Description: fullText[..Math.Min(200, fullText.Length)],
                Tags: null,
                ContentPreview: contentPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: DateTimeOffset.UtcNow,
                ReviewAfter: existingEntry?.ReviewAfter,
                ReviewWhen: existingEntry?.ReviewWhen,
                ChunkEntries: updatedChunks);

            store.Upsert(entry, scope);
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

        byte[] bytes = new Nmp2Strategy().Decode(artifact);
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
        bool deleted = store.DeleteArtifact(subject, scope);
        bool removed = store.Remove(subject, scope);
        return deleted || removed;
    }

    private static ChunkEntry[] ComputeChunkEntries(IMemoryStore store, string[] chunks)
    {
        var entries = new ChunkEntry[chunks.Length];
        for (int i = 0; i < chunks.Length; i++)
        {
            var kw = TextAnalysis.ExtractKeywords(chunks[i]);
            var tf2 = TextAnalysis.ComputeTermFrequencies(chunks[i]);
            foreach (string k in kw) { tf2.TryGetValue(k, out int c); tf2[k] = c + 3; }
            string preview = store.GenerateContentPreview(chunks[i]);
            entries[i] = new ChunkEntry(
                ChunkIndex: i + 1,
                ContentPreview: string.IsNullOrEmpty(preview) ? null : preview,
                Keywords: kw.Length > 0 ? kw : null,
                TermFrequencies: tf2.Count > 0 ? tf2 : null);
        }
        return entries;
    }
}
