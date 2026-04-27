using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    /// <summary>Bundle operations — export and import memory topics.
    /// No longer exposed as an MCP tool; available via CLI (scri export, scri import).</summary>
    public async Task<string> Bundle(
        [Description("Action: 'export' or 'import'.")] string action,
        [Description("Topic names to export, or topic filter for import.")] string[]? topics = null,
        [Description("Bundle file path (required for import, optional filename for export).")] string? bundlePath = null,
        [Description("Overwrite existing on import (default false).")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "export":
                if (topics is null || topics.Length == 0)
                    return ResponseBuilder.Error("bundle('export') requires 'topics' parameter.").ToYaml();
                return await Export(topics, bundlePath, cancellationToken);

            case "import":
                if (string.IsNullOrWhiteSpace(bundlePath))
                    return ResponseBuilder.Error("bundle('import') requires 'bundlePath' parameter.").ToYaml();
                return await Import(bundlePath, topics, overwrite, cancellationToken);

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: 'export', 'import'.").ToYaml();
        }
    }

    /// <summary>Scan for merge conflicts or resolve a specific conflict.</summary>
    internal Task<string> Reconcile(
        [Description("Conflict ID to resolve (from a prior reconcile scan). Omit to scan for conflicts.")] string? conflictId = null,
        [Description("Resolution: 'ours', 'theirs', or 'merged'. Required when conflictId is provided.")] string? choice = null,
        [Description("Content for 'merged' resolution.")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        // ── Resolve mode: conflictId provided ─────────────────────────────
        if (conflictId is not null)
        {
            if (string.IsNullOrWhiteSpace(choice))
                return Task.FromResult(ResponseBuilder.Error("'choice' is required when resolving a conflict. Use 'ours', 'theirs', or 'merged'.").ToYaml());

            if (!_activeConflicts.TryGetValue(conflictId, out var conflictEntry))
                return Task.FromResult(ResponseBuilder.Error($"Conflict '{conflictId}' not found. Run memory('reconcile') first to scan for conflicts.").ToYaml());

            string? resolvedContent;
            switch (choice.ToLowerInvariant())
            {
                case "ours":
                    resolvedContent = conflictEntry.OursContent;
                    if (resolvedContent is null)
                        return Task.FromResult(ResponseBuilder.Error($"No 'ours' content available for {conflictId}. Use 'merged' with explicit content instead.").ToYaml());
                    break;
                case "theirs":
                    resolvedContent = conflictEntry.TheirsContent;
                    if (resolvedContent is null)
                        return Task.FromResult(ResponseBuilder.Error($"No 'theirs' content available for {conflictId}. Use 'merged' with explicit content instead.").ToYaml());
                    break;
                case "merged":
                    if (string.IsNullOrEmpty(content))
                        return Task.FromResult(ResponseBuilder.Error("'merged' choice requires the content parameter.").ToYaml());
                    if (conflictEntry.Type.Contains("meta", StringComparison.OrdinalIgnoreCase))
                    {
                        try { System.Text.Json.Nodes.JsonNode.Parse(content!); }
                        catch { return Task.FromResult(ResponseBuilder.Error("Merged content is not valid JSON for .meta.json conflict.").ToYaml()); }
                    }
                    resolvedContent = content;
                    break;
                default:
                    return Task.FromResult(ResponseBuilder.Error($"Invalid choice '{choice}'. Use 'ours', 'theirs', or 'merged'.").ToYaml());
            }

            try
            {
                if (conflictEntry.Type == "nmp2")
                {
                    string artifact = Nmp2ChunkedEncoder.Encode(resolvedContent);
                    File.WriteAllText(conflictEntry.FilePath, artifact);
                }
                else
                {
                    File.WriteAllText(conflictEntry.FilePath, resolvedContent);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ResponseBuilder.Error($"Writing resolved content to {conflictEntry.FilePath}: {ex.Message}").ToYaml());
            }

            _activeConflicts.TryRemove(conflictId, out _);
            return Task.FromResult(ResponseBuilder.Success($"Resolved {conflictId} ({conflictEntry.Type}) with '{choice}'. {_activeConflicts.Count} conflict(s) remaining.").WithAction("reconciled").ToYaml());
        }

        // ── Scan mode: no conflictId ──────────────────────────────────────
        _activeConflicts.Clear();

        var store = CurrentStore;
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir)!; // .scrinia/ directory

        var autoResolved = new List<string>();
        var needsManual = new List<string>();
        int nextConflictId = 0;

        // Scan all files in .scrinia/ recursively
        foreach (var filePath in Directory.EnumerateFiles(scriniaDir, "*", SearchOption.AllDirectories))
        {
            string fileContent;
            try { fileContent = File.ReadAllText(filePath); }
            catch { continue; }

            // Check for git conflict markers
            if (!fileContent.Contains("<<<<<<<")) continue;

            string relativePath = Path.GetRelativePath(scriniaDir, filePath);

            if (filePath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            {
                // Try auto-resolve .meta.json
                if (TryAutoResolveMetaJson(filePath, fileContent))
                {
                    autoResolved.Add(relativePath);
                }
                else
                {
                    string id = $"CONFLICT-{++nextConflictId}";
                    _activeConflicts[id] = new ConflictEntry(filePath, "meta.json", null, null);
                    needsManual.Add($"{id}: {relativePath} (.meta.json — auto-resolve failed)");
                }
            }
            else if (filePath.EndsWith(".nmp2", StringComparison.OrdinalIgnoreCase))
            {
                // Extract ours and theirs raw content
                int oursStart = fileContent.IndexOf('\n', fileContent.IndexOf("<<<<<<<")) + 1;
                int separator = fileContent.IndexOf("=======");
                int theirsEnd = fileContent.IndexOf(">>>>>>>");

                if (separator < 0 || theirsEnd < 0) { needsManual.Add($"{relativePath} (.nmp2 — malformed conflict markers)"); continue; }

                int theirsStart = fileContent.IndexOf('\n', separator) + 1;

                string oursRaw = fileContent[oursStart..separator].TrimEnd();
                string theirsRaw = fileContent[theirsStart..theirsEnd].TrimEnd();

                // Try to decode NMP/2 content from each side
                string? oursDecoded = null, theirsDecoded = null;
                try { oursDecoded = System.Text.Encoding.UTF8.GetString(new Scrinia.Core.Encoding.Nmp2Strategy().Decode(oursRaw)); } catch { oursDecoded = oursRaw; }
                try { theirsDecoded = System.Text.Encoding.UTF8.GetString(new Scrinia.Core.Encoding.Nmp2Strategy().Decode(theirsRaw)); } catch { theirsDecoded = theirsRaw; }

                string id = $"CONFLICT-{++nextConflictId}";
                _activeConflicts[id] = new ConflictEntry(filePath, "nmp2", oursDecoded, theirsDecoded);

                // Check for additional conflict regions after the first
                string multiNote = "";
                int afterFirstConflict = theirsEnd + ">>>>>>>".Length;
                if (afterFirstConflict < fileContent.Length && fileContent.IndexOf("<<<<<<<", afterFirstConflict, StringComparison.Ordinal) >= 0)
                    multiNote = " (file has additional conflict regions — resolve manually)";

                needsManual.Add($"{id}: {relativePath} (.nmp2 artifact){multiNote}\n    OURS:\n    {Indent(oursDecoded)}\n    THEIRS:\n    {Indent(theirsDecoded)}");
            }
            else
            {
                string id = $"CONFLICT-{++nextConflictId}";
                _activeConflicts[id] = new ConflictEntry(filePath, "unknown", null, null);
                needsManual.Add($"{id}: {relativePath} (unknown file type)");
            }
        }

        if (autoResolved.Count == 0 && needsManual.Count == 0)
            return Task.FromResult(ResponseBuilder.Success("No merge conflicts found in .scrinia/.").WithAction("reconciled").ToYaml());

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Merge conflict scan: {autoResolved.Count} auto-resolved, {needsManual.Count} need manual resolution.");

        if (autoResolved.Count > 0)
        {
            sb.AppendLine("\nAuto-resolved:");
            foreach (var f in autoResolved) sb.AppendLine($"  OK {f}");
        }
        if (needsManual.Count > 0)
        {
            sb.AppendLine("\nNeeds manual resolution:");
            foreach (var f in needsManual) sb.AppendLine($"  FAIL {f}");
        }

        sb.Append($"\n{_activeConflicts.Count} conflict(s) remaining.");

        var reconcileWarnings = needsManual.Count > 0
            ? new[] { $"{needsManual.Count} conflict(s) need manual resolution." }
            : Array.Empty<string>();
        return Task.FromResult(
            ResponseBuilder.Success(sb.ToString()).WithAction("reconciled").WithActionNeeded(reconcileWarnings).ToYaml());
    }

    private static string Indent(string? text, string prefix = "      ")
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        // Show first 500 chars to keep output manageable
        var truncated = text.Length > 500 ? text[..500] + "..." : text;
        return truncated.Replace("\n", "\n" + prefix);
    }

    private static bool TryAutoResolveMetaJson(string filePath, string conflictedContent)
    {
        try
        {
            // Extract "ours" and "theirs" sections
            // Format: <<<<<<< HEAD\n{ours}\n=======\n{theirs}\n>>>>>>> {branch}
            int oursStart = conflictedContent.IndexOf("<<<<<<<");
            int separator = conflictedContent.IndexOf("=======");
            int theirsEnd = conflictedContent.IndexOf(">>>>>>>");
            if (oursStart < 0 || separator < 0 || theirsEnd < 0) return false;

            // Extract the JSON from each side
            string beforeConflict = conflictedContent[..oursStart];
            string oursSection = conflictedContent[(conflictedContent.IndexOf('\n', oursStart) + 1)..separator];
            string theirsSection = conflictedContent[(conflictedContent.IndexOf('\n', separator) + 1)..theirsEnd];
            string afterConflict = conflictedContent[(conflictedContent.IndexOf('\n', theirsEnd) + 1)..];

            // Try to reconstruct valid JSON from each side
            string oursJson = beforeConflict + oursSection + afterConflict;
            string theirsJson = beforeConflict + theirsSection + afterConflict;

            // Parse both sides as mutable JSON
            var oursNode = JsonNode.Parse(oursJson);
            var theirsNode = JsonNode.Parse(theirsJson);
            if (oursNode is null || theirsNode is null) return false;

            // Pick base: latest updatedAt wins, fall back to theirs
            var baseNode = theirsNode; // default to incoming
            var otherNode = oursNode;

            var oursUpdated = oursNode["updatedAt"]?.GetValue<string>();
            var theirsUpdated = theirsNode["updatedAt"]?.GetValue<string>();
            if (oursUpdated is not null && theirsUpdated is not null)
            {
                if (DateTimeOffset.TryParse(oursUpdated, out var odt) &&
                    DateTimeOffset.TryParse(theirsUpdated, out var tdt) && odt > tdt)
                {
                    baseNode = oursNode;
                    otherNode = theirsNode;
                }
            }

            // Union keywords
            var keywordSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (baseNode["keywords"] is JsonArray baseKw)
                foreach (var k in baseKw) if (k?.GetValue<string>() is string s) keywordSet.Add(s);
            if (otherNode["keywords"] is JsonArray otherKw)
                foreach (var k in otherKw) if (k?.GetValue<string>() is string s) keywordSet.Add(s);

            var sortedKw = new JsonArray();
            foreach (var k in keywordSet.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                sortedKw.Add(k);
            baseNode["keywords"] = sortedKw;

            // Union termFrequencies (max value for shared keys)
            if (baseNode["termFrequencies"] is JsonObject baseTf &&
                otherNode["termFrequencies"] is JsonObject otherTf)
            {
                foreach (var kvp in otherTf)
                {
                    if (baseTf.ContainsKey(kvp.Key))
                    {
                        int baseVal = baseTf[kvp.Key]?.GetValue<int>() ?? 0;
                        int otherVal = kvp.Value?.GetValue<int>() ?? 0;
                        baseTf[kvp.Key] = Math.Max(baseVal, otherVal);
                    }
                    else
                    {
                        baseTf[kvp.Key] = kvp.Value?.GetValue<int>() ?? 0;
                    }
                }
            }

            // Write resolved JSON
            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            string resolvedJson = baseNode.ToJsonString(writeOptions);
            File.WriteAllText(filePath, resolvedJson);

            return true;
        }
        catch
        {
            return false; // If anything fails, report as needing manual resolution
        }
    }

    // ── Maintenance tools ──────────────────────────────────────────────────

    /// <summary>Compact a multi-chunk memory by merging chunks. Archives the original version.</summary>
    internal async Task<string> Compact(
        [Description("Memory name to compact.")] string name,
        [Description("Keep only the N most recent chunks. 0 = merge all into one (default).")] int keepRecent = 0,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        var (scope, subject) = store.ParseQualifiedName(name);

        string artifact = await store.ReadArtifactAsync(subject, scope, cancellationToken);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        if (chunkCount <= 1)
            return ResponseBuilder.Success("Already a single chunk, nothing to compact.").WithAction("compacted").ToYaml();

        if (keepRecent > 0 && keepRecent >= chunkCount)
            return ResponseBuilder.Success($"Nothing to compact — keepRecent ({keepRecent}) >= chunk count ({chunkCount}).").WithAction("compacted").ToYaml();

        // Archive the original before modifying
        store.ArchiveVersion(subject, scope);

        string compacted;
        int newChunkCount;

        if (keepRecent <= 0)
        {
            // Merge all chunks into one: decode entire artifact, re-encode as single chunk
            byte[] allBytes = Nmp2Strategy.Instance.Decode(artifact);
            string fullText = System.Text.Encoding.UTF8.GetString(allBytes);
            compacted = Nmp2ChunkedEncoder.Encode(fullText);
            newChunkCount = 1;
        }
        else if (keepRecent == 1)
        {
            // Keep only the last chunk as a single-chunk artifact
            string lastChunk = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunkCount);
            compacted = Nmp2ChunkedEncoder.Encode(lastChunk);
            newChunkCount = 1;
        }
        else
        {
            // Keep the N most recent chunks
            int startChunk = chunkCount - keepRecent + 1;
            var keptChunks = new string[keepRecent];
            for (int i = 0; i < keepRecent; i++)
                keptChunks[i] = Nmp2ChunkedEncoder.DecodeChunk(artifact, startChunk + i);

            compacted = Nmp2ChunkedEncoder.EncodeChunks(keptChunks);
            newChunkCount = keepRecent;
        }

        await store.WriteArtifactAsync(subject, scope, compacted, cancellationToken);

        // Preserve keywords from the existing entry metadata
        var entries = store.LoadIndex(scope);
        var existingEntry = entries.FirstOrDefault(e => e.Name == subject);

        if (existingEntry is not null)
        {
            long newBytes = System.Text.Encoding.UTF8.GetByteCount(
                System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(compacted)));
            var updatedEntry = existingEntry with
            {
                ChunkCount = newChunkCount,
                OriginalBytes = newBytes,
                UpdatedAt = DateTimeOffset.UtcNow,
                ChunkEntries = null  // chunk-level entries no longer valid after compaction
            };
            store.Upsert(updatedEntry, scope);
        }

        string qualifiedName = store.FormatQualifiedName(scope, subject);
        int dropped = chunkCount - newChunkCount;
        return ResponseBuilder.Success($"Compacted {qualifiedName}: {chunkCount} -> {newChunkCount} chunk{(newChunkCount == 1 ? "" : "s")} ({dropped} dropped). Original archived.")
            .WithFileChanges().WithAction("compacted").ToYaml();
    }

    /// <summary>Resume agent context — agent profile, patterns, session log, available skills.</summary>
    internal async Task<string> Restore(CancellationToken cancellationToken)
    {
        var store = CurrentStore;
        var warnings = new List<string>();
        var info = new List<string>();
        var contentSections = new List<string>();
        var followUpNames = new List<string>();
        string? instruction = null;

        // Check for unresolved merge conflicts in .scrinia/
        try
        {
            string resumeStoreDir = store.GetStoreDirForScope("local");
            string resumeScriniaDir = Path.GetDirectoryName(resumeStoreDir)!;
            if (Directory.Exists(resumeScriniaDir))
            {
                bool hasConflicts = Directory.EnumerateFiles(resumeScriniaDir, "*", SearchOption.AllDirectories)
                    .Any(f =>
                    {
                        try
                        {
                            using var reader = new StreamReader(f);
                            var buf = new char[10240];
                            int read = reader.Read(buf, 0, buf.Length);
                            return new string(buf, 0, read).Contains("<<<<<<<");
                        }
                        catch { return false; }
                    });
                if (hasConflicts)
                    warnings.Add(".scrinia/ has unresolved merge conflicts. Run memory('reconcile') before continuing.");
            }
        }
        catch { /* best-effort check */ }

        // Available skills (built-in + disk overrides)
        try
        {
            string skillsBaseDir = GetScriniaBaseDir(store);
            string skillsDir = Path.Combine(skillsBaseDir, "skills");
            var diskSkills = Directory.Exists(skillsDir)
                ? Directory.GetFiles(skillsDir, "*.md").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null).Cast<string>().ToList()
                : [];
            var builtInNames = BuiltInSkills.Keys.ToList();
            var allSkills = new HashSet<string>(builtInNames, StringComparer.OrdinalIgnoreCase);
            allSkills.UnionWith(diskSkills);
            if (allSkills.Count > 0)
            {
                contentSections.Add(
                    $"Skills available ({allSkills.Count}): " +
                    string.Join(", ", allSkills.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)));
            }
        }
        catch { /* skills enumeration is best-effort */ }

        // Collect agent/* names for followUp — .md files first, NMP/2 fallback
        try
        {
            string agentBaseDir = GetScriniaBaseDir(store);
            string agentDir = Path.Combine(agentBaseDir, "agent");
            bool usedMdFiles = false;

            if (Directory.Exists(agentDir))
            {
                var mdFiles = Directory.GetFiles(agentDir, "*.md");
                if (mdFiles.Length > 0)
                {
                    usedMdFiles = true;
                    foreach (string mdFile in mdFiles)
                    {
                        string agentName = Path.GetFileNameWithoutExtension(mdFile);
                        followUpNames.Add($"/agent/{agentName}");
                    }
                }
            }

            if (!usedMdFiles)
            {
                var (agentScope, _) = store.ParseQualifiedName("agent:placeholder");
                var agentEntries = store.LoadIndex(agentScope);
                foreach (var entry in agentEntries)
                    followUpNames.Add($"/agent/{entry.Name}");
            }
        }
        catch { /* agent scope not yet created — skip silently */ }

        // Collect patterns/* names for followUp
        try
        {
            var (patternsScope, _) = store.ParseQualifiedName("patterns:placeholder");
            var patternsEntries = store.LoadIndex(patternsScope);
            foreach (var entry in patternsEntries)
                followUpNames.Add($"/patterns/{entry.Name}");
        }
        catch { /* patterns scope not yet created — skip silently */ }

        // Checkpoint:latest — followUp if exists
        try
        {
            string checkpointArtifact = await store.ResolveArtifactAsync("checkpoint:latest", cancellationToken);
            if (!string.IsNullOrEmpty(checkpointArtifact))
                followUpNames.Add("/checkpoint/latest");
        }
        catch (FileNotFoundException) { /* no checkpoint */ }

        // Today's session log — followUp if it exists
        try
        {
            string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            await store.ResolveArtifactAsync($"sessions:{today}", cancellationToken);
            followUpNames.Add($"/sessions/{today}");
        }
        catch (FileNotFoundException) { /* no session log for today */ }

        if (contentSections.Count == 0)
            contentSections.Add("No persistent agent context found yet. Use memory('remember') to start storing notes, patterns, and skills.");

        if (followUpNames.Count > 0)
            instruction = "Call memory('recall') for each item in followUp to load full context.";

        return ResponseBuilder.Success(string.Join("\n\n", contentSections))
            .WithAction("restored")
            .WithActionNeeded(warnings.ToArray())
            .WithInfo(info.ToArray())
            .WithInstruction(instruction)
            .WithFollowUp(followUpNames.ToArray())
            .ToYaml();
    }
}
