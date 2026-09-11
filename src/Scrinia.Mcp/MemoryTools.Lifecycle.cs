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
using Scrinia.Core.Models;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    /// <summary>Bundle operations — export and import memory topics.
    /// No longer exposed as an MCP tool; available via CLI (scri export, scri import).</summary>
    /// <remarks>
    /// Internal — not an MCP tool. Previously public for external embedders, but
    /// <c>WithTools&lt;T&gt;</c> registers all public instance methods as tools, so a public
    /// non-tool helper leaks the MCP surface. Keeps returning rendered YAML while the
    /// internal handlers return <see cref="McpResponse"/>; internal callers use the
    /// handlers directly.
    /// </remarks>
    internal async Task<string> Bundle(
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
            return ResponseBuilder.Warning("Already a single chunk, nothing to compact.")
                .WithAction("compacted")
                .WithInfo("mutation: none");

        if (keepRecent > 0 && keepRecent >= chunkCount)
            return ResponseBuilder.Warning($"Nothing to compact — keepRecent ({keepRecent}) >= chunk count ({chunkCount}).")
                .WithAction("compacted")
                .WithInfo("mutation: none");

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

        ArtifactEntry stored;
        WholeMemoryResult? transactionResult = null;
        try
        {
            if (store is FileMemoryStore fileStore)
            {
                (stored, transactionResult) = await fileStore
                    .StoreWithTransactionResultAsync(write, cancellationToken);
            }
            else
            {
                stored = await store.StoreAsync(write, cancellationToken);
            }
        }
        catch (WholeMemoryTransactionException exception)
        {
            return WholeMemoryFailureResponse(exception, scope);
        }

        string qualifiedName = ephemeral ? $"~{stored.Name}" : store.FormatQualifiedName(scope, stored.Name);
        int dropped = chunkCount - newChunkCount;
        string archived = hadIndexedEntry ? " Original archived." : "";
        string summary = $"Compacted {qualifiedName}: {chunkCount} -> {newChunkCount} chunk{(newChunkCount == 1 ? "" : "s")} ({dropped} dropped).{archived}";

        if (transactionResult?.Outcome == WholeMemoryOutcome.SuccessWithWarning)
        {
            var warningResponse = ResponseBuilder.SuccessWithWarning(summary)
                .WithAction("compacted")
                .WithOperationId(transactionResult.OperationId)
                .WithInfo(transactionResult.Warning ?? "Whole-memory commit completed with a warning.");
            if (transactionResult.Recovery is not null)
                warningResponse = warningResponse.WithRecovery(scope, transactionResult.Recovery.EvidencePath);
            if (!ephemeral)
                warningResponse = warningResponse.WithFileChanges();
            await FireEventSinkAsync(sink => sink.OnStoredAsync(
                stored.Address, write.RawContent, store, cancellationToken));
            return warningResponse;
        }

        await FireEventSinkAsync(sink => sink.OnStoredAsync(
            stored.Address, write.RawContent, store, cancellationToken));
        // "Original archived" only when there was an indexed entry to archive: StoreAsync archives
        // the previous version when the memory is already in the index, and an artifact with no
        // index entry is a first write as far as it is concerned. Claiming an archive that did not
        // happen is the kind of report a user acts on before deleting something. An ephemeral never
        // archives — it has no versions/ directory — so hadIndexedEntry is unconditionally false
        // for that branch above.
        var response = ResponseBuilder.Success(
            summary);
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
                        warnings.Add(".scrinia/ has unresolved merge conflicts. Resolve them before continuing.");
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
