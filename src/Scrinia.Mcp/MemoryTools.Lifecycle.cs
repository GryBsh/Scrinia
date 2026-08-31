using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Merge;
using Scrinia.Core.Models;

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
                try
                {
                    return (await Export(topics, bundlePath, cancellationToken)).ToYaml();
                }
                catch (RestoreRecoveryException exception)
                {
                    return RecoveryBlockedResponse(exception).ToYaml();
                }

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
        var store = CurrentStore;

        if (conflictId is null && !string.IsNullOrWhiteSpace(choice))
            return Task.FromResult(ResponseBuilder.Error(
                "'choice' requires 'conflictId' — without one there is no conflict to resolve, "
                + "and the call would silently perform a read-only scan instead.",
                ErrorCodes.InvalidParameter,
                "reconcile({ conflictId: '<path>', choice: 'ours' }) or reconcile({}) to scan"));

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
            if (type == "meta.json")
            {
                double threshold = GetJaccardThreshold(scriniaDir);
                if (TryComputeAutoResolvedMetaJson(fileContent, threshold, out string? autoResolvedJson))
                {
                    try { WriteResolved(absolutePath, autoResolvedJson!); }
                    catch (ScopeReadOnlyException ex)
                    {
                        return DurableWriteRefused(ex);
                    }
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

                // The gate refusal is a deliberate decision, not a parse failure — give it its own
                // message rather than falling into the generic "'choice' is required" error below,
                // which reads as though the sidecar were malformed instead of simply too different
                // to merge automatically.
                if (!MetaSidecarsAreSimilarEnough(fileContent, threshold))
                    return ResponseBuilder.Error(
                        $"'{conflictId}' cannot be auto-merged: the two sides' keywords are too " +
                        $"dissimilar (below the configured similarity threshold of " +
                        $"{threshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}). " +
                        "Set 'jaccardThreshold' in .scrinia/merge.config to change the threshold, " +
                        "or resolve explicitly.",
                        ErrorCodes.Conflict,
                        $"reconcile({{ conflictId: '{conflictId}', choice: 'ours' }}) or choice: 'theirs'/'merged'");

                // The second deliberate refusal, and it needs its own message for the same reason
                // the first one does: falling through to "'choice' is required" reads as though the
                // sidecar were malformed, when in fact it parsed perfectly and the two sides simply
                // state different facts about the same memory. Names the fields, because which one
                // disagrees is the whole of what a caller needs to decide between 'ours' and
                // 'theirs'.
                if (MetaSidecarsDisagreeOnCarriedFields(fileContent) is { } disagreeing)
                    return ResponseBuilder.Error(
                        $"'{conflictId}' cannot be auto-merged: the two sides disagree on " +
                        $"{disagreeing}, which the merge cannot decide — there is no common " +
                        "ancestor to tell which side changed it. Resolve explicitly.",
                        ErrorCodes.Conflict,
                        $"reconcile({{ conflictId: '{conflictId}', choice: 'ours' }}) or choice: 'theirs'/'merged'");
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
            WriteResolved(absolutePath, toWrite);
        }
        catch (ScopeReadOnlyException ex)
        {
            return DurableWriteRefused(ex);
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

    /// <summary>
    /// Writes conflict-resolved bytes back into the store and drops the store's cached views.
    /// </summary>
    /// <remarks>
    /// A repair is not a well-formed store write — it produces bytes for whichever path the
    /// conflict scan found, a <c>.nmp2</c> body or a <c>.meta.json</c> sidecar — so it does not
    /// go through <c>Upsert</c>, and forcing it through one would change the merge semantics the
    /// git driver and this auto-resolver deliberately share. But it must still behave like a
    /// store write in two respects a bare <c>File.WriteAllText</c> did not.
    /// <para>ATOMIC AND DURABLE: routed through the store's own temp-then-rename writer, so a
    /// concurrent reader never observes a half-repaired sidecar and the bytes reach media before
    /// the rename.</para>
    /// <para>VISIBLE: <c>_indexCache</c> is not keyed by mtime (only the artifact cache is), so
    /// without an explicit invalidation a long-lived MCP session went on serving the PRE-repair
    /// sidecar for the rest of the process — having just reported the resolve as successful.
    /// Both write sites go through this one helper rather than each remembering to do the same
    /// two things.</para>
    /// </remarks>
    /// <summary>Reads an <c>attributes</c>-shaped node — an object whose every value is an array
    /// of strings. ABSENT returns true with a null map; MALFORMED returns false so the caller
    /// escalates, matching <c>TryReadIntMap</c>'s contract and the driver's
    /// <c>TryGetStringArrayMap</c>.</summary>
    private static bool TryReadStringArrayMap(JsonNode? node, string key, out Dictionary<string, string[]>? map)
    {
        map = null;
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(key, out var n))
            return true; // absent
        if (n is not JsonObject inner)
            return false; // malformed: present but not an object

        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in inner)
        {
            if (value is not JsonArray arr) return false;
            var values = new List<string>(arr.Count);
            foreach (var element in arr)
            {
                if (element is JsonValue ev && ev.TryGetValue<string>(out string? s) && s is not null)
                    values.Add(s);
                else
                    return false;
            }
            result[name] = [.. values];
        }
        map = result;
        return true;
    }

    private static JsonObject ToJsonAttributeObject(Dictionary<string, string[]> map) =>
        new(map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
               .Select(kv => KeyValuePair.Create(
                   kv.Key,
                   (JsonNode?)new JsonArray(kv.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()))));

    private static void WriteResolved(string absolutePath, string content)
    {
        if (absolutePath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            content = NormalizeResolvedArtifactSidecar(content);

        if (absolutePath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            EnsureResolvedArtifactSidecarIsWritable(absolutePath, content);

        FileMemoryStore.AtomicWriteAllText(absolutePath, content);
        if (MemoryStoreContext.Current is FileMemoryStore store)
            store.InvalidateCaches();
    }

    private static string NormalizeResolvedArtifactSidecar(string content)
    {
        JsonNode? node = JsonNode.Parse(content);
        NormalizeLegacyArtifactKey(node);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? content;
    }

    private static void EnsureResolvedArtifactSidecarIsWritable(string absolutePath, string content)
    {
        ArtifactEntry entry = JsonSerializer.Deserialize(content, ScriniaMcpJsonContext.Default.ArtifactEntry)
            ?? throw new JsonException($"Resolved artifact sidecar '{absolutePath}' is empty.");

        if (FormatVersion.Evaluate(DurableFormats.ArtifactSidecar, entry.SchemaVersion) == FormatDisposition.Load)
            return;

        throw new ScopeReadOnlyException(
            Path.GetDirectoryName(absolutePath) ?? absolutePath,
            entry.Name,
            entry.SchemaVersion ?? DurableFormats.ArtifactSidecar.AbsentMeans ?? 0,
            DurableFormats.ArtifactSidecar.Current);
    }

    private static McpResponse DurableWriteRefused(ScopeReadOnlyException exception) =>
        ResponseBuilder.Error(
            exception.Message,
            ErrorCodes.DurableWriteFailed,
            "Upgrade scri to a version that supports this sidecar, or re-export the memory with a compatible scri version before resolving the conflict.");

    private static McpResponse ScanForConflicts(string scriniaDir)
    {
        var autoResolved = new List<string>();
        var needsManual = new List<string>();
        double threshold = GetJaccardThreshold(scriniaDir);

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
                if (TryComputeAutoResolvedMetaJson(fileContent, threshold, out _))
                    autoResolved.Add(relativePath);
                else if (!MetaSidecarsAreSimilarEnough(fileContent, threshold))
                    needsManual.Add($"{relativePath} (.meta.json — sides too dissimilar to merge automatically)");
                else if (MetaSidecarsDisagreeOnCarriedFields(fileContent) is { } disagreeing)
                    // Same three-way split the explicit reconcile reports, so the scan never
                    // describes a refusal as an "auto-resolve failure" when it was a decision.
                    needsManual.Add($"{relativePath} (.meta.json — sides disagree on {disagreeing})");
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

    /// <summary>
    /// The Jaccard admission threshold from <c>merge.config</c>, or 0.7.
    ///
    /// <para>The same file and the same default the git merge driver reads
    /// (<c>Scrinia.Merge.MergeConfig.Load</c>), so one workspace setting governs both routes to a
    /// sidecar merge. Read here rather than through <c>MergeConfig</c> because that type lives in
    /// the AOT driver assembly, which this project does not reference.</para>
    /// </summary>
    private static double GetJaccardThreshold(string scriniaDir)
    {
        string configPath = Path.Combine(scriniaDir, "merge.config");
        if (!File.Exists(configPath)) return 0.7;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath));
            return node?["jaccardThreshold"]?.GetValue<double>() ?? 0.7;
        }
        catch { return 0.7; }
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

    /// <summary>Matches <c>MetaJsonMerger.NodeOptions</c> on the driver side — see the comment in
    /// <see cref="TryExtractMetaSides"/> for why this is scoped to a conflicted sidecar's own two
    /// sides rather than applied to every parse in this file.</summary>
    private static readonly JsonNodeOptions CaseInsensitiveNodeOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Slices a conflicted .meta.json's marker body into "ours" and "theirs" JSON nodes — the ONE
    /// place this parsing happens.
    ///
    /// <para><see cref="TryComputeAutoResolvedMetaJson"/> and <see
    /// cref="MetaSidecarsAreSimilarEnough"/> both call this rather than each slicing the markers
    /// themselves. This file's own doctrine (see <see cref="TryReadIntMap"/>) is that a rule
    /// duplicated between the git merge driver and reconcile() is FORCED — the driver is a
    /// standalone AOT executable with no project references — and every other duplication is a
    /// defect waiting to happen: teach one caller to handle CRLF markers or a second conflict
    /// region and miss the other, and the scan starts mislabeling a parse failure as "too
    /// dissimilar" — the exact honesty gap Step 5 exists to close.</para>
    /// </summary>
    /// <returns>False when the markers are malformed or either side fails to parse as JSON.</returns>
    private static bool TryExtractMetaSides(
        string conflictedContent,
        [NotNullWhen(true)] out JsonNode? oursNode,
        [NotNullWhen(true)] out JsonNode? theirsNode)
    {
        oursNode = null;
        theirsNode = null;
        try
        {
            // Format: <<<<<<< HEAD\n{ours}\n=======\n{theirs}\n>>>>>>> {branch}
            int oursStart = conflictedContent.IndexOf("<<<<<<<");
            int separator = conflictedContent.IndexOf("=======");
            int theirsEnd = conflictedContent.IndexOf(">>>>>>>");
            if (oursStart < 0 || separator < 0 || theirsEnd < 0) return false;

            string beforeConflict = conflictedContent[..oursStart];
            string oursSection = conflictedContent[(conflictedContent.IndexOf('\n', oursStart) + 1)..separator];
            string theirsSection = conflictedContent[(conflictedContent.IndexOf('\n', separator) + 1)..theirsEnd];
            string afterConflict = conflictedContent[(conflictedContent.IndexOf('\n', theirsEnd) + 1)..];

            // Try to reconstruct valid JSON from each side
            string oursJson = beforeConflict + oursSection + afterConflict;
            string theirsJson = beforeConflict + theirsSection + afterConflict;

            // PropertyNameCaseInsensitive, matching MetaJsonMerger.NodeOptions on the driver side.
            // Without it, a hand-edited or foreign sidecar spelling a field "Keywords" (or
            // "CreatedAt", etc.) reads as ABSENT here while the driver's case-insensitive parse
            // reads it correctly — for the admission gate specifically, that turned two sides the
            // driver would compute a real Jaccard for into null/null on this side, which
            // KeywordJaccard.Compute treats as "identical" and admits on no evidence at all: the
            // two mergers disagreeing about the very thing this task exists to align. Scoped to
            // just these two parses (the conflicted sidecar's own two sides) rather than every
            // JsonNode.Parse call in this file — merge.config and the ad-hoc mergedContent
            // validity check elsewhere read different, codebase-defined schemas with no foreign
            // authorship, so they are not the same risk this closes.
            oursNode = JsonNode.Parse(oursJson, CaseInsensitiveNodeOptions);
            theirsNode = JsonNode.Parse(theirsJson, CaseInsensitiveNodeOptions);
            return oursNode is not null && theirsNode is not null;
        }
        catch { oursNode = null; theirsNode = null; return false; }
    }

    /// <summary>
    /// Whether a conflicted .meta.json's two sides pass the admission gate — read through the same
    /// <see cref="TryExtractMetaSides"/> slicing <see cref="TryComputeAutoResolvedMetaJson"/> uses,
    /// so the scan's classification always matches what an explicit reconcile would decide.
    ///
    /// <para>Used only to give <see cref="ScanForConflicts"/> an honest reason for a refusal: a
    /// gate refusal is a deliberate decision, not a parse failure, so it must not be reported the
    /// same way. Content that cannot be parsed returns <c>true</c> here — an unparseable side is a
    /// genuine auto-resolve failure, not a dissimilarity refusal, and <see
    /// cref="TryComputeAutoResolvedMetaJson"/> is the one place that distinction is reported.</para>
    ///
    /// <para><b>Wrapped in a catch, and it must stay that way.</b>
    /// <see cref="TryExtractMetaSides"/> parses with <c>PropertyNameCaseInsensitive</c>, and that
    /// option does NOT fail at parse time when a side carries two sibling keys differing only by
    /// case (e.g. both <c>"keywords"</c> and <c>"Keywords"</c>) — <c>JsonNode.Parse</c> succeeds,
    /// and the property dictionary throws <see cref="ArgumentException"/> ("An item with the same
    /// key has already been added") LAZILY, on the first property lookup. That lookup is
    /// <see cref="TryReadStrings"/> below, not the parse call above, so <c>TryExtractMetaSides</c>'s
    /// own try/catch cannot see it — the throw happens strictly after that method has already
    /// returned. Both callers of this method (<see cref="ResolveConflictByPath"/> and the
    /// <see cref="ScanForConflicts"/> loop) call it unguarded, so an uncaught throw here would
    /// abort the whole scan — every OTHER conflict in the workspace would vanish from the report
    /// because of one bad file — which is strictly worse than the honesty gap this method
    /// exists to close. Caught here, matching the same "unparseable → true → reported as
    /// auto-resolve failed, not too dissimilar" convention as an unparseable side above.</para>
    /// </summary>
    /// <summary>
    /// The fields the two sides state differently, as a caller-facing list — or null when they
    /// state nothing differently, or when the content cannot be read at all.
    ///
    /// <para>The reporting twin of <see cref="TryMergeCarriedFields"/>: that method decides, this
    /// one explains, and both read through <see cref="TryExtractMetaSides"/> so the explanation can
    /// never name a different set than the decision used. Re-parses rather than reusing the merge's
    /// nodes because <see cref="TryMergeCarriedFields"/> mutates ours in place; on a fresh parse
    /// that is invisible, and this runs only on the refusal path.</para>
    ///
    /// <para>Wrapped in a catch for the reason
    /// <see cref="MetaSidecarsAreSimilarEnough"/> documents at length — a duplicate case-variant key
    /// throws LAZILY on the first property read, and both callers are unguarded. Null on a throw,
    /// which reports the conflict as a generic auto-resolve failure: that is what it is.</para>
    /// </summary>
    private static string? MetaSidecarsDisagreeOnCarriedFields(string conflictedContent)
    {
        if (!TryExtractMetaSides(conflictedContent, out var oursNode, out var theirsNode))
            return null;

        try
        {
            var differing = new List<string>();
            if (!TryMergeCarried<string>(Probe(oursNode), theirsNode, "name")) differing.Add("name");
            if (!TryMergeCarried<string>(Probe(oursNode), theirsNode, "uri")) differing.Add("uri");
            if (!TryMergeCarried<string>(Probe(oursNode), theirsNode, "description")) differing.Add("description");
            if (!TryMergeCarried<long>(Probe(oursNode), theirsNode, "originalBytes")) differing.Add("originalBytes");
            if (!TryMergeCarried<int>(Probe(oursNode), theirsNode, "chunkCount")) differing.Add("chunkCount");

            return differing.Count == 0 ? null : string.Join(", ", differing);
        }
        catch { return null; }

        // TryMergeCarried writes theirs' value onto ours when ours has none. Harmless on this
        // throwaway parse, but passing a clone keeps this method a pure question.
        static JsonNode? Probe(JsonNode? node) => node?.DeepClone();
    }

    private static bool MetaSidecarsAreSimilarEnough(string conflictedContent, double threshold)
    {
        if (!TryExtractMetaSides(conflictedContent, out var oursNode, out var theirsNode))
            return true; // unparseable: a genuine auto-resolve failure, not a gate refusal

        try
        {
            if (!TryReadStrings(oursNode, "keywords", out var oursKeywords) ||
                !TryReadStrings(theirsNode, "keywords", out var theirsKeywords))
                // MALFORMED keywords (present but not an array, including an explicit JSON null):
                // a genuine auto-resolve failure, not a dissimilarity refusal — see the class doc
                // above. TryComputeAutoResolvedMetaJson is the one place that escalates this.
                return true;

            return SidecarAdmission.IsSimilarEnough(oursKeywords, theirsKeywords, threshold);
        }
        catch
        {
            // A duplicate case-variant key (e.g. both "keywords" and "Keywords") parses fine under
            // PropertyNameCaseInsensitive but throws ArgumentException on this first property read
            // — a genuine auto-resolve failure, not a dissimilarity refusal.
            return true;
        }
    }

    /// <summary>
    /// Computes the auto-merged JSON for a conflicted .meta.json file (keyword union, latest
    /// <c>updatedAt</c> but EARLIEST <c>createdAt</c>, max term-frequency counts) WITHOUT writing
    /// anything to disk. Callers decide whether/when to persist <paramref name="resolvedJson"/>.
    /// </summary>
    private static bool TryComputeAutoResolvedMetaJson(
        string conflictedContent, double jaccardThreshold, out string? resolvedJson)
    {
        resolvedJson = null;
        try
        {
            if (!TryExtractMetaSides(conflictedContent, out var oursNode, out var theirsNode))
                return false;

            NormalizeLegacyArtifactKey(oursNode);
            NormalizeLegacyArtifactKey(theirsNode);

            // Admission gate, matching the git merge driver's. Two sidecars can collide on a
            // filename while describing different memories; merging those is worse than a
            // conflict, because a conflict asks a human. Placed before any merge work so a
            // refusal is total.
            // MALFORMED keywords (present but not an array, including an explicit JSON null) must
            // escalate rather than be silently treated as absent — same rule as createdAt/updatedAt
            // and termFrequencies below, applied here because the admission gate is the first place
            // "keywords" is read.
            if (!TryReadStrings(oursNode, "keywords", out var oursKeywordsForGate) ||
                !TryReadStrings(theirsNode, "keywords", out var theirsKeywordsForGate))
                return false;

            if (!SidecarAdmission.IsSimilarEnough(
                    oursKeywordsForGate, theirsKeywordsForGate, jaccardThreshold))
                return false;

            // MALFORMED createdAt/updatedAt (present but not a JSON string — including an explicit
            // JSON null, which the node?[key] indexer below cannot distinguish from ABSENT) must
            // escalate rather than be silently treated as absent. Read all four up front, through
            // the one TryReadTimestampString helper, so base-node selection below and the shared
            // rule both see the SAME values — reading updatedAt twice with two different extraction
            // shapes is exactly how this divergence happened the first time.
            if (!TryReadTimestampString(oursNode, "createdAt", out var oursCreated) ||
                !TryReadTimestampString(oursNode, "updatedAt", out var oursUpdated) ||
                !TryReadTimestampString(theirsNode, "createdAt", out var theirsCreated) ||
                !TryReadTimestampString(theirsNode, "updatedAt", out var theirsUpdated))
                return false;

            // THE SUBSTRATE IS OURS, ALWAYS — and the fields this merge does not itself decide are
            // 3-way-merged rather than carried from a winner. Both halves are the git merge
            // driver's rule, adopted here (RULED 2026-08-15) to close a divergence that predates
            // this branch. What used to be here picked a whole base node by latest `updatedAt`,
            // defaulting to THEIRS, and wrote the merged fields onto it. That had two consequences,
            // both now closed and both pinned by
            // Scrinia.Tests.MetaMergeSymmetricParityTests, which drives ONE .meta.json pair through
            // this method and through MetaJsonMerger and compares the documents:
            //
            //  1. Every field this merge does NOT decide rode along on the base node. So a conflict
            //     where the two sides disagreed on `originalBytes`, `chunkCount`, `description`,
            //     `name` or `uri` — which the driver REFUSES, because with no merge base any
            //     disagreement is "both sides changed it" — resolved automatically here and kept
            //     the timestamp winner's copy. The standalone driver asked a human; this tool
            //     guessed, and because the default base was theirs, the side it discarded was the
            //     local one. `originalBytes`/`chunkCount` describe the artifact's physical shape,
            //     so the guess could leave a sidecar describing an artifact it does not match.
            //
            //  2. The keyword and term-frequency unions accumulate into case-INSENSITIVE
            //     collections, which keep the FIRST-inserted spelling. Seeding from the base node
            //     meant the surviving casing depended on which side happened to be newer, while the
            //     driver always seeds from ours. On identical input the two wrote
            //     keywords ["Auth","JWT"] / termFrequencies {"Auth":5} and ["auth","jwt"] /
            //     {"auth":5} respectively — one conflict, two machines, different committed bytes.
            //
            // Fixing (1) alone would not have fixed (2): the casing divergence needs the SUBSTRATE
            // to move to ours, and the escalation needs the gate. Hence both, together.
            //
            // NOTE the deliberate NON-symmetry with the driver: reconcile has no ancestor at all
            // (TryExtractMetaSides slices <<<<<<< / ======= / >>>>>>> and never |||||||), so it can
            // only reproduce the driver's ancestor-less behaviour, which is what TryMergeCarried
            // implements. Should diff3 conflict markers ever be handled, this becomes a real 3-way
            // merge and the gate loosens accordingly.
            if (!TryMergeCarriedFields(oursNode, theirsNode))
                return false;

            // SCHEMA VERSION. Refuse to auto-resolve when the two sides declare different
            // versions: this resolver picks a base node and overlays onto it, so the loser's
            // version and any field it carries that this build cannot model are dropped. That is
            // the cross-machine case the sidecar versioning exists for, and it is why the git
            // driver escalates here too. The parity suite pins the two against each other.
            if (ReadSchemaVersion(oursNode) != ReadSchemaVersion(theirsNode))
                return false;

            var baseNode = oursNode;
            var otherNode = theirsNode;

            // Carry over keys that exist only on the OTHER side and that nothing here models.
            // Without this the base-node overlay silently discards the other machine's unknown
            // fields on every automatic reconcile -- the same loss the git driver had, reached by
            // the second of the two implementations this rule is forced to have.
            //
            // Only UNMODELLED keys: the merge logic below deliberately REMOVES some modelled ones
            // (keywords when both sides have none, agentKeywords when only one side recorded it),
            // and a blanket copy would resurrect exactly those deletions.
            if (baseNode is JsonObject baseObj && otherNode is JsonObject otherObj)
                foreach (var kv in otherObj)
                    if (!baseObj.ContainsKey(kv.Key) && !ModelledSidecarKeys.Contains(kv.Key))
                        baseObj[kv.Key] = kv.Value?.DeepClone();

            // The timestamps come from the ONE rule, not from whichever node won the base
            // selection above. Riding along meant createdAt took the winner's value — so an
            // automatic reconcile could move a memory's creation time FORWARD.
            var (mergedCreated, mergedUpdated) = SidecarTimestampMerge.Select(
                oursCreated, oursUpdated, theirsCreated, theirsUpdated);
            if (mergedCreated is null)
                (baseNode as JsonObject)?.Remove("createdAt");
            else
                baseNode["createdAt"] = mergedCreated;
            if (mergedUpdated is null)
                (baseNode as JsonObject)?.Remove("updatedAt");
            else
                baseNode["updatedAt"] = mergedUpdated;

            // Union the keywords and their provenance, through the ONE rule
            // (Scrinia.Core.Merge.SidecarKeywordMerge). This used to be a hand-rolled keyword
            // union that left `agentKeywords` to ride along on whichever side won the timestamp
            // comparison — so an automatic reconcile recorded the losing side's curated keywords
            // as auto-extracted while reporting a clean merge, which is the same defect the git
            // merge driver had already been fixed for. Two implementations of this rule are forced
            // (the driver is AOT with no project references); a third was not.
            if (!TryReadStrings(baseNode, "keywords", out var baseKeywords) ||
                !TryReadStrings(baseNode, "agentKeywords", out var baseAgentKeywords) ||
                !TryReadStrings(otherNode, "keywords", out var otherKeywords) ||
                !TryReadStrings(otherNode, "agentKeywords", out var otherAgentKeywords))
                return false;

            var (mergedKeywords, mergedAgentKeywords) = SidecarKeywordMerge.Union(
                baseKeywords, baseAgentKeywords, otherKeywords, otherAgentKeywords);

            baseNode["keywords"] = ToJsonArray(mergedKeywords);
            if (mergedAgentKeywords is null)
                (baseNode as JsonObject)?.Remove("agentKeywords");
            else
                baseNode["agentKeywords"] = ToJsonArray(mergedAgentKeywords);

            // Union the term frequencies through the ONE rule
            // (Scrinia.Core.Merge.SidecarTermFrequencyMerge). This used to require BOTH sides to
            // carry a map, so a memory whose base recorded none and whose other side did lost them
            // whole — a silent ranking change, since term frequencies are what BM25 scores against.
            //
            // ABSENT (the key is missing) contributes nothing — a normal side of the union.
            // MALFORMED (present but not an object, or holding a value that isn't an int) used to
            // throw inside GetValue<int>() and let the outer catch escalate to manual resolution;
            // returning false here restores exactly that failure mode instead of silently
            // "resolving" the conflict minus the key it couldn't read.
            if (!TryReadIntMap(baseNode, "termFrequencies", out var baseTf) ||
                !TryReadIntMap(otherNode, "termFrequencies", out var otherTf))
                return false;

            var mergedTf = SidecarTermFrequencyMerge.Union(baseTf, otherTf);
            if (mergedTf is null)
                (baseNode as JsonObject)?.Remove("termFrequencies");
            else
                baseNode["termFrequencies"] = ToJsonObject(mergedTf);

            // Union the attributes through the ONE rule (Scrinia.Core.Merge.SidecarAttributeMerge).
            // This resolver never looked at attributes at all, so whichever side lost the timestamp
            // selection lost its attributes with it — and `ref` is an attribute, so a link written
            // on one branch simply disappeared when that branch merged.
            //
            // Same ABSENT/MALFORMED contract as termFrequencies above. A false return from TryUnion
            // is a different thing: a scalar key both sides set to different values, which nothing
            // here can order, so it escalates to manual resolution rather than picking a winner.
            if (!TryReadStringArrayMap(baseNode, "attributes", out var baseAttrs) ||
                !TryReadStringArrayMap(otherNode, "attributes", out var otherAttrs))
                return false;

            if (!SidecarAttributeMerge.TryUnion(baseAttrs, otherAttrs, out var mergedAttrs))
                return false;

            if (mergedAttrs is null)
                (baseNode as JsonObject)?.Remove("attributes");
            else
                baseNode["attributes"] = ToJsonAttributeObject(mergedAttrs);

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

    /// <summary>
    /// The fields the sidecar merge does not itself decide: <c>name</c>, <c>uri</c>,
    /// <c>description</c>, <c>originalBytes</c>, <c>chunkCount</c>. 3-way-merged against an ABSENT
    /// ancestor, which is the only ancestor reconcile can have — mirroring
    /// <c>MetaJsonMerger.TryMergeField</c> exactly:
    /// <list type="bullet">
    /// <item>the two sides agree → nothing to do;</item>
    /// <item>one side has no readable value → take the other, because a side that never set the
    /// field did not change it;</item>
    /// <item>both have readable values and they differ → <b>refuse</b>. With no merge base that is
    /// "both sides changed it to something different", which no merge can decide.</item>
    /// </list>
    ///
    /// <para><b>Mirrors the driver's escalation set exactly, and that is the point.</b> The review
    /// estimate for this gate proposed refusing on "any non-merged property whose JSON differs",
    /// on the reasoning that a fixed field list reopens the gap the moment a sidecar field is
    /// added. Measured against the driver, that formulation is wrong: it would refuse conflicts
    /// <c>MetaJsonMerger</c> RESOLVES — it overlays onto ours and carries every property it does not
    /// know about verbatim (<c>MetaJsonMerger.cs:104-107</c>), escalating on these five and nothing
    /// else. A broader gate here would have replaced one divergence with its mirror image. The
    /// duplication is the same forced duplication as every other rule these two share, and
    /// <c>MetaMergeSymmetricParityTests</c> is what catches it drifting.</para>
    ///
    /// <para><b>In practice the field list is not the fragile part.</b> Two conflicted sidecars for
    /// the same memory with different content differ on <c>originalBytes</c> and <c>chunkCount</c>
    /// — the artifact's physical shape — so the realistic case is caught by those two regardless of
    /// what else a future sidecar carries.</para>
    ///
    /// <para><b>Known residual, unreachable from any sidecar this codebase writes.</b> When NEITHER
    /// side has a readable <c>originalBytes</c>/<c>chunkCount</c>, the driver writes <c>0</c>
    /// (<c>originalBytesN ?? GetLong(ours, …) ?? 0</c>) while this leaves the field as it found it.
    /// Not replicated: writing a fabricated <c>0</c> over an unreadable value is a wart, not the
    /// rule being adopted, and <c>ArtifactEntry</c> declares both as required positional members so
    /// every sidecar Scrinia serializes carries them.</para>
    /// </summary>
    /// <returns><see langword="false"/> when the two sides disagree — the caller escalates to
    /// manual resolution.</returns>
    /// <summary>
    /// Every sidecar key this build models. A key OUTSIDE this set is one neither end understands,
    /// which is exactly what must survive an automatic reconcile rather than be dropped.
    ///
    /// <para><b>Twin of <c>MetaJsonMerger.ModelledKeys</c>.</b> Change one and you must change the
    /// other; <c>MetaMergeSymmetricParityTests</c> is what enforces that. The duplication is forced
    /// — the git driver is AOT with no project references — and is the same duplication the
    /// keyword and timestamp rules already carry.</para>
    /// </summary>
    private static readonly HashSet<string> ModelledSidecarKeys = new(StringComparer.Ordinal)
    {
        "name", "uri", "artifactKey", "originalBytes", "chunkCount", "createdAt", "updatedAt",
        "contentPreview", "description", "keywords", "agentKeywords",
        "termFrequencies", "chunkEntries", "codeRefs", "attributes", "fields", "v"
    };

    /// <summary>The declared schema version, or null when absent. Absent is NOT normalised to the
    /// current version here: the comparison this feeds only asks whether the two sides AGREE, and
    /// normalising would make "absent" and an explicit "1" compare equal in one implementation and
    /// not the other.</summary>
    private static int? ReadSchemaVersion(JsonNode? node) =>
        node is JsonObject obj && obj.TryGetPropertyValue("v", out var v)
            && v is JsonValue jv && jv.TryGetValue<int>(out int parsed)
                ? parsed
                : null;

    private static void NormalizeLegacyArtifactKey(JsonNode? node)
    {
        if (node is not JsonObject sidecar || sidecar.ContainsKey("artifactKey"))
            return;

        if (sidecar.TryGetPropertyValue("name", out var nameNode) &&
            nameNode is JsonValue nameValue && nameValue.TryGetValue<string>(out string? name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            string relativeName = name.TrimStart('/', '~').Replace('\\', '/');
            sidecar["artifactKey"] = $"artifacts/{relativeName}.nmp2";
            sidecar["address"] = name.StartsWith('/') || name.StartsWith('~') ? name : $"/{name}";
            sidecar.Remove("uri");
        }
    }

    private static bool TryMergeCarriedFields(JsonNode? oursNode, JsonNode? theirsNode) =>
        // Typed per field, and in the driver's own order: string for name/artifactKey/description, long for
        // originalBytes, int for chunkCount. Reading them all as untyped JSON scalars instead would
        // NOT be the same rule — a hand-edited sidecar carrying "20" (a string) where the other side
        // carries 20 (a number) reads as UNREADABLE to the driver's GetLong, which then takes the
        // other side, but would read as "present and different" to an untyped comparison and refuse.
        TryMergeCarried<string>(oursNode, theirsNode, "name")
        && TryMergeCarried<string>(oursNode, theirsNode, "artifactKey")
        && TryMergeCarried<string>(oursNode, theirsNode, "description")
        && TryMergeCarried<long>(oursNode, theirsNode, "originalBytes")
        && TryMergeCarried<int>(oursNode, theirsNode, "chunkCount");

    /// <summary>One field of <see cref="TryMergeCarriedFields"/>. See its doc for the rule.</summary>
    private static bool TryMergeCarried<T>(JsonNode? oursNode, JsonNode? theirsNode, string key)
    {
        bool oursOk = TryReadScalar<T>(oursNode, key, out T? ours, out _);
        bool theirsOk = TryReadScalar<T>(theirsNode, key, out T? theirs, out JsonNode? theirsRaw);

        // Ours never set a readable value, so ours did not change it — take theirs, exactly as
        // TryMergeField does when ours equals the (absent) ancestor. The raw node is cloned rather
        // than rebuilt from the typed value so the written JSON is byte-for-byte theirs'.
        if (!oursOk)
        {
            if (theirsOk && oursNode is JsonObject o)
                o[key] = theirsRaw!.DeepClone();
            return true;
        }

        if (!theirsOk) return true;

        return EqualityComparer<T>.Default.Equals(ours!, theirs!);
    }

    /// <summary>A property read as <typeparamref name="T"/> — <c>TryGetPropertyValue</c>, then
    /// <c>is JsonValue</c>, then <c>TryGetValue&lt;T&gt;</c>: the exact shape
    /// <c>MetaJsonMerger</c>'s <c>GetString</c>/<c>GetLong</c>/<c>GetInt</c> share. Absent, not a
    /// JSON scalar, and present-but-wrong-type are all one answer — false — because they are one
    /// answer (null) there.</summary>
    private static bool TryReadScalar<T>(JsonNode? node, string key, out T? value, out JsonNode? raw)
    {
        value = default;
        raw = null;
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var n) || n is not JsonValue v)
            return false;
        raw = n;
        return v.TryGetValue(out value);
    }

    /// <summary>Reads a keywords/agentKeywords-shaped JSON node into a string array. Returns true for
    /// ABSENT (<paramref name="value"/> is null; load-bearing for <c>agentKeywords</c>, where absent
    /// means UNKNOWN provenance) and for a well-formed JSON array of strings. Returns false for
    /// MALFORMED input — present but not a JSON array (including an explicit JSON <c>null</c>), or an
    /// array containing an element that isn't a JSON string — so the caller can escalate to manual
    /// resolution instead of what the <c>node?[key]</c> indexer used to do here: return a null
    /// reference for BOTH "key absent" and "key present with value JSON <c>null</c>," silently
    /// treating the latter as absent and letting a foreign writer's malformed <c>keywords</c>
    /// auto-resolve on the other side's value, or (for <c>agentKeywords</c>) be misread as UNKNOWN
    /// provenance instead of escalating.
    ///
    /// <para>Deliberately mirrors <see cref="TryReadTimestampString"/> and <see cref="TryReadIntMap"/>'s
    /// two-step shape (<c>JsonObject.TryGetPropertyValue</c> before inspecting the value) for the
    /// same reason those exist: the indexer cannot tell "absent" from "present but null" apart, and
    /// getting that wrong here would make the git merge driver and reconcile()'s auto-resolver
    /// classify a malformed <c>keywords</c> differently — the array analogue of the divergence
    /// <see cref="TryReadIntMap"/> closes for <c>termFrequencies</c>. Refusing a non-string element,
    /// rather than skipping it, is the same rule applied one level deeper: dropping one keyword out
    /// of an array with no signal is data loss, and this whole rule's premise is escalate-on-malformed
    /// over silently-absorb.</para></summary>
    private static bool TryReadStrings(JsonNode? node, string key, out string[]? value)
    {
        value = null;
        if (node is not JsonObject nodeObj || !nodeObj.TryGetPropertyValue(key, out var raw))
            return true; // absent
        if (raw is not JsonArray array)
            return false; // malformed: present but not a JSON array (includes an explicit JSON null)

        var values = new List<string>(array.Count);
        foreach (var element in array)
        {
            if (element is not JsonValue v || !v.TryGetValue<string>(out var s))
                return false; // malformed: an array element isn't a JSON string
            values.Add(s);
        }
        value = [.. values];
        return true;
    }

    private static JsonArray ToJsonArray(string[] values)
    {
        var array = new JsonArray();
        foreach (string value in values) array.Add((JsonNode?)value);
        return array;
    }

    /// <summary>Reads a createdAt/updatedAt-shaped JSON node into a string. Returns true for ABSENT
    /// (the key is missing; contributes nothing to <see cref="SidecarTimestampMerge.Select"/>) and
    /// for a well-formed JSON string. Returns false for MALFORMED input — present but not a JSON
    /// string, including an explicit JSON <c>null</c> — so the caller can escalate to manual
    /// resolution instead of what the <c>node?[key]</c> indexer used to do here: return a null
    /// reference for BOTH "key absent" and "key present with value JSON <c>null</c>", silently
    /// treating the latter as absent and auto-resolving on the other side's value.
    ///
    /// <para>Deliberately mirrors <see cref="TryReadIntMap"/>'s two-step shape
    /// (<c>JsonObject.TryGetPropertyValue</c> before inspecting the value) for the same reason that
    /// one exists: the indexer cannot tell "absent" from "present but null" apart, and getting that
    /// wrong here made the git merge driver and reconcile()'s auto-resolver classify
    /// <c>"createdAt": null</c> differently — the timestamp analogue of the divergence
    /// <see cref="TryReadIntMap"/> closes for <c>termFrequencies</c>.</para></summary>
    private static bool TryReadTimestampString(JsonNode? node, string key, out string? value)
    {
        value = null;
        if (node is not JsonObject nodeObj || !nodeObj.TryGetPropertyValue(key, out var raw))
            return true; // absent
        if (raw is not JsonValue v || !v.TryGetValue<string>(out var s))
            return false; // malformed: present but not a JSON string (includes an explicit JSON null)
        value = s;
        return true;
    }

    /// <summary>Reads a termFrequencies-shaped JSON node into a dictionary. Returns true for
    /// ABSENT (<paramref name="map"/> is null; the side recorded no term frequencies and
    /// contributes nothing to a merge) and for a well-formed object. Returns false for MALFORMED
    /// input — present but not a JSON object (including an explicit JSON <c>null</c>), or
    /// containing a value that isn't readable as an int — so the caller can escalate to manual
    /// resolution rather than silently drop or coerce a hand-edited or foreign sidecar's term
    /// frequencies.
    ///
    /// <para>Deliberately mirrors <c>MetaJsonMerger.TryGetIntMap</c>'s two-step shape
    /// (<c>JsonObject.TryGetPropertyValue</c> before inspecting the value) rather than the
    /// single-step <c>node?[key]</c> indexer. The indexer returns a null reference for BOTH "key
    /// absent" and "key present with value JSON <c>null</c>" — System.Text.Json represents a JSON
    /// null as a null <see cref="JsonNode"/> reference — so it cannot tell the two apart. Getting
    /// that wrong here made the git merge driver and reconcile()'s auto-resolver classify
    /// <c>"termFrequencies": null</c> differently: exactly the divergence this whole rule exists to
    /// close.</para></summary>
    private static bool TryReadIntMap(JsonNode? node, string key, out Dictionary<string, int>? map)
    {
        if (node is not JsonObject nodeObj || !nodeObj.TryGetPropertyValue(key, out var value))
        {
            map = null;
            return true; // absent
        }
        if (value is not JsonObject obj)
        {
            map = null;
            return false; // malformed: present but not an object (includes an explicit JSON null)
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // TryGetValue, not GetValue<int>() — the latter throws on a non-integer value, and a
        // hand-edited sidecar is exactly the input this method has to survive by reporting
        // MALFORMED rather than crashing.
        foreach (var (name, propertyValue) in obj)
        {
            if (propertyValue is JsonValue v && v.TryGetValue<int>(out int i))
            {
                result[name] = i;
            }
            else
            {
                map = null;
                return false; // malformed: a value isn't readable as an int
            }
        }
        map = result;
        return true;
    }

    private static JsonObject ToJsonObject(Dictionary<string, int> map)
    {
        var obj = new JsonObject();
        foreach (var (term, count) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            obj[term] = count;
        return obj;
    }

    // ── Maintenance tools ──────────────────────────────────────────────────

    /// <summary>Compact a multi-chunk memory by merging chunks. Archives the original version,
    /// except for an ephemeral (no versions/ directory) or a persistent artifact with no index
    /// entry yet — see <c>hadIndexedEntry</c> below.</summary>
    internal async Task<McpResponse> Compact(
        [Description("Memory name to compact.")] MemoryAddress name,
        [Description("Keep only the N most recent chunks. 0 = merge all into one (default).")] int keepRecent = 0,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        var (scope, subject) = name.Kind == MemoryAddressKind.Ephemeral
            ? (string.Empty, name.ToString())
            : store.ResolveAddress(name);
        if (name.Kind == MemoryAddressKind.Persistent && subject.Length > MemoryWrite.MaxPersistentSubjectLength)
            return ResponseBuilder.Error(
                $"Memory subject exceeds {MemoryWrite.MaxPersistentSubjectLength} sanitized characters.",
                ErrorCodes.InvalidParameter,
                $"Use a persistent memory subject of at most {MemoryWrite.MaxPersistentSubjectLength} sanitized characters.");

        // Ephemerals live in memory, not on disk. ParseQualifiedName still runs above (it does not
        // strip the '~', so subject stays harmless for this branch) — the read itself has to be
        // resolved from the ephemeral map instead of the disk-only ReadArtifactAsync.
        bool ephemeral = name.Kind == MemoryAddressKind.Ephemeral;
        string ephemeralKey = ephemeral ? name.Value : name.ToString();
        EphemeralEntry? ephemeralEntry = ephemeral ? store.GetEphemeral(name) : null;

        string artifact;
        if (ephemeral)
        {
            // ReadArtifactAsync is disk-only, so this method used to report a memory list() will
            // happily show as "not found" — and then suggest running list() to find it.
            if (ephemeralEntry is null)
                return ResponseBuilder.Error(
                    $"Memory '{name}' not found.",
                    ErrorCodes.NotFound,
                    "list() to see available memories",
                    "remember({ path: '...', content: [...] }) to create it");
            artifact = ephemeralEntry.Artifact;
        }
        else
        {
            try
            {
                artifact = await store.ReadArtifactAsync(name, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return ResponseBuilder.Error(
                    $"Memory '{name}' not found.",
                    ErrorCodes.NotFound,
                    "list() to see available memories",
                    "remember({ path: '...', content: [...] }) to create it");
            }
        }
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        if (chunkCount <= 1)
            return ResponseBuilder.Warning("Already a single chunk, nothing to compact.").WithAction("compacted");

        if (keepRecent > 0 && keepRecent >= chunkCount)
            return ResponseBuilder.Warning($"Nothing to compact — keepRecent ({keepRecent}) >= chunk count ({chunkCount}).").WithAction("compacted");

        // The compacted CHUNKS, not a re-encoded artifact: the store encodes what it is given, and
        // handing it the chunk array is what keeps the chunk boundaries a caller asked to keep
        // (keepRecent > 1) from being flattened into one on the way through. It also archives the
        // previous version itself, so the explicit ArchiveVersion this method used to make first is
        // gone — making it here as well would archive twice.
        string[] compacted;
        int newChunkCount;

        // Every chunk as it stands BEFORE the rewrite, decoded once — one pass, CRC-checked per
        // chunk, rather than a whole-artifact decode plus a per-chunk decode of the survivors.
        // The pre-compact chunks are needed in their own right and not just as a source of the
        // kept ones: the legacy keyword fallback below analyzes them.
        (string[] priorChunks, _) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact);

        if (keepRecent <= 0)
        {
            // Merge all chunks into one
            compacted = [string.Concat(priorChunks)];
            newChunkCount = 1;
        }
        else if (keepRecent == 1)
        {
            // Keep only the last chunk as a single-chunk memory
            compacted = [priorChunks[^1]];
            newChunkCount = 1;
        }
        else
        {
            // Keep the N most recent chunks
            compacted = priorChunks[^keepRecent..];
            newChunkCount = keepRecent;
        }

        // Read the existing entry BEFORE the write, not after. Compaction rewrites content, not
        // metadata, so the entry's attributes and curated description must survive it — and they
        // survive by travelling INTO the write as caller input, because a re-store derives its
        // whole metadata half from what it is handed. Reading after the write would sample the
        // already-rewritten entry.
        //
        // For an ephemeral, that existing entry comes from the map, not the index: an index lookup
        // finds nothing for a "~name" and would send D-29's provenance read to its fallback
        // heuristic below with the real answer sitting right there in ephemeralEntry.
        //
        // hadIndexedEntry is hoisted alongside the other locals (rather than leaving existingEntry
        // in scope) because the "Original archived" message below needs it and existingEntry itself
        // is scoped to the else branch — it means nothing for an ephemeral, which has no versions/
        // directory to archive into.
        string? existingPreview;
        Dictionary<string, string[]>? existingAttributes;
        string[]? existingAgentKeywords;
        string[]? existingKeywords;
        bool hadIndexedEntry;

        if (ephemeral)
        {
            existingPreview = ephemeralEntry!.ContentPreview;
            existingAttributes = ephemeralEntry.Attributes;
            existingAgentKeywords = ephemeralEntry.AgentKeywords;
            existingKeywords = ephemeralEntry.Keywords;
            hadIndexedEntry = false;
        }
        else
        {
            // Case-insensitive match plus canonical-casing carry-forward, the same rule every
            // other name lookup in this codebase follows. Here an ordinal match sent the write out
            // with no metadata at all, which is precisely the reset this read exists to prevent.
            var entries = store.LoadIndex(scope);
            var existingEntry = entries.FirstOrDefault(
                e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
            if (existingEntry is not null)
                subject = existingEntry.Name;
            existingPreview = existingEntry?.ContentPreview;
            existingAttributes = existingEntry?.Attributes;
            existingAgentKeywords = existingEntry?.AgentKeywords;
            existingKeywords = existingEntry?.Keywords;
            hadIndexedEntry = existingEntry is not null;
        }

        if (!ephemeral)
        {
            string qualifiedAddress = scope == "local"
                ? "/" + subject
                : "/" + scope["local:".Length..] + "/" + subject;
            name = MemoryAddress.Parse(qualifiedAddress);
        }

        // Compaction is a whole-memory rewrite, so it is a StoreAsync over the compacted chunks —
        // the spec's "compact needs no third derivation entry point". The store archives the
        // previous version, writes the artifact and upserts the entry, all of which this method
        // used to do for itself.
        //
        // ONLY THE AGENT'S OWN KEYWORDS TRAVEL (D-29). The entry's Keywords are the MERGE of the
        // agent's list with the auto-extracted terms, so handing the whole thing back in as
        // MemoryWrite.Keywords would reclassify every auto term as agent-supplied and boost it +5
        // instead of +2 — on every compaction, compounding. Handing back none loses the agent's
        // keywords instead. The entry records which were the agent's (ArtifactEntry.AgentKeywords),
        // written by every store and append, so for anything written since that field arrived this
        // is not a guess: it is D-29's first option — "only the memory's genuinely-curated
        // keywords" — read off the entry rather than reconstructed.
        //
        // For an entry that predates the field (null, meaning UNKNOWN — not "none") the fallback
        // is a documented HEURISTIC over the pre-compact chunks. It under-recovers and can
        // over-recover; see CuratedKeywords. The entry stops being unknown as soon as this
        // compaction writes it.
        //
        // Attributes and the curated description DO travel whole: they are caller-supplied state,
        // not derivation output. Note that they are MERGED OVER what the compacted text yields,
        // not simply restored, so a compaction inherits the whole store path — including D-05: if
        // the surviving text is count-shaped, the memory gains
        // review: "when counts in this memory change" that it did not have before.
        string[]? curatedKeywords = existingAgentKeywords
            ?? MemoryDerivation.CuratedKeywords(existingKeywords, priorChunks);

        // The ORIGINAL name, not `subject`, when ephemeral: StoreAsync's IsEphemeral check reads
        // MemoryWrite.Name directly, and only the "~"-prefixed original carries that prefix —
        // `subject` here is a sanitized-but-still-tilde-prefixed value from ParseQualifiedName that
        // exists only to keep the non-ephemeral branch working.
        MemoryWrite write;
        try
        {
            write = MemoryWrite.Create(name, scope, compacted,
                WriteAttributesAsBytes(existingAttributes ?? new Dictionary<string, string[]>()),
                curatedKeywords ?? [], description: existingPreview);
        }
        catch (InvalidMemoryWriteException ex)
        {
            return ResponseBuilder.Error(
                ex.Message,
                ErrorCodes.InvalidParameter,
                $"Use a persistent memory subject of at most {MemoryWrite.MaxPersistentSubjectLength} sanitized characters.");
        }

        var stored = await store.StoreAsync(write, cancellationToken);

        string qualifiedName = ephemeral ? $"~{stored.Name}" : store.FormatQualifiedName(scope, stored.Name);
        await FireEventSinkAsync(sink => sink.OnStoredAsync(
            stored.Address, write.RawContent, store, cancellationToken));
        int dropped = chunkCount - newChunkCount;
        // "Original archived" only when there was an indexed entry to archive: StoreAsync archives
        // the previous version when the memory is already in the index, and an artifact with no
        // index entry is a first write as far as it is concerned. Claiming an archive that did not
        // happen is the kind of report a user acts on before deleting something. An ephemeral never
        // archives — it has no versions/ directory — so hadIndexedEntry is unconditionally false
        // for that branch above.
        string archived = hadIndexedEntry ? " Original archived." : "";
        var response = ResponseBuilder.Success(
            $"Compacted {qualifiedName}: {chunkCount} -> {newChunkCount} chunk{(newChunkCount == 1 ? "" : "s")} ({dropped} dropped).{archived}");
        if (!ephemeral)
            response = response.WithFileChanges();
        return response.WithAction("compacted");
    }

    /// <summary>Resume agent context — agent profile, patterns, session log, available skills.</summary>
    internal async Task<McpResponse> Restore(CancellationToken cancellationToken)
    {
        var ctx = await BuildRestoreContextAsync(cancellationToken);
        // ContentSections always has exactly one entry (the skills line, or the "nothing found"
        // fallback) — see BuildRestoreContextAsync — so its count never varies and reporting it
        // here would read as "Restored: 1 context section(s)" on a workspace where nothing was
        // actually found. Report the counts that do vary instead, and special-case the empty
        // state via the explicit HasStoredContext signal rather than string-matching the
        // fallback text.
        string summary = ctx.HasStoredContext
            ? $"Restored workspace context: {ctx.FollowUpNames.Count} follow-up item{(ctx.FollowUpNames.Count == 1 ? "" : "s")}, " +
              $"{ctx.Warnings.Count} warning{(ctx.Warnings.Count == 1 ? "" : "s")}. See info for details."
            : "No stored workspace context yet. See info for details.";
        return ApplyRestoreContext(
            ResponseBuilder.Success(summary).WithAction("restored"),
            ctx);
    }

    /// <summary>
    /// Applies a restore context to a response so that guide and restore place the same
    /// payload in the same fields. The two tools previously differed here, which meant the
    /// skills list arrived under info: from one and content: from the other.
    /// </summary>
    private static McpResponse ApplyRestoreContext(McpResponse response, RestoreContext ctx) =>
        response
            .WithActionNeeded(ctx.Warnings.ToArray())
            .WithInfo([.. ctx.Info, .. ctx.ContentSections])
            .WithInstruction(ctx.Instruction)
            .WithFollowUp(ctx.FollowUpNames.ToArray());

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

        // Check for unresolved merge conflicts in .scrinia/.
        {
            try
            {
                string resumeScriniaDir = Path.Combine(store.WorkspaceRoot, ".scrinia");
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

        // Available skills (built-in + disk overrides).
        {
            try
            {
                string skillsDir = PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot);
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
        {
            try
            {
                string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
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
                    var (agentScope, _) = store.ResolveAddress(MemoryAddress.Parse("/agent/placeholder"));
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
                    var (patternsScope, _) = store.ResolveAddress(MemoryAddress.Parse("/patterns/placeholder"));
            var patternsEntries = store.LoadIndex(patternsScope);
            foreach (var entry in patternsEntries)
                followUpNames.Add($"/patterns/{entry.Name}");
        }
        catch { /* patterns scope not yet created — skip silently */ }

        // Checkpoint:latest — followUp if exists
        try
        {
            string checkpointArtifact = await store.ReadArtifactAsync(MemoryAddress.Parse("/checkpoint/latest"), cancellationToken);
            if (!string.IsNullOrEmpty(checkpointArtifact))
                followUpNames.Add("/checkpoint/latest");
        }
        catch (FileNotFoundException) { /* no checkpoint */ }

        // Today's session log — followUp if it exists
        try
        {
            string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            await store.ReadArtifactAsync(MemoryAddress.Parse($"/sessions/{today}"), cancellationToken);
            followUpNames.Add($"/sessions/{today}");
        }
        catch (FileNotFoundException) { /* no session log for today */ }

        // Recorded before the fallback below is appended, so it is an explicit signal for "did
        // any real section fire" rather than something a caller would have to infer by
        // string-matching the fallback text out of ContentSections.
        bool hasStoredContext = contentSections.Count > 0;
        if (!hasStoredContext)
            contentSections.Add("No persistent agent context found yet. Use remember to start storing notes, patterns, and skills.");

        if (followUpNames.Count > 0)
            instruction = "Call recall for each item in followUp to load full context.";

        return new RestoreContext(warnings, info, contentSections, followUpNames, instruction, hasStoredContext);
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
    string? Instruction,
    bool HasStoredContext);
