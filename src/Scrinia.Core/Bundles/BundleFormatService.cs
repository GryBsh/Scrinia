using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Scrinia.Core.Encoding;
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
    Dictionary<string, List<string>>? FileEntities = null);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BundleIndex))]
[JsonSerializable(typeof(BundleManifest))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
public partial class BundleJsonContext : JsonSerializerContext;

/// <summary>
/// Shared bundle format I/O — zip creation/extraction, manifest/index serialization.
/// Used by both REST API (BundleService) and MCP layer (MemoryTools).
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
    /// Write topics to a zip archive. Caller manages the ZipArchive lifecycle.
    /// </summary>
    /// <returns>Tuple of (exported topic names, total entry count).</returns>
    public static (List<string> Topics, int EntryCount) ExportTopicsToZip(
        ZipArchive zip, ILocalMemoryStore store, string[] topics)
    {
        int totalEntries = 0;
        var exportedTopics = new List<string>();

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
            exportedTopics.Add(bundleTopic);

            // Write index.json for this topic
            // ToList() because BundleIndex is a serialization DTO declared over List<T>; LoadIndex now
            // returns a live view of the index cache and must not be handed to a type that could retain it.
            string indexJson = JsonSerializer.Serialize(new BundleIndex(entries.ToList()), BundleJsonContext.Default.BundleIndex);
            var indexEntry = zip.CreateEntry($"topics/{bundleTopic}/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(indexJson);

            // Write artifact files
            foreach (var (name, filePath) in artifacts)
            {
                string artifactContent = File.ReadAllText(filePath);
                string entryName = $"topics/{bundleTopic}/{store.SanitizeName(name)}.nmp2";
                var zipEntry = zip.CreateEntry(entryName);
                using var writer = new StreamWriter(zipEntry.Open());
                writer.Write(artifactContent);
                totalEntries++;
            }
        }

        // Export disk-based file entities (skills, agent)
        var fileEntities = ExportFileEntities(zip, store);

        // Write manifest — use v2 when file entities are present
        int version = fileEntities is { Count: > 0 } ? 2 : 1;
        var manifest = new BundleManifest(
            version,
            DateTimeOffset.UtcNow.ToString("o"),
            exportedTopics,
            totalEntries,
            fileEntities is { Count: > 0 } ? fileEntities : null);
        string manifestJson = JsonSerializer.Serialize(manifest, BundleJsonContext.Default.BundleManifest);
        var manifestEntry = zip.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
            writer.Write(manifestJson);

        return (exportedTopics, totalEntries);
    }

    /// <summary>
    /// Read and import topics from a zip archive into the store.
    /// </summary>
    /// <returns>
    /// Tuple of (imported topic count, imported entry count, topic names, skipped file-entity
    /// categories — manifest categories with no <see cref="CategoryToKind"/> entry, reported
    /// rather than silently dropped).
    /// </returns>
    /// <exception cref="InvalidOperationException">If bundle is malformed.</exception>
    public static (int TopicCount, int EntryCount, List<string> Names, List<string> SkippedCategories) ImportTopicsFromZip(
        ZipArchive zip, ILocalMemoryStore store, string[]? filterTopics, bool overwrite)
    {
        // Read manifest
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Invalid bundle — manifest.json not found.");

        string manifestJson;
        using (var reader = new StreamReader(manifestEntry.Open()))
            manifestJson = reader.ReadToEnd();

        using var manifestDoc = JsonDocument.Parse(manifestJson);
        var root = manifestDoc.RootElement;

        if (!root.TryGetProperty("topics", out var topicsElement))
            throw new InvalidOperationException("Invalid bundle — no topics in manifest.");

        var availableTopics = new List<string>();
        foreach (var t in topicsElement.EnumerateArray())
        {
            string? topicName = t.GetString();
            if (topicName is not null)
                availableTopics.Add(topicName);
        }

        // Filter topics
        var topicsToImport = filterTopics is { Length: > 0 }
            ? availableTopics.Where(t => filterTopics.Any(f =>
                f.Trim().Equals(t, StringComparison.OrdinalIgnoreCase))).ToList()
            : availableTopics;

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
                throw new InvalidOperationException(
                    $"Invalid bundle — manifest topic '{topic}' is not a usable scope name.");
        }

        int importedTopics = 0;
        int importedEntries = 0;
        var importedTopicNames = new List<string>();

        // BUILD PASS — parse and validate every topic before writing any of them. The malformed-
        // index InvalidOperationException below used to live inside the write loop, which meant a
        // bundle with a good topic A followed by a malformed topic B wrote A to the store and only
        // THEN told the caller the bundle was invalid — leaving a half-imported store, the exact
        // failure mode the up-front scope-name validation loop above exists to rule out (":156" —
        // "validate EVERY manifest topic before writing anything ... refuse cleanly ... before any
        // side effect"). Fixed here by the same rule: nothing in this loop calls
        // ImportTopicEntries. Parsed results are collected and written only in the pass below.
        var toWrite = new List<(string TopicScope, string Topic, List<ArtifactEntry> Entries, Dictionary<string, string> ArtifactContents)>();

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
                continue;

            string indexJson;
            using (var reader = new StreamReader(indexZipEntry.Open()))
                indexJson = reader.ReadToEnd();

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
            // InvalidOperationException keeps the contract this method documents (:125) true, and
            // names the topic so the bad entry is findable.
            BundleIndex? index;
            try
            {
                if (JsonNode.Parse(indexJson) is not JsonObject indexObject)
                    continue;

                BundleLegacyKeys.NormalizeIndex(indexObject);

                index = indexObject.Deserialize(BundleJsonContext.Default.BundleIndex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid bundle — topic '{topic}' has an unreadable index: {ex.Message}", ex);
            }
            if (index?.Entries is not { Count: > 0 })
                continue;

            var entries = new List<ArtifactEntry>(index.Entries.Count);
            var artifactContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var deserialized in index.Entries)
            {
                // A nameless entry is unaddressable. The parse this replaced threw for a missing
                // "name" key and skipped a null one, but it DID accept and store an empty-string
                // name (JsonElement.GetString() on "" returns "", which is neither null nor
                // thrown). IsNullOrEmpty now refuses that case too — an entry with no name could
                // never be found again by anything that looks entries up by Name, so storing one
                // was never actually useful; refusing it here is still an improvement, not a
                // silent behavior change with a cost.
                if (string.IsNullOrEmpty(deserialized.Name))
                    continue;

                // Folds a pre-rename "fields" key into Attributes and clears the holder. The old
                // hand-parse never did this, so such a bundle lost its attributes entirely. It is a
                // FALLBACK, not a merge: an entry carrying both "fields" and legacy "tags" keeps
                // only the tags-derived attributes, exactly as it does today.
                var entry = deserialized.NormalizeAttributesKey();

                // The deleted hand-parse defaulted a missing "chunkCount" to 1; the deserializer
                // leaves it at 0. Modern bundles always carry the key, so this only bites
                // pre-Merge-3 and hand-crafted ones — which is precisely the population the legacy
                // pre-pass exists for. Uri gets no such restoration on purpose: it is transiently
                // null here and ImportTopicEntries overwrites it before any reader sees it.
                if (entry.ChunkCount == 0)
                    entry = entry with { ChunkCount = 1 };

                string artifactEntryName = $"topics/{bundleTopic}/{store.SanitizeName(entry.Name)}.nmp2";
                var artifactZipEntry = zip.GetEntry(artifactEntryName);
                if (artifactZipEntry is not null)
                {
                    using var reader = new StreamReader(artifactZipEntry.Open());
                    artifactContents[entry.Name] = reader.ReadToEnd();
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
                if (entry.Keywords is null && artifactContents.TryGetValue(entry.Name, out string? artifact))
                {
                    // A bundle is untrusted external input — hand-crafted, foreign, or corrupted —
                    // and before this feature existed, import never decoded artifact bytes at all,
                    // so such a bundle still imported successfully. Two decisions sit side by side
                    // in this method that can look contradictory unless the distinction is written
                    // down: a malformed `createdAt` (":230-244" above) REFUSES the whole import,
                    // while an undecodable artifact here is TOLERATED. They are not the same kind
                    // of failure.
                    //   - A malformed timestamp would make us INVENT metadata — guessing "now" is
                    //     the CreatedAt-reset defect this branch exists to remove.
                    //   - An undecodable artifact makes us DECLINE TO ENRICH. Leaving Keywords null
                    //     preserves its exact prior meaning — "never derived" — so nothing is
                    //     fabricated and nothing that was there is lost.
                    // One fabricates; the other abstains. Making decode failure fatal here would
                    // make this derivation step — purely additive by design — newly REJECT bundles
                    // that imported fine a moment ago, an acceptance regression caused by an
                    // enrichment feature. So one entry's artifact failing to decode must not abort
                    // the whole import; it must fall through with Keywords left exactly as the
                    // bundle carried it (still null).
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
                    string[]? chunks;
                    try
                    {
                        // The shared decode path, not a local one: it carries the chunk-count
                        // bound, the MaxDecodedBytes decompression-bomb budget and the CRC/length
                        // integrity check, and it decompresses each chunk once rather than twice.
                        // Bundle content is untrusted, so all three matter here. A hand-rolled
                        // per-chunk loop over Nmp2ChunkedEncoder.GetChunkCount/DecodeChunk used to
                        // live here; it duplicated the chunk-count bound and omitted the other two,
                        // making it a seventh decode path on top of six existing production callers
                        // of this exact method — the drift this branch exists to stop.
                        chunks = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidDataException
                        or InvalidOperationException or OverflowException)
                    {
                        chunks = null;
                    }

                    if (chunks is not null)
                    {
                        // fileHasher is null deliberately: hashing this machine's files against
                        // paths captured on the exporting machine would fabricate a drift baseline
                        // that never existed. A bundle's own FileHashes is preserved as-is above.
                        var derived = MemoryDerivation.ForStore(
                            chunks, keywords: entry.AgentKeywords, fileHasher: null);

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

            if (entries.Count > 0)
                toWrite.Add((topicScope, topic, entries, artifactContents));
        }

        // WRITE PASS — nothing above this point has touched the store, so every throw in the
        // build pass above happens before any side effect. Holding every topic's entries and
        // artifact text in memory here is acceptable and consistent with the rest of this file,
        // which already reads whole artifacts into memory during export; this is not a streaming
        // path and should not become one.
        foreach (var (topicScope, topic, entries, artifactContents) in toWrite)
        {
            store.ImportTopicEntries(topicScope, entries, artifactContents, overwrite);
            importedTopics++;
            importedEntries += entries.Count;
            importedTopicNames.Add(topic);
        }

        // Import disk-based file entities (v2 bundles; v1 bundles skip this)
        var skippedCategories = ImportFileEntities(zip, root, store);

        return (importedTopics, importedEntries, importedTopicNames, skippedCategories);
    }

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
    private static string? CategoryDir(ILocalMemoryStore store, string category) =>
        CategoryToKind.TryGetValue(category, out string? kind)
            ? PathRouter.ToFileEntityDir(kind, store.WorkspaceRoot)
            : null;

    /// <summary>
    /// Scans each file-entity category's directory for exportable files and writes them
    /// into the zip under <c>files/{category}/{filename}</c>.
    /// Skips the <c>versions/</c> subdirectory within each category.
    /// </summary>
    private static Dictionary<string, List<string>> ExportFileEntities(
        ZipArchive zip, ILocalMemoryStore store)
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

    /// <summary>
    /// Imports disk-based file entities from a v2 bundle.
    /// If the manifest has no <c>fileEntities</c> property (v1 bundles), this is a no-op.
    /// </summary>
    /// <returns>
    /// Manifest category keys that were skipped because <see cref="CategoryToKind"/> has no
    /// entry for them (deleted categories like the former "workflows", or unrecognised/crafted
    /// keys) — reported so a bundle never silently loses content on import.
    /// </returns>
    private static List<string> ImportFileEntities(
        ZipArchive zip, JsonElement manifestRoot, ILocalMemoryStore store)
    {
        var skippedCategories = new List<string>();

        if (!manifestRoot.TryGetProperty("fileEntities", out var fileEntitiesEl)
            || fileEntitiesEl.ValueKind != JsonValueKind.Object)
            return skippedCategories;

        foreach (var categoryProp in fileEntitiesEl.EnumerateObject())
        {
            string category = categoryProp.Name;

            // CategoryToKind is the single allowlist: it doubles as both "is this category
            // recognised" and "where does it live on disk". A category with no kind (deleted,
            // like "workflows", or an unrecognised/crafted key) is reported, never written.
            if (!CategoryToKind.ContainsKey(category))
            {
                skippedCategories.Add(category);
                continue; // deleted or unknown category — reported, never written
            }

            if (categoryProp.Value.ValueKind != JsonValueKind.Array)
                continue;

            // CategoryToKind.ContainsKey(category) already returned true above, so CategoryDir
            // always resolves here — the "no kind" branch that used to guard this is unreachable.
            string categoryDir = CategoryDir(store, category)!;
            Directory.CreateDirectory(categoryDir);

            foreach (var fileEl in categoryProp.Value.EnumerateArray())
            {
                string? fileName = fileEl.GetString();
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                // Guard against path traversal and directory nesting: a crafted manifest entry
                // like "sub/x.md" would pass both the ".." and rooted-path checks, then
                // File.Create(destPath) would throw DirectoryNotFoundException for a
                // subdirectory that doesn't exist — uncaught, aborting the import after topics
                // were already written. Export only ever emits Path.GetFileName(filePath), so a
                // legitimate manifest entry never contains a separator.
                if (fileName.Contains("..") || Path.IsPathRooted(fileName)
                    || fileName.IndexOfAny(['/', '\\']) >= 0)
                    continue;

                string zipEntryName = $"files/{category}/{fileName}";
                var zipEntry = zip.GetEntry(zipEntryName);
                if (zipEntry is null)
                    continue;

                string destPath = Path.Combine(categoryDir, fileName);
                string fullDest = Path.GetFullPath(destPath);
                // Anchored to categoryDir (not the outer .scrinia base): stricter against
                // traversal, but permits a symlinked .scrinia/agent pointing outside .scrinia,
                // where an outer-anchored check would deny it. Deliberate — consistent with the
                // write path (SkillCreate/Remember's agent branch), which never had that outer
                // check either.
                if (!PathContainment.IsWithin(categoryDir, fullDest))
                    continue;

                using var inStream = zipEntry.Open();
                using var outStream = File.Create(destPath);
                inStream.CopyTo(outStream);
            }
        }

        return skippedCategories;
    }
}
