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
    /// <remarks>
    /// Deliberately keeps returning rendered YAML while the internal handlers return
    /// <see cref="McpResponse"/>. It is <c>public</c> on <see cref="ScriniaMcpTools"/> — part of the
    /// assembly's external surface — but has no in-assembly caller, so it sits at the same
    /// serialisation boundary as the twelve <c>[McpServerTool]</c> methods even though it carries no
    /// attribute. Retyping it would push YAML rendering onto external embedders for no internal gain.
    /// </remarks>
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
                    return ResponseBuilder.Error(
                        "bundle('export') requires 'topics' parameter.",
                        ErrorCodes.InvalidParameter,
                        "bundle('export', { topics: ['api', 'arch'] })").ToYaml();
                return (await Export(topics, bundlePath, cancellationToken)).ToYaml();

            case "import":
                if (string.IsNullOrWhiteSpace(bundlePath))
                    return ResponseBuilder.Error(
                        "bundle('import') requires 'bundlePath' parameter.",
                        ErrorCodes.InvalidParameter,
                        "bundle('import', { bundlePath: './exports/foo.scrinia-bundle' })").ToYaml();
                return (await Import(bundlePath, topics, overwrite, cancellationToken)).ToYaml();

            default:
                return ResponseBuilder.Error(
                    $"Unknown action '{action}'. Valid actions: 'export', 'import'.",
                    ErrorCodes.InvalidAction,
                    "bundle('export', { topics: [...] }) or bundle('import', { bundlePath: '...' })").ToYaml();
        }
    }

    /// <summary>Scan for merge conflicts or resolve a specific conflict.</summary>
    /// <remarks>
    /// Conflict identifiers are workspace-relative paths under .scrinia/ (e.g. "memories/local/auth-flow.nmp2"),
    /// so the two-step scan → resolve workflow survives across processes. Each resolve re-reads the
    /// conflict markers from disk; there is no in-memory state shared between scan and resolve.
    /// </remarks>
    internal Task<McpResponse> Reconcile(
        [Description("Workspace-relative path under .scrinia/ to the conflicted file. Omit to scan.")] string? conflictId = null,
        [Description("Resolution: 'ours', 'theirs', or 'merged'. Required when conflictId is provided.")] string? choice = null,
        [Description("Content for 'merged' resolution.")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        // Both branches — scan and resolve — walk .scrinia/ on disk for conflict markers. A
        // remote store has no such directory, so there is no partial answer to give.
        if (!TryGetLayout(out var store))
            return Task.FromResult(ResponseBuilder.Error(
                "Reconcile reads and rewrites conflicted files under .scrinia/ on disk, and the active store is remote.",
                ErrorCodes.NotSupported,
                "Resolve merge conflicts in the workspace checkout, then retry there."));

        string scriniaDir = Path.Combine(store.WorkspaceRoot, ".scrinia");

        return conflictId is not null
            ? Task.FromResult(ResolveConflictByPath(scriniaDir, conflictId, choice, content))
            : Task.FromResult(ScanForConflicts(scriniaDir));
    }

    private static McpResponse ResolveConflictByPath(string scriniaDir, string conflictId, string? choice, string? mergedContent)
    {
        if (!TryResolveScriniaRelativePath(scriniaDir, conflictId, out string absolutePath, out string? pathError))
            return ResponseBuilder.Error(
                pathError!,
                ErrorCodes.InvalidParameter,
                "reconcile() to list current conflicts by relative path");

        if (!File.Exists(absolutePath))
            return ResponseBuilder.Error(
                $"Conflict '{conflictId}' not found at {absolutePath}.",
                ErrorCodes.NotFound,
                "reconcile() to list current conflicts by relative path");

        string fileContent;
        try { fileContent = File.ReadAllText(absolutePath); }
        catch (Exception ex)
        {
            return ResponseBuilder.Error(
                $"Reading {absolutePath}: {ex.Message}",
                ErrorCodes.Internal,
                "Verify filesystem permissions and retry.");
        }

        if (!fileContent.Contains("<<<<<<<"))
            return ResponseBuilder.Error(
                $"No conflict markers found in '{conflictId}'; nothing to resolve.",
                ErrorCodes.Conflict,
                "reconcile() to list current conflicts");

        string type = ClassifyConflictType(absolutePath);

        if (string.IsNullOrWhiteSpace(choice))
        {
            // No explicit choice: the only unattended resolution we support is the .meta.json
            // auto-merge (keyword union, latest timestamp). This is the ONLY place that write
            // is allowed to happen — scanning (ScanForConflicts) only reports it as possible.
            if (type == "meta.json" && TryComputeAutoResolvedMetaJson(fileContent, out string? autoResolvedJson))
            {
                try { File.WriteAllText(absolutePath, autoResolvedJson!); }
                catch (Exception ex)
                {
                    return ResponseBuilder.Error(
                        $"Writing resolved content to {absolutePath}: {ex.Message}",
                        ErrorCodes.Internal,
                        "Verify filesystem permissions and disk space, then retry the reconcile.");
                }

                return ResponseBuilder.Success(
                        $"Resolved {conflictId} (meta.json) via automatic merge (keyword union, latest timestamp wins).")
                    .WithAction("reconciled");
            }

            return ResponseBuilder.Error(
                "'choice' is required when resolving a conflict. Use 'ours', 'theirs', or 'merged'.",
                ErrorCodes.InvalidParameter,
                $"reconcile({{ conflictId: '{conflictId}', choice: 'ours' }})");
        }

        string normalisedChoice = choice.ToLowerInvariant();
        string resolvedContent;

        switch (normalisedChoice)
        {
            case "ours":
            case "theirs":
                if (!TryExtractConflictSide(fileContent, normalisedChoice, out string? extracted, out string? extractError))
                    return ResponseBuilder.Error(
                        $"{conflictId}: {extractError}",
                        ErrorCodes.Conflict,
                        $"reconcile({{ conflictId: '{conflictId}', choice: 'merged', mergedContent: '...' }})");

                // For .nmp2 the extracted region is an encoded artifact; decode for storage as plain text,
                // then re-encode on write below.
                if (type == "nmp2")
                {
                    string side = normalisedChoice == "ours" ? "current" : "incoming";
                    extracted = TryReadConflictCopy(absolutePath, side) ?? extracted;
                    try { extracted = System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(extracted!)); }
                    catch { /* fall through: write extracted as-is */ }
                }
                resolvedContent = extracted!;
                break;

            case "merged":
                if (string.IsNullOrEmpty(mergedContent))
                    return ResponseBuilder.Error(
                        "'merged' choice requires the mergedContent parameter.",
                        ErrorCodes.InvalidParameter,
                        $"reconcile({{ conflictId: '{conflictId}', choice: 'merged', mergedContent: 'merged text here' }})");
                if (type == "meta.json")
                {
                    try { JsonNode.Parse(mergedContent!); }
                    catch
                    {
                        return ResponseBuilder.Error(
                            "Merged content is not valid JSON for .meta.json conflict.",
                            ErrorCodes.Conflict,
                            "Provide JSON-parseable mergedContent for .meta.json conflicts.");
                    }
                }
                resolvedContent = mergedContent!;
                break;

            default:
                return ResponseBuilder.Error(
                    $"Invalid choice '{choice}'. Use 'ours', 'theirs', or 'merged'.",
                    ErrorCodes.InvalidParameter,
                    $"reconcile({{ conflictId: '{conflictId}', choice: 'ours' }})");
        }

        try
        {
            string toWrite = type == "nmp2" ? Nmp2ChunkedEncoder.Encode(resolvedContent) : resolvedContent;
            File.WriteAllText(absolutePath, toWrite);
        }
        catch (Exception ex)
        {
            return ResponseBuilder.Error(
                $"Writing resolved content to {absolutePath}: {ex.Message}",
                ErrorCodes.Internal,
                "Verify filesystem permissions and disk space, then retry the reconcile.");
        }

        return ResponseBuilder.Success($"Resolved {conflictId} ({type}) with '{choice}'.").WithAction("reconciled");
    }

    private static McpResponse ScanForConflicts(string scriniaDir)
    {
        var autoResolved = new List<string>();
        var needsManual = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(scriniaDir, "*", SearchOption.AllDirectories))
        {
            string fileContent;
            try { fileContent = File.ReadAllText(filePath); }
            catch { continue; }

            if (!fileContent.Contains("<<<<<<<")) continue;

            string relativePath = Path.GetRelativePath(scriniaDir, filePath).Replace('\\', '/');
            string type = ClassifyConflictType(filePath);

            if (type == "meta.json")
            {
                // Read-only: compute whether this conflict WOULD auto-resolve, but never write —
                // scanning must not mutate .scrinia/. The actual merge is written only when the
                // caller explicitly reconciles this conflictId (see ResolveConflictByPath).
                if (TryComputeAutoResolvedMetaJson(fileContent, out _))
                    autoResolved.Add(relativePath);
                else
                    needsManual.Add($"{relativePath} (.meta.json — auto-resolve failed)");
            }
            else if (type == "nmp2")
            {
                if (!TryExtractConflictSide(fileContent, "ours", out string? oursRaw, out string? extractError))
                {
                    needsManual.Add($"{relativePath} (.nmp2 — {extractError})");
                    continue;
                }
                TryExtractConflictSide(fileContent, "theirs", out string? theirsRaw, out _);

                string oursDecoded = TryDecodeNmp2(TryReadConflictCopy(filePath, "current") ?? oursRaw!);
                string theirsDecoded = TryDecodeNmp2(TryReadConflictCopy(filePath, "incoming") ?? theirsRaw ?? "");

                int afterFirstConflict = fileContent.IndexOf(">>>>>>>", StringComparison.Ordinal) + ">>>>>>>".Length;
                bool hasMore = afterFirstConflict < fileContent.Length
                    && fileContent.IndexOf("<<<<<<<", afterFirstConflict, StringComparison.Ordinal) >= 0;
                string multiNote = hasMore ? " (file has additional conflict regions — resolve manually)" : "";

                needsManual.Add($"{relativePath} (.nmp2 artifact){multiNote}\n    OURS:\n    {Indent(oursDecoded)}\n    THEIRS:\n    {Indent(theirsDecoded)}");
            }
            else
            {
                needsManual.Add($"{relativePath} (unknown file type)");
            }
        }

        if (autoResolved.Count == 0 && needsManual.Count == 0)
            return ResponseBuilder.Success("No merge conflicts found in .scrinia/.").WithAction("reconciled");

        // Nothing on disk was changed by this scan — autoResolved lists conflicts that CAN be
        // auto-merged, not ones that already were. Every entry here still needs an explicit
        // reconcile({ conflictId }) call to actually apply and persist the merge.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Merge conflict scan: {autoResolved.Count} auto-resolvable, {needsManual.Count} need manual resolution.");

        if (autoResolved.Count > 0)
        {
            sb.AppendLine("\nAuto-resolvable (call reconcile({ conflictId: '<path>' }) to apply):");
            foreach (var f in autoResolved) sb.AppendLine($"  OK {f}");
        }
        if (needsManual.Count > 0)
        {
            sb.AppendLine("\nNeeds manual resolution (pass the relative path as conflictId):");
            foreach (var f in needsManual) sb.AppendLine($"  FAIL {f}");
        }

        int pendingCount = autoResolved.Count + needsManual.Count;
        sb.Append($"\n{pendingCount} conflict(s) remaining.");

        var reconcileWarnings = new List<string>();
        if (needsManual.Count > 0)
            reconcileWarnings.Add($"{needsManual.Count} conflict(s) need manual resolution.");
        if (autoResolved.Count > 0)
            reconcileWarnings.Add(
                $"{autoResolved.Count} conflict(s) can be auto-resolved — call reconcile({{ conflictId: '<path>' }}) for each to apply and persist the merge.");

        return ResponseBuilder.Success(sb.ToString())
            .WithAction("reconciled")
            .WithActionNeeded(reconcileWarnings.ToArray());
    }

    private static bool TryResolveScriniaRelativePath(
        string scriniaDir, string conflictId, out string absolutePath, out string? error)
    {
        absolutePath = "";
        error = null;

        string normalised = conflictId.Replace('\\', '/').Trim();
        if (normalised.Length == 0)
        {
            error = "Conflict id is empty.";
            return false;
        }
        if (Path.IsPathRooted(normalised))
        {
            error = $"Conflict id '{conflictId}' must be a path relative to .scrinia/, not an absolute path.";
            return false;
        }

        string scriniaAbs = Path.GetFullPath(scriniaDir);
        string candidate = Path.GetFullPath(Path.Combine(scriniaAbs, normalised));
        string scriniaWithSep = scriniaAbs.EndsWith(Path.DirectorySeparatorChar)
            ? scriniaAbs
            : scriniaAbs + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(scriniaWithSep, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Conflict id '{conflictId}' resolves outside .scrinia/.";
            return false;
        }

        absolutePath = candidate;
        return true;
    }

    private static string ClassifyConflictType(string filePath)
    {
        if (filePath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) return "meta.json";
        if (filePath.EndsWith(".nmp2", StringComparison.OrdinalIgnoreCase)) return "nmp2";
        return "unknown";
    }

    private static bool TryExtractConflictSide(
        string fileContent, string side, out string? extracted, out string? error)
    {
        extracted = null;
        error = null;

        int markerStart = fileContent.IndexOf("<<<<<<<", StringComparison.Ordinal);
        if (markerStart < 0) { error = "no conflict markers"; return false; }
        int oursStartLine = fileContent.IndexOf('\n', markerStart);
        int separator = fileContent.IndexOf("=======", StringComparison.Ordinal);
        int theirsEnd = fileContent.IndexOf(">>>>>>>", StringComparison.Ordinal);
        if (oursStartLine < 0 || separator < 0 || theirsEnd < 0 || separator < markerStart || theirsEnd < separator)
        {
            error = "malformed conflict markers";
            return false;
        }
        int theirsStartLine = fileContent.IndexOf('\n', separator);
        if (theirsStartLine < 0) { error = "malformed conflict markers"; return false; }

        extracted = side == "ours"
            ? fileContent[(oursStartLine + 1)..separator].TrimEnd()
            : fileContent[(theirsStartLine + 1)..theirsEnd].TrimEnd();
        return true;
    }

    private static string TryDecodeNmp2(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        try { return System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(raw)); }
        catch { return raw; }
    }

    /// <summary>
    /// Whole conflict copy the merge driver wrote beside the artifact, or null when absent.
    /// The marker body is not guaranteed to be independently decodable — multi-chunk artifacts,
    /// extra conflict regions, or markers the driver did not write can leave it so; these copies
    /// always are.
    /// </summary>
    private static string? TryReadConflictCopy(string artifactPath, string side)
    {
        string dir = Path.GetDirectoryName(artifactPath)!;
        string candidate = Path.Combine(dir, ReadConflictDirName(dir), side, Path.GetFileName(artifactPath));
        if (!File.Exists(candidate)) return null;
        try { return File.ReadAllText(candidate); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>ConflictDir from .scrinia/merge.config, defaulting to "conflict".</summary>
    private static string ReadConflictDirName(string artifactDir)
    {
        var dir = new DirectoryInfo(artifactDir);
        while (dir is not null && !string.Equals(dir.Name, ".scrinia", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;
        if (dir is null) return "conflict";

        string configPath = Path.Combine(dir.FullName, "merge.config");
        if (!File.Exists(configPath)) return "conflict";
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath));
            string? configured = node?["conflictDir"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(configured) ? "conflict" : configured;
        }
        catch { return "conflict"; }
    }

    private static string Indent(string? text, string prefix = "      ")
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        // Show first 500 chars to keep output manageable
        var truncated = text.Length > 500 ? text[..500] + "..." : text;
        return truncated.Replace("\n", "\n" + prefix);
    }

    /// <summary>
    /// Computes the auto-merged JSON for a conflicted .meta.json file (keyword union, latest
    /// timestamp wins, max term-frequency counts) WITHOUT writing anything to disk. Callers
    /// decide whether/when to persist <paramref name="resolvedJson"/>.
    /// </summary>
    private static bool TryComputeAutoResolvedMetaJson(string conflictedContent, out string? resolvedJson)
    {
        resolvedJson = null;
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
                sortedKw.Add((JsonNode?)k);
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

            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            resolvedJson = baseNode.ToJsonString(writeOptions);

            return true;
        }
        catch
        {
            resolvedJson = null;
            return false; // If anything fails, report as needing manual resolution
        }
    }

    // ── Maintenance tools ──────────────────────────────────────────────────

    /// <summary>Compact a multi-chunk memory by merging chunks. Archives the original version.</summary>
    internal async Task<McpResponse> Compact(
        [Description("Memory name to compact.")] string name,
        [Description("Keep only the N most recent chunks. 0 = merge all into one (default).")] int keepRecent = 0,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        var (scope, subject) = store.ParseQualifiedName(name);

        string artifact;
        try
        {
            artifact = await store.ReadArtifactAsync(subject, scope, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error(
                $"Memory '{name}' not found.",
                ErrorCodes.NotFound,
                "list() to see available memories",
                "remember({ path: '...', content: [...] }) to create it");
        }
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        if (chunkCount <= 1)
            return ResponseBuilder.Success("Already a single chunk, nothing to compact.").WithAction("compacted");

        if (keepRecent > 0 && keepRecent >= chunkCount)
            return ResponseBuilder.Success($"Nothing to compact — keepRecent ({keepRecent}) >= chunk count ({chunkCount}).").WithAction("compacted");

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

        await store.WriteArtifactAsync(subject, scope, compacted, ct: cancellationToken);

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
            .WithFileChanges().WithAction("compacted");
    }

    /// <summary>Resume agent context — agent profile, patterns, session log, available skills.</summary>
    internal async Task<McpResponse> Restore(CancellationToken cancellationToken)
    {
        var ctx = await BuildRestoreContextAsync(cancellationToken);
        return ResponseBuilder.Success(string.Join("\n\n", ctx.ContentSections))
            .WithAction("restored")
            .WithActionNeeded(ctx.Warnings.ToArray())
            .WithInfo(ctx.Info.ToArray())
            .WithInstruction(ctx.Instruction)
            .WithFollowUp(ctx.FollowUpNames.ToArray());
    }

    /// <summary>
    /// Build the workspace-context blocks that <see cref="Restore"/> serialises into a YAML
    /// response. Extracted so <see cref="Guide"/> can fold the same signals into the cold-start
    /// guide call — the model gets workspace warnings, available skills, and followUp pointers
    /// on the first tool call without having to remember to call <c>restore</c> separately.
    /// </summary>
    internal async Task<RestoreContext> BuildRestoreContextAsync(CancellationToken cancellationToken)
    {
        // The whole store, not a narrowed one: the three local-only sections below are each skipped
        // when the store is remote, leaving a valid, degraded context. Narrowing here instead
        // would fail the whole call — including guide(), the first tool an agent calls in a
        // session. Each section tests for a local store rather than relying on its best-effort
        // catch to swallow a throw: same result, without making the normal remote path an
        // exception. The catches stay — they guard genuine I/O failures, which is their job.
        var store = CurrentStore;
        var warnings = new List<string>();
        var info = new List<string>();
        var contentSections = new List<string>();
        var followUpNames = new List<string>();
        string? instruction = null;

        // Check for unresolved merge conflicts in .scrinia/ — local stores only; a remote store
        // has no working copy to conflict.
        if (store is ILocalMemoryStore conflictLayout)
        {
            try
            {
                string resumeScriniaDir = Path.Combine(conflictLayout.WorkspaceRoot, ".scrinia");
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
                        warnings.Add(".scrinia/ has unresolved merge conflicts. Run reconcile before continuing.");
                }
            }
            catch { /* best-effort check */ }
        }

        // Available skills (built-in + disk overrides). Skipped entirely for a remote store:
        // the built-in names alone, with no disk overrides to reconcile them against, would
        // report a skill set the agent cannot actually load remotely.
        if (store is ILocalMemoryStore skillsLayout)
        {
            try
            {
                string skillsDir = PathRouter.ToFileEntityDir("skill", skillsLayout.WorkspaceRoot);
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
        }

        // Collect agent/* names for followUp — .md files first, NMP/2 fallback.
        // The whole section is local-only, fallback included: remotely the workspace-root read
        // threw into the catch below and took the fallback with it. Preserved deliberately —
        // widening it to run the NMP/2 branch remotely is a behaviour change, not a tidy-up.
        if (store is ILocalMemoryStore agentLayout)
        {
            try
            {
                string agentDir = PathRouter.ToFileEntityDir("agent", agentLayout.WorkspaceRoot);
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
                    var (agentScope, _) = store.ParseQualifiedName("/agent/placeholder");
                    var agentEntries = store.LoadIndex(agentScope);
                    foreach (var entry in agentEntries)
                        followUpNames.Add($"/agent/{entry.Name}");
                }
            }
            catch { /* agent scope not yet created — skip silently */ }
        }

        // Collect patterns/* names for followUp
        try
        {
            var (patternsScope, _) = store.ParseQualifiedName("/patterns/placeholder");
            var patternsEntries = store.LoadIndex(patternsScope);
            foreach (var entry in patternsEntries)
                followUpNames.Add($"/patterns/{entry.Name}");
        }
        catch { /* patterns scope not yet created — skip silently */ }

        // Checkpoint:latest — followUp if exists
        try
        {
            string checkpointArtifact = await store.ResolveArtifactAsync("/checkpoint/latest", cancellationToken);
            if (!string.IsNullOrEmpty(checkpointArtifact))
                followUpNames.Add("/checkpoint/latest");
        }
        catch (FileNotFoundException) { /* no checkpoint */ }

        // Today's session log — followUp if it exists
        try
        {
            string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            await store.ResolveArtifactAsync($"/sessions/{today}", cancellationToken);
            followUpNames.Add($"/sessions/{today}");
        }
        catch (FileNotFoundException) { /* no session log for today */ }

        if (contentSections.Count == 0)
            contentSections.Add("No persistent agent context found yet. Use remember to start storing notes, patterns, and skills.");

        if (followUpNames.Count > 0)
            instruction = "Call recall for each item in followUp to load full context.";

        return new RestoreContext(warnings, info, contentSections, followUpNames, instruction);
    }
}

/// <summary>
/// Building blocks for the workspace-restore response. Shared between
/// <see cref="ScriniaMcpTools.Restore"/> and <see cref="ScriniaMcpTools.Guide"/>.
/// </summary>
internal sealed record RestoreContext(
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Info,
    IReadOnlyList<string> ContentSections,
    IReadOnlyList<string> FollowUpNames,
    string? Instruction);
