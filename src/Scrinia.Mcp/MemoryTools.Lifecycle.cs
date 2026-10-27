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

        if (store is not IAtomicMemoryCompactor compactor)
            return ResponseBuilder.Error(
                "This memory store does not support atomic compaction.", ErrorCodes.NotSupported);

        CompactMemoryResult result;
        try
        {
            result = await compactor.CompactAsync(name, keepRecent, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error(
                $"Memory '{name}' not found.", ErrorCodes.NotFound,
                "list() to see available memories",
                "remember({ path: '...', description: 'Subjects and kinds of evidence stored here.', content: [...] }) to create it");
        }
        catch (InvalidMemoryWriteException exception)
        {
            return InvalidMemoryWriteResponse(exception);
        }
        catch (WholeMemoryTransactionException exception)
        {
            return WholeMemoryFailureResponse(exception, scope);
        }
        catch (FileLockTimeoutException exception)
        {
            return DurableWriteFailureResponse(exception);
        }

        int chunkCount = result.OriginalChunkCount;
        if (result.Entry is not { } stored)
        {
            string message = chunkCount <= 1
                ? "Already a single chunk, nothing to compact."
                : $"Nothing to compact — keepRecent ({keepRecent}) >= chunk count ({chunkCount}).";
            return ResponseBuilder.Warning(message).WithAction("compacted").WithInfo("mutation: none");
        }
        bool ephemeral = name.Kind == MemoryAddressKind.Ephemeral;
        int newChunkCount = stored.ChunkCount;
        bool hadIndexedEntry = result.Archived;
        WholeMemoryResult? transactionResult = result.Transaction;
        string[] committedContent = result.Content.ToArray();

        string qualifiedName = ephemeral ? $"~{stored.Name}" : store.FormatQualifiedName(scope, stored.Name);
        int dropped = chunkCount - newChunkCount;
        string archived = hadIndexedEntry ? " Original archived." : "";
        string summary = $"Compacted {qualifiedName}: {chunkCount} -> {newChunkCount} chunk{(newChunkCount == 1 ? "" : "s")} ({dropped} dropped).{archived}";

        if (transactionResult is not null)
        {
            // Route the promotion through the shared B0 result — the single FromWhole mapping reused
            // by every whole-memory facade. FromWhole is a behaviour-preserving pass of OperationId /
            // Warning / Recovery and SuccessWithWarning -> SuccessWithWarning, so the envelope below
            // renders byte-identical to the pre-B1 golden.
            B0Result b0 = B0ResultWrappers.FromWhole(transactionResult);
            if (b0.Outcome == B0Outcome.SuccessWithWarning)
            {
                var warningResponse = ResponseBuilder.SuccessWithWarning(summary)
                    .WithAction("compacted")
                    .WithOperationId(b0.OperationId)
                    .WithInfo(b0.Warning ?? "Whole-memory commit completed with a warning.");
                if (b0.Recovery is not null)
                    warningResponse = warningResponse.WithRecovery(
                        scope, SafeRecoveryEvidencePath(b0.Recovery.EvidencePath));
                if (!ephemeral)
                    warningResponse = warningResponse.WithFileChanges();
                await FireEventSinkAsync(sink => sink.OnStoredAsync(
                    stored.Address, committedContent, store, cancellationToken));
                return warningResponse;
            }
        }

        await FireEventSinkAsync(sink => sink.OnStoredAsync(
            stored.Address, committedContent, store, cancellationToken));
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
        // The whole workspace is inspected. Each section is best-effort so one unavailable or
        // unreadable context source does not fail guide(), the first tool an agent calls in a session.
        // The catches guard genuine I/O failures.
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
                using WorkspaceLease lease = EnterFileEntityAdmission(store, mutation: false, cancellationToken);
                string resumeScriniaDir = Path.Combine(store.WorkspaceRoot, ".scrinia");
                if (!lease.CapturedAbsentWorkspace && Directory.Exists(resumeScriniaDir))
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
                using WorkspaceLease lease = EnterFileEntityAdmission(store, mutation: false, cancellationToken);
                string skillsDir = PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot);
                var diskSkills = !lease.CapturedAbsentWorkspace && Directory.Exists(skillsDir)
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
                using (WorkspaceLease lease = EnterFileEntityAdmission(store, mutation: false, cancellationToken))
                {
                    var mdFiles = !lease.CapturedAbsentWorkspace && Directory.Exists(agentDir)
                        ? Directory.GetFiles(agentDir, "*.md") : [];
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
