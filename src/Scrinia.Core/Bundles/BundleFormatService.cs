using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Core.Bundles;

/// <summary>Bundle index containing artifact entries for a single topic.</summary>
public sealed record BundleIndex(List<ArtifactEntry> Entries);

/// <summary>Bundle manifest describing the archive contents.</summary>
public sealed record BundleManifest(
    int Version,
    string Exported,
    List<string> Topics,
    int TotalEntries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, List<string>>? FileEntities = null);

[JsonSourceGenerationOptions(
     WriteIndented = true,
     PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
     DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BundleIndex))]
[JsonSerializable(typeof(BundleManifest))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(MemoryAddress))]
[JsonSerializable(typeof(MemoryAddressKind))]
[JsonSerializable(typeof(MemoryWrite))]
[JsonSerializable(typeof(MemoryError))]
[JsonSerializable(typeof(MemoryAddressError))]
[JsonSerializable(typeof(MemorySearchRequest))]
[JsonSerializable(typeof(MemoryListRequest))]
[JsonSerializable(typeof(ArtifactEntry))]
public partial class BundleJsonContext : JsonSerializerContext;

/// <summary>
/// Shared bundle format I/O — zip creation/extraction, manifest/index serialization.
/// Used by the local CLI bundle commands and MCP memory tools.
/// </summary>
public static class BundleFormatService
{
    public static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = BundleJsonContext.Default,
    };

    /// <summary>
    /// Write topics to a zip archive. Caller manages the ZipArchive lifecycle. Builds every topic
    /// (index, body, manifest) BEFORE opening any entry, so a malformed topic, oversized body or
    /// unsafe subject fails the build and no topic is published; the publish order and wire format
    /// are otherwise unchanged.
    /// </summary>
    /// <returns>Tuple of (exported topic names, total entry count).</returns>
    public static (List<string> Topics, int EntryCount) ExportTopicsToZip(
        ZipArchive zip, IMemoryStore store, string[] topics)
    {
        var (staged, exportedTopics, totalEntries) = BuildExportStaging(store, topics);
        PublishStaging(zip, staged, totalEntries, store);
        return (exportedTopics, totalEntries);
    }

    /// <summary>
    /// Write topics to a bundle at <paramref name="bundlePath"/>. Builds every topic into an
    /// in-memory staging structure BEFORE the bundle file exists, so a malformed topic, oversized
    /// body or unsafe subject fails the build and no bundle is written. The assembled archive is
    /// then published atomically with <see cref="DurableFile.AtomicWriteAllBytes"/> — the single
    /// durable boundary of the export — and any failure (a build refuse or a failed publish) removes
    /// the target so a failed export leaves no half-created bundle.
    /// </summary>
    /// <returns>Tuple of (exported topic names, total entry count).</returns>
    public static (List<string> Topics, int EntryCount) ExportTopicsToZip(
        string bundlePath, IMemoryStore store, string[] topics)
    {
        var (staged, exportedTopics, totalEntries) = BuildExportStaging(store, topics);

        // Nothing to publish: leave no bundle behind. Zero exported topics means nothing was staged,
        // so there is nothing to remove — this mirrors the facade's empty-export delete without
        // ever creating the file.
        if (staged.Count == 0)
            return (exportedTopics, 0);

        try
        {
            using var archiveBytes = new MemoryStream();
            using (var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
                PublishStaging(zip, staged, totalEntries, store);

            DurableFile.AtomicWriteAllBytes(bundlePath, archiveBytes.ToArray());
        }
        catch
        {
            // cleanup-on-failure: refuse-before-write never created the file, and a failed atomic
            // publish removed its own temp — either way nothing half-created may remain.
            try { File.Delete(bundlePath); } catch { }
            throw;
        }

        return (exportedTopics, totalEntries);
    }

    private sealed record StagedExportTopic(
        string BundleTopic,
        IReadOnlyList<ArtifactEntry> Entries,
        List<(string Name, string Content)> Artifacts,
        int ArtifactCount);

    /// <summary>
    /// Builds every requested topic into an in-memory staging structure BEFORE any archive entry is
    /// written. This is the refuse-before-write gate: <see cref="MemoryNaming.BuildScopedTopicScope"/>
    /// throws on a malformed topic (a trailing dot, a bare ".", a NUL), so a bad topic aborts the
    /// whole build and no topic is published. Topics with no entries are skipped, exactly as before.
    /// </summary>
    private static (List<StagedExportTopic> Staged, List<string> ExportedTopics, int TotalEntries)
        BuildExportStaging(IMemoryStore store, string[] topics)
    {
        var staged = new List<StagedExportTopic>();
        var exportedTopics = new List<string>();
        int totalEntries = 0;

        foreach (string topic in topics)
        {
            // Task 13: the ONE scope constructor sanitizes each segment itself, so the separate
            // SanitizeName call this used to wrap is gone. That pairing sanitized without
            // de-aliasing — "topic." survived it intact and became a second scope string over the
            // "topic" directory.
            string topicScope = MemoryNaming.BuildScopedTopicScope(topic.Trim());
            var artifacts = store.ListTopicArtifacts(topicScope);
            var entries = store.LoadIndex(topicScope);

            if (entries.Count == 0)
                continue;

            // DERIVED from the scope, never re-derived from `topic`. These are two different
            // transforms of the same input — the scope treats '/' as a SEPARATOR, SanitizeName
            // replaces it with '_' — so computing them independently made them disagree for any
            // hierarchical topic: "goal/G-5" resolved scope "local:goal/G-5" (the real content)
            // but wrote zip key and manifest entry "goal_G-5", and import then reconstructed
            // "local:goal_G-5" and landed the memories in a DIFFERENT scope than they left,
            // silently. FormatScopeLabel is BuildScopedTopicScope's exact inverse, so the key and
            // the scope cannot drift. Zip entry names use '/' as their separator already, so a
            // hierarchical topic round-trips as a nested path rather than being flattened.
            string bundleTopic = MemoryNaming.FormatScopeLabel(topicScope);
            var contents = artifacts.Select(a => (a.Name, File.ReadAllText(a.FilePath))).ToList();

            staged.Add(new StagedExportTopic(bundleTopic, entries, contents, artifacts.Count));
            exportedTopics.Add(bundleTopic);
            totalEntries += artifacts.Count;
        }

        return (staged, exportedTopics, totalEntries);
    }

    /// <summary>
    /// Publishes a fully built staging set to an open archive: one topic per <see cref="WriteTopic"/>
    /// call, then the disk-based file entities and the manifest. This is the only place the bundle
    /// layout is written, so export and the CLI's <c>scri bundle</c> (which shares <see cref="WriteTopic"/>
    /// and <see cref="WriteManifest"/>) cannot drift.
    /// </summary>
    private static void PublishStaging(
        ZipArchive zip, List<StagedExportTopic> staged, int totalEntries, IMemoryStore store)
    {
        foreach (var topic in staged)
            WriteTopic(zip, store, topic.BundleTopic, topic.Entries, topic.Artifacts);

        // Export disk-based file entities (skills, agent)
        var fileEntities = ExportFileEntities(zip, store);
        WriteManifest(zip, staged.Select(t => t.BundleTopic).ToList(), totalEntries, fileEntities);
    }

    /// <summary>
    /// Writes one topic's <c>index.json</c> and its <c>.nmp2</c> artifact entries into an open
    /// archive. The single definition of the topic half of the bundle layout — both
    /// <see cref="ExportTopicsToZip"/> (which reads from a store) and the CLI's <c>scri bundle</c>
    /// (which reads loose files that were never memories) call it, so the two cannot drift.
    /// </summary>
    /// <param name="artifacts">Entry name → artifact text. Enumerated lazily, so a caller
    /// streaming from disk does not have to hold every artifact in memory at once.</param>
    internal static void WriteTopic(
        ZipArchive zip,
        IMemoryStore store,
        string bundleTopic,
        IReadOnlyList<ArtifactEntry> entries,
        IEnumerable<(string Name, string Content)> artifacts)
    {
        string indexJson = JsonSerializer.Serialize(
            new BundleIndex(entries.ToList()), BundleJsonContext.Default.BundleIndex);
        var indexEntry = zip.CreateEntry($"topics/{bundleTopic}/index.json");
        using (var writer = new StreamWriter(indexEntry.Open()))
            writer.Write(indexJson);

        foreach (var (name, content) in artifacts)
        {
            var zipEntry = zip.CreateEntry($"topics/{bundleTopic}/{store.SanitizeName(name)}.nmp2");
            using var writer = new StreamWriter(zipEntry.Open());
            writer.Write(content);
        }
    }

    /// <summary>
    /// Writes the archive manifest, including the version rule: v2 when the bundle carries
    /// disk-based file entities, v1 otherwise. The single definition of that rule — the CLI used
    /// to hard-code a literal 1, which happened to be right for what it writes and would have
    /// stopped being right the moment it gained file entities.
    /// </summary>
    internal static void WriteManifest(
        ZipArchive zip,
        List<string> topics,
        int totalEntries,
        Dictionary<string, List<string>>? fileEntities)
    {
        bool hasFileEntities = fileEntities is { Count: > 0 };
        var manifest = new BundleManifest(
            hasFileEntities ? 2 : 1,
            DateTimeOffset.UtcNow.ToString("o"),
            topics,
            totalEntries,
            hasFileEntities ? fileEntities : null);

        string manifestJson = JsonSerializer.Serialize(manifest, BundleJsonContext.Default.BundleManifest);
        var manifestEntry = zip.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
            writer.Write(manifestJson);
    }

    /// <summary>
    /// Read and import topics from a zip archive into the store.
    /// </summary>
    /// <returns>
    /// Tuple of (committed topic count, committed entry count, and topic names).
    /// </returns>
    /// <exception cref="BundleValidationException">If the bundle is malformed or unsupported.</exception>
    public static (int TopicCount, int EntryCount, List<string> Names) ImportTopicsFromZip(
        ZipArchive zip, IMemoryStore store, string[]? filterTopics, bool overwrite)
        => ImportTopicsFromZipAsync(zip, store, filterTopics, overwrite).GetAwaiter().GetResult();

    public static async Task<(int TopicCount, int EntryCount, List<string> Names)> ImportTopicsFromZipAsync(
        ZipArchive zip, IMemoryStore store, string[]? filterTopics, bool overwrite,
        CancellationToken ct = default)
    {
        // Read manifest
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new BundleValidationException("INVALID_BUNDLE: manifest.json not found.");

        string manifestJson = ReadZipEntryText(manifestEntry);

        using var manifestDoc = ParseManifest(manifestJson);
        var root = manifestDoc.RootElement;

        int? declaredVersion = root.TryGetProperty("version", out var versionEl)
            && versionEl.TryGetInt32(out int v) ? v : null;

        if (FormatVersion.Evaluate(DurableFormats.BundleManifest, declaredVersion)
            == FormatDisposition.Refuse)
            throw new BundleValidationException(
                $"INVALID_BUNDLE: manifest declares version {declaredVersion}, but this build " +
                $"of scri supports up to {DurableFormats.BundleManifest.Current}. " +
                "Re-export the bundle with a matching version, or upgrade scri.");

        if (!root.TryGetProperty("topics", out var topicsElement))
            throw new BundleValidationException("INVALID_BUNDLE: no topics in manifest.");
        if (topicsElement.ValueKind != JsonValueKind.Array)
            throw new BundleValidationException("INVALID_BUNDLE: manifest topics must be an array.");

        var availableTopics = new List<string>();
        var topicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in topicsElement.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(t.GetString()))
                throw new BundleValidationException("INVALID_BUNDLE: manifest contains a non-string topic name.");
            string topicName = t.GetString()!;
            if (!topicNames.Add(topicName))
                throw new BundleValidationException($"INVALID_BUNDLE: manifest contains duplicate topic '{topicName}'.");
            availableTopics.Add(topicName);
        }

        // Filter topics
        var selectedTopicNames = filterTopics is { Length: > 0 }
            ? availableTopics.Where(t => filterTopics.Any(f =>
                f.Trim().Equals(t, StringComparison.OrdinalIgnoreCase)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : availableTopics.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Validation is complete even for a filtered restore. Filtering controls the immutable
        // plan, not which declared archive members are allowed to be malformed or missing.
        var topicsToImport = availableTopics;

        // Task 13: validate EVERY manifest topic before writing anything. The scope constructor
        // throws for a topic it cannot build (a trailing dot, a bare ".", NUL), and this loop
        // writes as it goes — so validating inside it would abort part-way through, leaving the
        // store half-imported and surfacing a raw ArgumentException that no caller in
        // BundleService or ScriniaCommands.Bundle turns into a caller-facing error.
        //
        // CORRECTED (branch review): this said it "only fires for a hand-crafted or foreign
        // bundle". It also fires for a bundle THIS codebase exported before Task 13. The
        // pre-Task-13 export wrote `BuildScopedTopicScope(SanitizeName(topic))` where
        // BuildScopedTopicScope was `$"local:{topic}"` with no validation at all, and SanitizeName
        // rejects nothing — it strips "..", swaps separators and invalid characters for '_', and
        // returns Path.GetFileName. A trailing dot or space, a bare "." and "NUL" all survive it
        // intact, so a bundle exported then can carry a manifest topic this refuses now. That is
        // the intended answer either way — refuse cleanly, as the malformed-bundle
        // InvalidOperationException this method already documents, before any side effect — but
        // "foreign bundle" is not where a user will actually meet it.
        foreach (string topic in topicsToImport)
        {
            if (!MemoryNaming.TryBuildScopedTopicScope(topic, out _))
                throw new BundleValidationException(
                    $"INVALID_BUNDLE: manifest topic '{topic}' is not a usable scope name.");
        }

        int declaredTotalEntries = root.TryGetProperty("totalEntries", out var totalEl)
            && totalEl.TryGetInt32(out int total) ? total : -1;
        if (declaredTotalEntries < 0)
            throw new BundleValidationException("INVALID_BUNDLE: manifest totalEntries is missing or invalid.");

        // BUILD PASS — parse and validate every topic before writing any of them. The malformed-
        // index InvalidOperationException below used to live inside the write loop, which meant a
        // bundle with a good topic A followed by a malformed topic B wrote A to the store and only
        // THEN told the caller the bundle was invalid — leaving a half-imported store, the exact
        // failure mode the up-front scope-name validation loop above exists to rule out (":156" —
        // "validate EVERY manifest topic before writing anything ... refuse cleanly ... before any
        // side effect"). Fixed here by the same rule: nothing in this loop calls
        // ImportTopicEntries. Parsed results are collected and written only in the pass below.
        var toWrite = new List<(string TopicScope, string Topic, List<ArtifactEntry> Entries, Dictionary<string, byte[]> ArtifactBytes)>();

        foreach (string topic in topicsToImport)
        {
            // Task 13: import is a WRITE path, so an unconstructable topic throws rather than
            // resolving to an aliased scope — see BundleFormatService's export counterpart.
            string topicScope = MemoryNaming.BuildScopedTopicScope(topic);

            // Derived from the scope, matching export. Back-compatible with bundles written
            // before that change: their manifest carries an already-flattened "goal_G-5", which
            // constructs scope "local:goal_G-5" and formats back to the same "goal_G-5" zip key
            // they were written with, so they still import exactly as they used to. A flat topic
            // is byte-identical either way.
            string bundleTopic = MemoryNaming.FormatScopeLabel(topicScope);

            var indexZipEntry = zip.GetEntry($"topics/{bundleTopic}/index.json");
            if (indexZipEntry is null)
                throw new BundleValidationException(
                    $"INVALID_BUNDLE: manifest topic '{topic}' declares a missing index.json.");

            string indexJson = ReadZipEntryText(indexZipEntry);

            // Deserialize through the SAME source-generated context that ExportTopicsToZip
            // serializes with, so the round trip is total by construction. This replaced a
            // hand-written member-by-member parse that read 7 of ArtifactEntry's 13 members and
            // silently dropped the rest on every backup restore (design review finding M4).
            // Legacy keys are migrated first, in one bounded pass that knows about exactly two
            // of them — see BundleLegacyKeys for why they do not live on the entry itself.
            //
            // JsonNode.Parse AND Deserialize both sit inside this one try: the hand-parse this
            // replaced silently rewrote an unparseable createdAt to "now" and skipped malformed
            // attribute values, and both are now refused rather than guessed — quietly inventing
            // a timestamp is the same defect this branch removes. Covering Parse too (not just
            // Deserialize) closes the whole class of "this topic's index.json is unreadable" —
            // structurally invalid JSON text, not only a validly-shaped value this type rejects —
            // rather than the one path a narrower catch would have named. Converting to
             // BundleValidationException keeps the contract this method documents (:125) true, and
            // names the topic so the bad entry is findable.
            BundleIndex? index;
            try
            {
                if (JsonNode.Parse(indexJson) is not JsonObject indexObject)
                    // A syntactically valid but non-object index root ([], 42, "x", null) is an
                    // unreadable index, not an empty one. Skipping it would silently drop the
                    // whole topic while other topics still imported — a silent partial restore,
                    // and the pre-branch code threw here (JsonElement.TryGetProperty throws on a
                    // non-object). Refuse it like any other unreadable index, before any write.
                    throw new BundleValidationException(
                        $"INVALID_BUNDLE: topic '{topic}' has an unreadable index: " +
                        "the index root is not a JSON object.");

                BundleLegacyKeys.NormalizeIndex(indexObject);

                index = indexObject.Deserialize(BundleJsonContext.Default.BundleIndex);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                throw new BundleValidationException(
                    $"INVALID_BUNDLE: topic '{topic}' has an unreadable index: {ex.Message}", ex);
            }
            if (index?.Entries is not { Count: > 0 })
                continue;

            var entries = new List<ArtifactEntry>(index.Entries.Count);
            var artifactBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            for (int entryIndex = 0; entryIndex < index.Entries.Count; entryIndex++)
            {
                var deserialized = index.Entries[entryIndex];

                // A nameless entry is unaddressable, and dropping one silently is the same defect
                // as silently dropping a whole topic for a non-object index root (":262-270") —
                // one nesting level down. Two distinct cases land here, and they are refused for
                // two different reasons:
                //   - MISSING or null "name": the parse this replaced called
                //     entryEl.GetProperty("name"), which threw KeyNotFoundException. Ugly, but
                //     loud, and the whole import aborted. Deserialization leaves Name null
                //     instead, and skipping it would drop that one memory from the restore while
                //     every sibling entry imported — and nothing compares the returned EntryCount
                //     against the manifest's totalEntries, so the user would be told the restore
                //     succeeded. Refusing restores the pre-branch loudness.
                //   - EMPTY-string "name": the old parse DID accept and store this
                //     (JsonElement.GetString() on "" returns "", which is neither null nor
                //     thrown). Refusing it is a separate, genuine improvement — an entry with no
                //     name could never be found again by anything that looks entries up by Name —
                //     and it is refused the same loud way rather than skipped, for the reason
                //     above.
                // This runs in the build pass, before any topic is written, so refusing here
                // cannot leave an earlier valid topic half-imported.
                if (!deserialized.Address.IsValid)
                    throw new BundleValidationException(
                        $"INVALID_BUNDLE: topic '{topic}' has an entry at index {entryIndex} " +
                        "with a missing or invalid address.");

                string subject = SubjectFromAddress(deserialized.Address);
                try
                {
                    MemoryWrite.ValidatePersistentSubject(subject);
                }
                catch (InvalidMemoryWriteException ex)
                {
                    throw new BundleValidationException(
                        $"INVALID_BUNDLE: topic '{topic}' has an entry '{subject}' that {ex.Message}", ex);
                }

                // Same "refuse before any write" reasoning as the nameless-entry check above.
                // Routed through DurableFormats.BundleEntry, NOT ArtifactSidecar: the same bytes
                // read in a transport context get a different OnNewer (Refuse, not LoadReadOnly),
                // because a bundle entry has no "in place" -- importing it means writing a fresh
                // copy under rules this build cannot know it is reproducing faithfully. That
                // difference is declared as a contract rather than as a `> Current` comparison
                // here, so the policy stays in the one table instead of being restated at a site.
                if (FormatVersion.Evaluate(DurableFormats.BundleEntry, deserialized.SchemaVersion)
                    == FormatDisposition.Refuse)
                    throw new BundleValidationException(
                        $"INVALID_BUNDLE: topic '{topic}' has an entry '{deserialized.Name}' " +
                        $"with schema version {deserialized.SchemaVersion}, but this build of " +
                        $"scri supports up to {DurableFormats.BundleEntry.Current}. " +
                        "Re-export the bundle with a matching version, or upgrade scri.");

                // Folds a pre-rename "fields" key into Attributes and clears the holder. The old
                // hand-parse never did this, so such a bundle lost its attributes entirely. It is a
                // FALLBACK, not a merge: an entry carrying both "fields" and legacy "tags" keeps
                // only the tags-derived attributes, exactly as it does today.
                var entry = deserialized.NormalizeAttributesKey() with { Name = subject };

                if (entries.Any(existing => string.Equals(existing.Name, entry.Name,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new BundleValidationException(
                        $"INVALID_BUNDLE: topic '{topic}' contains duplicate entry '{entry.Name}'.");

                // The deleted hand-parse defaulted a missing "chunkCount" to 1; the deserializer
                // leaves it at 0. Modern bundles always carry the key, so this only bites
                // pre-Merge-3 and hand-crafted ones — which is precisely the population the legacy
                // pre-pass exists for. Uri gets no such restoration on purpose: it is transiently
                // null here and ImportTopicEntries overwrites it before any reader sees it.
                if (entry.ChunkCount == 0)
                    entry = entry with { ChunkCount = 1 };

                string artifactEntryName = $"topics/{bundleTopic}/{store.SanitizeName(entry.Name)}.nmp2";
                var artifactZipEntry = zip.GetEntry(artifactEntryName);
                if (artifactZipEntry is null)
                    throw new BundleValidationException(
                        $"INVALID_BUNDLE: topic '{topic}' declares artifact '{entry.Name}' but its body is missing.");
                artifactBytes[entry.Name] = ReadZipArtifactBytes(artifactZipEntry);

                string[] validatedChunks;
                try
                {
                    validatedChunks = Nmp2ChunkedEncoder.DecodeAllChunksVerified(
                        System.Text.Encoding.UTF8.GetString(artifactBytes[entry.Name])).Chunks;
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException
                    or InvalidOperationException or OverflowException or ArgumentOutOfRangeException)
                {
                    throw new BundleValidationException(
                        $"INVALID_BUNDLE: topic '{topic}' entry '{entry.Name}' has an invalid artifact body.", ex);
                }

                // A null Keywords means this entry was never derived — `scri bundle` packages
                // loose files that were never memories, so nothing ever ran over them. An EMPTY
                // Keywords array is a different fact: derivation ran and found nothing, and must
                // be left alone. Gating on `is null` rather than on length is the whole of that
                // distinction.
                //
                // Round-tripping and deriving cover disjoint gaps. Round-tripping restores what
                // CANNOT be re-derived (AgentKeywords, FileHashes, timestamps); this fills what
                // was never there. Attributes are never rewritten here — the bundle's are
                // authoritative and round-trip intact.
                if (entry.Keywords is null)
                {
                // A bundle is untrusted external input. Every declared artifact body is required
                // restore data, so decode failure refuses the complete plan before any mutation.
                // This is intentionally stricter than the old enrichment-only path, which could
                // import an undecodable body while leaving Keywords null.
                //
                // The catch list below was determined by reading the decode path, not guessed:
                    //   - FormatException: Nmp2Strategy.Base64UrlDecode -> Convert.FromBase64String
                    //     on data that isn't valid base64 (confirmed via a real run against
                    //     non-NMP/2 plain text).
                    //   - InvalidOperationException: BrotliStream.Read's own "Decoder ran into
                    //     invalid data" for base64-valid-but-not-Brotli payloads (confirmed via a
                    //     real run; this is .NET's BCL type/message for that condition, unrelated to
                    //     — and never reachable from inside — the InvalidOperationException this
                    //     method itself throws for a malformed index at ":230-244", which runs
                    //     earlier, over the index JSON, before this per-entry loop begins).
                    //   - InvalidDataException: DecodeAllChunksVerified's own explicit throws for a
                    //     declared chunk count outside [1, MaxChunkCount], a decompressed size
                    //     exceeding MaxDecodedBytes, or a CRC/length mismatch in VerifyIntegrity.
                    //   - OverflowException: an array allocation sized from a hostile declared
                    //     count going negative (confirmed via a real run against a crafted header:
                    //     "Arithmetic operation resulted in an overflow"). DecodeAllChunksVerified's
                    //     own bound check runs before any such allocation, so this is now dead
                    //     coverage for THIS call in particular — left in the filter regardless,
                    //     since a defensive catch that never fires costs nothing and a future change
                    //     to the shared method's allocation order should not silently lose it.
                    //   - ArgumentOutOfRangeException: a forged `##PAD:{n}` footer with a NEGATIVE
                    //     value makes Nmp2Strategy compute compressedLen > buffer length and slice
                    //     past the end (Nmp2Strategy.cs:178-181, :306). Reachable down both
                    //     branches of DecodeAllChunksVerified. This was removed in an earlier round
                    //     as "unreachable" — true of DecodeChunk's own index bounds, false of this
                    //     route, which is why the reason is recorded here rather than the verdict.
                    {
                        // fileHasher is null deliberately: hashing this machine's files against
                        // paths captured on the exporting machine would fabricate a drift baseline
                        // that never existed. A bundle's own FileHashes is preserved as-is above.
                        var derived = MemoryDerivation.ForStore(
                            validatedChunks, keywords: entry.AgentKeywords, fileHasher: null);

                        entry = entry with
                        {
                            Keywords = derived.Keywords,
                            TermFrequencies = derived.TermFrequencies,
                            ChunkEntries = entry.ChunkEntries ?? derived.ChunkEntries,
                            AgentKeywords = entry.AgentKeywords ?? derived.AgentKeywords,
                        };
                    }
                }

                entries.Add(entry);
            }

            if (entries.Count > 0 && selectedTopicNames.Contains(topic))
                toWrite.Add((topicScope, topic, entries, artifactBytes));
        }

        if (declaredTotalEntries != availableTopics.Sum(topic =>
                topic.Equals(string.Empty, StringComparison.Ordinal) ? 0 :
                ReadDeclaredEntryCount(zip, topic)))
            throw new BundleValidationException("INVALID_BUNDLE: manifest totalEntries does not match declared topic entries.");

        // File entities are archive-wide rather than topic-scoped. A partial topic selection must
        // therefore leave them untouched; otherwise a filtered restore would still mutate the
        // unselected skill/agent surface. When the filter selects the complete topic set, the
        // archive is effectively unfiltered and its file entities remain part of the restore.
        bool restoreFileEntities = selectedTopicNames.Count == availableTopics.Count;
        IReadOnlyList<BundleFileEntityRestore> fileEntities = restoreFileEntities
            ? BuildFileEntityWrites(zip, root, store)
            : [];
        var planTopics = toWrite.Select(item => new BundleTopicRestore(
            item.TopicScope, item.Topic, item.Entries, item.ArtifactBytes)).ToList();
        var plan = new BundleRestorePlan(planTopics, fileEntities, overwrite);
        BundleRestoreResult result = await store.RestoreBundleAsync(plan, ct).ConfigureAwait(false);
        return (result.TopicCount, result.EntryCount, planTopics.Select(t => t.Topic).ToList());
    }

    private static int ReadDeclaredEntryCount(ZipArchive zip, string topic)
    {
        string scope = MemoryNaming.BuildScopedTopicScope(topic);
        string key = MemoryNaming.FormatScopeLabel(scope);
        ZipArchiveEntry entry = zip.GetEntry($"topics/{key}/index.json")
            ?? throw new BundleValidationException($"INVALID_BUNDLE: topic '{topic}' has no index.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(ReadZipEntryText(entry));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("entries", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array)
                throw new BundleValidationException($"INVALID_BUNDLE: topic '{topic}' index has no entries.");
            return entries.GetArrayLength();
        }
        catch (JsonException ex)
        {
            throw new BundleValidationException($"INVALID_BUNDLE: topic '{topic}' index is malformed.", ex);
        }
    }

    private static JsonDocument ParseManifest(string manifestJson)
    {
        try
        {
            return JsonDocument.Parse(manifestJson);
        }
        catch (JsonException ex)
        {
            throw new BundleValidationException(
                $"INVALID_BUNDLE: manifest.json is not valid JSON: {ex.Message}", ex);
        }
    }

    private static string SubjectFromAddress(MemoryAddress address)
    {
        if (address.Kind == MemoryAddressKind.Ephemeral)
            return address.Value;

        int separator = address.Value.LastIndexOf('/');
        return separator >= 0 ? address.Value[(separator + 1)..] : address.Value;
    }

    /// <summary>
    /// Reads one archive member while retaining an archive I/O failure as the inner cause of a
    /// durable import failure. JSON parsing failures remain invalid-bundle refusals at their own
    /// boundary.
    /// </summary>
    private static string ReadZipEntryText(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            throw new DurableWriteException("read bundle", entry.FullName, exception);
        }
    }

    /// <summary>
    /// Reads one zip member while enforcing a hard byte cap (the same raw-body bound the NMP/2
    /// artifact path uses). Streams instead of loading the whole member into memory at once, so a
    /// hostile, highly-compressible member cannot blow the process heap. Shared by the artifact and
    /// file-entity import paths — the decompression-bomb guard lives in one place.
    /// </summary>
    private static byte[] ReadZipEntryBounded(ZipArchiveEntry entry, long maximumBytes)
    {
        // Refuse an oversized declared member before allocating any body — defense in depth
        // alongside the streaming cap below. Shared by the artifact and file-entity import
        // paths so the guard lives in one place (design-review finding #2).
        if (entry.Length > maximumBytes)
            throw new BundleValidationException($"INVALID_BUNDLE: '{entry.FullName}' exceeds the maximum raw NMP/2 body size.");

        try
        {
            using var input = entry.Open();
            using var bytes = new MemoryStream(maximumBytes > 0 && maximumBytes < int.MaxValue ? (int)Math.Min(maximumBytes, int.MaxValue) : 0);
            var buffer = new byte[81920];
            long totalBytes = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                totalBytes = checked(totalBytes + read);
                if (totalBytes > maximumBytes)
                    throw new BundleValidationException($"INVALID_BUNDLE: member '{entry.FullName}' exceeds the maximum raw size.");

                bytes.Write(buffer, 0, read);
            }

            return bytes.ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            throw new DurableWriteException("read bundle", entry.FullName, exception);
        }
    }

    private static byte[] ReadZipEntryBytes(ZipArchiveEntry entry) =>
        ReadZipEntryBounded(entry, RestoreJournalCodec.MaxRawNmp2BodyBytes);

    private static byte[] ReadZipArtifactBytes(ZipArchiveEntry entry) =>
        ReadZipEntryBounded(entry, RestoreJournalCodec.MaxRawNmp2BodyBytes);

    // ── File entity helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Bundle manifest category key -> PathRouter file-entity kind. The manifest has used the
    /// plural "skills" since v2 bundles shipped; changing it would break existing bundles.
    /// </summary>
    private static readonly Dictionary<string, string> CategoryToKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skills"] = "skill",
        ["agent"] = "agent",
    };

    /// <summary>
    /// Resolves a file-entity category's directory, or null when the category has no
    /// file-entity kind. Every kind resolves the same way now — the agent special-case is
    /// gone because agent no longer lives inside the memory store.
    /// </summary>
    private static string? CategoryDir(IMemoryStore store, string category) =>
        CategoryToKind.TryGetValue(category, out string? kind)
            ? PathRouter.ToFileEntityDir(kind, store.WorkspaceRoot)
            : null;

    /// <summary>
    /// Scans each file-entity category's directory for exportable files and writes them
    /// into the zip under <c>files/{category}/{filename}</c>.
    /// Skips the <c>versions/</c> subdirectory within each category.
    /// </summary>
    private static Dictionary<string, List<string>> ExportFileEntities(
        ZipArchive zip, IMemoryStore store)
    {
        var fileEntities = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string category in CategoryToKind.Keys)
        {
            string? categoryDir = CategoryDir(store, category);
            if (categoryDir is null || !Directory.Exists(categoryDir))
                continue;

            var files = Directory.EnumerateFiles(categoryDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    string ext = Path.GetExtension(f);
                    return ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".md", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
                continue;

            var fileList = new List<string>();
            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);
                string zipEntryName = $"files/{category}/{fileName}";

                var zipEntry = zip.CreateEntry(zipEntryName);
                using var outStream = zipEntry.Open();
                using var inStream = File.OpenRead(filePath);
                inStream.CopyTo(outStream);

                fileList.Add(fileName);
            }

            if (fileList.Count > 0)
                fileEntities[category] = fileList;
        }

        return fileEntities;
    }

    private sealed record FileEntityWrite(string Kind, string Name, byte[] Body, byte[]? Sidecar);

    /// <summary>
    /// Builds the approved file-entity writes without creating directories or replacing files.
    /// A v1 manifest without <c>fileEntities</c> has no file-entity work.
    /// </summary>
    private static ImmutableArray<BundleFileEntityRestore> BuildFileEntityWrites(
        ZipArchive zip, JsonElement manifestRoot, IMemoryStore store)
    {
        var writes = ImmutableArray.CreateBuilder<BundleFileEntityRestore>();

        if (!manifestRoot.TryGetProperty("fileEntities", out var fileEntitiesEl))
            return writes.ToImmutable();

        if (fileEntitiesEl.ValueKind != JsonValueKind.Object)
            throw new BundleValidationException(
                "INVALID_BUNDLE: manifest fileEntities must be a JSON object when present.");

        foreach (var categoryProp in fileEntitiesEl.EnumerateObject())
        {
            string category = categoryProp.Name;

            // CategoryToKind is the single allowlist. Unknown categories are not optional data:
            // accepting them would make a restore silently incomplete.
            if (!CategoryToKind.TryGetValue(category, out string? kind))
                throw new BundleValidationException($"INVALID_BUNDLE: unknown file-entity category '{category}'.");

            if (categoryProp.Value.ValueKind != JsonValueKind.Array)
                throw new BundleValidationException(
                    $"INVALID_BUNDLE: file category '{category}' must be an array.");

            var names = new List<string?>();
            foreach (JsonElement fileElement in categoryProp.Value.EnumerateArray())
            {
                if (fileElement.ValueKind != JsonValueKind.String)
                    throw InvalidFileEntity(category, null, "contains a non-string filename");
                names.Add(fileElement.GetString());
            }
            var declaredNames = names.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string? fileName in names)
            {
                if (!IsSupportedFileEntityName(fileName))
                    throw InvalidFileEntity(category, fileName, "has an unsafe or unsupported filename");

                string approvedFileName = fileName!;
                string zipEntryName = $"files/{category}/{approvedFileName}";
                var zipEntry = zip.GetEntry(zipEntryName);
                if (zipEntry is null)
                    throw InvalidFileEntity(category, fileName, "is declared but missing from the archive");

                byte[] content = ReadZipEntryBytes(zipEntry);

                ValidateFileEntityMetadata(category, approvedFileName, content);
                if (!approvedFileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    string bodyName = approvedFileName[..^".meta.json".Length] + ".md";
                    if (!declaredNames.Contains(bodyName))
                        throw InvalidFileEntity(category, approvedFileName, "is declared without its file body");
                    continue;
                }

                string sidecarName = Path.GetFileNameWithoutExtension(approvedFileName) + ".meta.json";
                byte[]? sidecar = null;
                if (names.Any(name => string.Equals(name, sidecarName, StringComparison.OrdinalIgnoreCase)))
                {
                    ZipArchiveEntry sidecarEntry = zip.GetEntry($"files/{category}/{sidecarName}")
                        ?? throw InvalidFileEntity(category, sidecarName, "is declared but missing from the archive");
                    sidecar = ReadZipEntryBytes(sidecarEntry);
                    ValidateFileEntityMetadata(category, sidecarName, sidecar);
                }

                writes.Add(new BundleFileEntityRestore(kind, approvedFileName, content, sidecar));
            }
        }

        return writes.ToImmutable();
    }

    private static bool IsSupportedFileEntityName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || fileName.Contains("..")
            || fileName.IndexOfAny(['/', '\\']) >= 0
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            return false;

        return fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || (fileName.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
                && fileName.Length > ".meta.json".Length);
    }

    private static void ValidateFileEntityMetadata(string category, string fileName, byte[] bytes)
    {
        if (!fileName.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            int? declaredVersion;
            if (category.Equals("skills", StringComparison.OrdinalIgnoreCase))
            {
                var metadata = JsonSerializer.Deserialize(bytes, FileEntityJsonContext.Default.SkillFileMeta);
                if (metadata is null)
                    throw InvalidFileEntity(category, fileName, "contains null metadata");

                declaredVersion = metadata.SchemaVersion;
            }
            else
            {
                var metadata = JsonSerializer.Deserialize(bytes, FileEntityJsonContext.Default.AgentFileMeta);
                if (metadata is null)
                    throw InvalidFileEntity(category, fileName, "contains null metadata");

                declaredVersion = metadata.SchemaVersion;
            }
            var contract = category.Equals("skills", StringComparison.OrdinalIgnoreCase)
                ? DurableFormats.SkillMeta
                : DurableFormats.AgentMeta;

            if (FormatVersion.Evaluate(contract, declaredVersion) == FormatDisposition.LoadReadOnly)
                throw InvalidFileEntity(category, fileName,
                    $"declares version {declaredVersion}, but this build supports up to {contract.Current}. " +
                    "Re-export the bundle with a matching version, or upgrade scri");
        }
        catch (JsonException exception)
        {
            throw InvalidFileEntity(category, fileName, "contains malformed metadata JSON", exception);
        }
    }

    private static BundleValidationException InvalidFileEntity(
        string category, string? fileName, string reason, Exception? innerException = null) =>
        new($"INVALID_BUNDLE: file entity '{category}/{fileName ?? "<missing>"}' {reason}.", innerException);
}
