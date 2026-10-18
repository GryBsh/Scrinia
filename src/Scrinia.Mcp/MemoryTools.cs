using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Formats;
using Scrinia.Core.Encoding;

namespace Scrinia.Mcp;

[McpServerToolType]
public sealed partial class ScriniaMcpTools
{

    // The count-pattern regex that used to live here moved into MemoryDerivation with the D-05
    // rule it served: the auto-derived review condition is derivation, and derivation has one home.

    private static IMemoryStore CurrentStore => MemoryStoreContext.Resolve();

    /// <summary>
    /// Resolves inline NMP/2 artifacts without requiring a configured store.
    /// Returns null if the input requires store-based resolution (memory name or ephemeral address).
    /// </summary>
    private static Task<string?> TryResolveWithoutStore(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult<string?>(null);

        // Inline NMP/2 artifact
        if (Nmp2Strategy.Instance.RecognizesNmp2Prefix(input.TrimStart()))
            return Task.FromResult<string?>(input);

        return Task.FromResult<string?>(null);
    }

    internal static bool TryParseLogicalAddress(string? raw, out MemoryAddress address, out McpResponse error)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = ResponseBuilder.Error("Memory address must not be empty.", ErrorCodes.MalformedAddress,
                "Provide a logical address such as '/api/auth' or '~scratch'.");
            return false;
        }

        string input = raw.Trim();
        if (LooksLikeUri(input))
        {
            error = ResponseBuilder.Error("Only logical memory addresses are supported; URI inputs are not accepted.",
                ErrorCodes.UnsupportedInputForm,
                "Provide a logical address such as '/api/auth' or '~scratch'.");
            return false;
        }

        // Ephemeral alias seam: /temp/ resolved before store, store sees only ~subject.
        // Fence-aware extraction (``` fences) runs at the store boundary on ~subject form.
        // See TryParseLogicalAddress canonicalization.
        string canonical = MemoryNaming.CanonicalizeEphemeralAlias(input);
        if (!canonical.StartsWith('/') && !canonical.StartsWith('~'))
            canonical = "/" + canonical;

        if (MemoryAddress.TryParse(canonical, out address, out var addressError))
        {
            error = null!;
            return true;
        }

        error = ResponseBuilder.Error(addressError.Message, ErrorCodes.MalformedAddress,
            "Provide a logical address such as '/api/auth' or '~scratch'.");
        return false;
    }

    private static bool LooksLikeUri(string input) =>
        input.Contains("://", StringComparison.Ordinal) || input.StartsWith("//", StringComparison.Ordinal) ||
        input.Contains(':') || input.Contains('?') || input.Contains('#') || input.Contains('%');

    private static bool TryClassifyArtifactInput(string? input, out string? artifact, out McpResponse? error)
    {
        artifact = null;
        error = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string candidate = input.Trim();
        bool isNmp2 = Nmp2Strategy.Instance.RecognizesNmp2Prefix(candidate);
        bool isKnownOtherArtifact = candidate.StartsWith("TAMIS/2", StringComparison.Ordinal);
        bool isArtifactHeader = System.Text.RegularExpressions.Regex.IsMatch(
            candidate, "^[A-Z][A-Z0-9_-]*/[0-9]+(?:\\s|$)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        bool isPayload = candidate.Contains('\n') || candidate.Contains('\r') || candidate.StartsWith('{')
            || candidate.StartsWith("eyJ", StringComparison.Ordinal);

        if (!isNmp2 && !isKnownOtherArtifact && !isArtifactHeader && !isPayload)
            return false;

        if (isNmp2 && Nmp2Strategy.Instance.CanDecode(candidate))
        {
            artifact = candidate;
            return true;
        }

        error = ResponseBuilder.Error(
            "The supplied value is an unsupported or incomplete artifact.",
            ErrorCodes.UnsupportedInputForm,
            "Provide a logical memory address or a complete NMP/2 artifact.");
        return false;
    }

    private static string CanonicalAddress(MemoryAddress address) => address.ToString();

    [McpServerTool(Name = "guide"), Description(
        "Required reading — call at session start. Returns the agent guide AND auto-restores " +
        "workspace context (available skills, recent sessions, patterns) so you have everything " +
        "needed for the session in one tool call. If maintaining project instructions, record Scrinia usage " +
        "in a file such as AGENTS.md. That instruction file is distinct from the /agent/ persona namespace.")]
    public async Task<string> Guide(CancellationToken cancellationToken = default)
    {
        string guideContent = EmbeddedPrompts.LoadGuide()
            ?? throw new InvalidOperationException("Built-in guide not found in embedded resources");

        // guide() is exempt from the response-size cap (Task 10, T10-A) — the document is
        // embedded at build time, bounded by construction rather than by a caller's query, and
        // guide() is the session-start tool every agent calls first. Truncating it protects
        // nothing and silently damages the one document the agent most needs intact. The bound
        // this trades for is enforced at test time; see GuideSizeTests.

        // No store configured (e.g. running outside a workspace): just return the static guide.
        if (MemoryStoreContext.Current is null)
            return ResponseBuilder.SuccessUntruncated(guideContent).WithAction("guide").ToYaml();

        // Fold the workspace-restore context into the guide response so the agent gets warnings,
        // available skills, and followUp pointers without a separate restore call —
        // closes the most common utilization failure mode (read guide, forget to restore).
        try
        {
            var ctx = await BuildRestoreContextAsync(cancellationToken);
            return ApplyRestoreContext(
                ResponseBuilder.SuccessUntruncated(guideContent).WithAction("guide"),
                ctx).ToYaml();
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return (ResponseBuilder.SuccessUntruncated(guideContent).WithAction("guide") with
            {
                Status = "warning", ErrorCode = ErrorCodes.BundleRecoveryRequired,
                Error = ex.Message,
                Instruction = "Resolve the retained bundle recovery evidence before retrying persistent operations."
            }).ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
        catch (ScopeContainmentException ex)
        {
            return ResponseBuilder.Error(ex, "Use a canonical logical address and retry.").ToYaml();
        }
    }

    // ── Per-verb memory tools ────────────────────────────────────────────────
    //
    // Each tool below has its own JSONSchema — the validator sees exactly the
    // fields the operation accepts, so the agent's autonomous-mode "reach for
    // the more salient name" failure mode (e.g. passing `content` to an append
    // that needed a different shape) is now caught at schema-validation time,
    // not by the per-handler error path. Shared validation constants:

    private const int MaxContentBytesPerElement = 5 * 1024 * 1024; // 5 MB

    // ── Per-tool truncation advice (Task 10, T10-B) ─────────────────────────
    //
    // ResponseBuilder.DefaultTruncationAdvice ("narrow with search ... or recall a chunk") is
    // wrong for a tool that has no query to narrow and no chunk to recall. Rather than a switch
    // in the YAML renderer keyed on which tool is calling, each tool that can realistically
    // truncate (search, recall, list) sets its own advice once here and chains it with
    // .WithTruncationAdvice(...) at its own Success()/Warning() call sites — the renderer stays
    // one rule (McpResponseExtensions.ToYaml), the tool identity is the input.

    private const string RecallTruncationAdvice =
        "This memory's content exceeds the response limit. Retrieve it in pieces with " +
        "recall({ path, chunk: N }), starting at chunk 1. If chunk was already set, that chunk " +
        "itself is oversized — compact() the memory to shrink its chunks.";

    private const string SkillContentTruncationAdvice =
        "This skill file exceeds the response limit and skill content is not chunked. Shorten " +
        "the skill, or drop withBuiltin so only the override is returned.";

    private const string SearchTruncationAdvice =
        "Results were cut short. Narrow with a smaller limit, a pathPrefix, or an " +
        "attributeFilter, or recall a specific hit's path for its full content.";

    private const string ListTruncationAdvice =
        "The listing was cut short. Narrow with excludeTopics or a smaller scopes list, or use " +
        "mode: 'full' with offset/limit to page through it.";

    /// <summary>
    /// Canonicalizes an address arriving at the MCP facade. One helper rather than seven
    /// repetitions of the same call; the local CLI applies its own refusal policy.
    ///
    /// <para>The facade canonicalizes and proceeds. The CLI deliberately refuses ephemeral
    /// addresses because each command process exits immediately and cannot serve the memory
    /// back. Those are considered local-surface policies, not an inconsistency to unify.</para>
    ///
    /// <para>Scope is deliberately this class only. Other local call sites canonicalize at their
    /// own boundary; wrapping a one-line call used once would add indirection without
    /// consolidation. The facade and CLI policies are pinned behaviourally by the test suite.</para>
    /// </summary>
    [McpServerTool(Name = "remember"), Description(
        "Add chunks to a memory at a path, creating it if missing. Otherwise, append every supplied chunk and preserve existing content. " +
        "Set replace: true only for an intentional complete replacement. Store facts about people, their preferences, and past events under ordinary " +
        "paths such as /people/user/travel. Reserve /agent/ for the AI assistant's own persona and behavior, " +
        "not the human user's profile. Preserve explicitly stated details of the facts you store, including dates, durations, " +
        "quantities, and units. Keep qualifiers attached to their facts, preserving distinctions such as another event versus the same event, planned versus completed, and still true versus no longer true; do not infer missing values or event relationships. Separate existing facts from goals to change them; retain a short source quotation when a paraphrase could change the meaning. Content is an array — each element becomes one independently " +
        "retrievable chunk. attributes is a map of typed facets that link and filter memories " +
        "(e.g. person/org/topic/file/review); keywords are the distinctive terms and query phrasings " +
        "a searcher would use. A keywords entry shaped 'key:value' (e.g. 'person:maria') is routed " +
        "into attributes instead of kept as a keyword — see the keywords and content parameter " +
        "descriptions for the exact rule. Example: remember({ path: '/findings/SEC-001', " +
        "content: ['observation', 'recommendation'], attributes: { org: ['acme'], topic: ['auth'] }, " +
        "keywords: ['oauth flow', 'token refresh'] }).")]
    public async Task<string> MemoryRemember(
        [Description("Ordinary memory path (e.g. '/people/user/travel', '/preferences/food', '/findings/SEC-001'). " +
                     "/agent/ is reserved for the AI assistant's persona, not facts about the user.")] string? path,
        [Description("Content array — each element becomes one independently retrievable chunk. Everything " +
                     "below follows one rule: extraction reads all of the text except inside a properly " +
                     "closed fenced code block (```) — YAML frontmatter and inline `code` spans are read as " +
                     "ordinary text, not skipped. An opener ``` that never closes isn't a fence at all; the " +
                     "opener line and everything after it read as prose too. What extracts: a line-start " +
                     "'key: value' pair or a '**Key:** value' bold label mints an attribute under any key " +
                     "that starts with a letter (so 'C#:' and '2024:' don't route); an inline 'key:value' " +
                     "with no space (e.g. 'file:src/x.cs') additionally requires the key to be one of the " +
                     "eight recommended keys — person, place, org, thing, topic, file, ref, review — and is " +
                     "left as plain text for any other key; a bare file path ending in one of a fixed set of " +
                     "code/doc extensions (.cs, .md, .json, .yaml and similar — a name with no matching " +
                     "extension, like 'Dockerfile' or 'app.py', does not qualify) becomes a 'file' " +
                     "attribute, hashed as this memory's drift baseline; a '[[wiki-link]]' or '~ephemeral' " +
                     "reference becomes a 'ref' attribute; and a capitalized proper-noun phrase becomes a " +
                     "'mention' attribute on creation or explicit replacement. Appending preserves existing mentions. So a pasted build log or diff still contributes every path in it " +
                     "as a 'file' attribute unless you wrap it in a properly closed fenced code block — do " +
                     "that when you want sample content excluded from drift tracking, wiki-links, and " +
                      "mentions.")] string[]? content,
        [Description("Optional typed attributes — a map of key to values. Combines with what content and " +
                     "keywords derive: person/place/org/thing/topic/file/ref (and the auto-derived 'mention') " +
                     "union with the derived value; every other key — including review, anchor, and tag — is " +
                     "replaced by this value when both sides set it. Recommended keys: person, place, org, " +
                     "thing, topic — entities that link related memories, so a query naming one entity can " +
                     "surface a memory via a related entity even with no text overlap; use the same canonical " +
                     "name for an entity across memories, and the type lets a person and a place that share a " +
                     "name stay distinct. file — dependencies, hashed for drift detection. ref — a link to " +
                     "another memory, NOT hashed. review — an ISO 8601 date and/or a free-text condition. " +
                     "anchor — a retrieval-bridging phrase or question; write vocabulary you expect users to " +
                     "query with, not vocabulary already in the content — it's indexed for both attribute-" +
                     "score lookup and BM25 via term-frequency seeding. tag — free-form categorization. Any " +
                     "key is allowed. Example: { person: ['maria'], org: ['acme'], review: ['2026-08-01'] }.")]
        Dictionary<string, string[]>? attributes = null,
        [Description("Optional keywords — the distinctive terms AND the phrasings a searcher is likely to use. " +
                     "Merged with auto-extracted terms and seeded into search. A 'key:value' entry (any key, " +
                     "e.g. 'person:maria', 'sprint:24') is routed into attributes instead of kept as a keyword, " +
                     "provided the key starts with a letter (so 'C#:x' and '2024:x' don't qualify) and the " +
                     "value is 1-120 characters and doesn't start with '//' (a URL) — otherwise it stays a " +
                     "plain keyword. This is the preferred way to attach a person/place/org/thing/topic/file/" +
                     "ref/review facet (see attributes for what each means); any other key works too since " +
                     "every element here was typed on purpose. A plain keyword (no colon) stays a keyword.")] string[]? keywords = null,
        [Description("False (default) adds the supplied chunks while preserving existing content. Set true only to replace the entire memory with the supplied content.")] bool replace = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "remember requires 'path'.",
                ErrorCodes.InvalidParameter,
                "remember({ path: '/findings/example', content: [...] })").ToYaml();

        if (!TryParseLogicalAddress(path, out var parsedPath, out var addressError))
            return addressError.ToYaml();
        path = CanonicalAddress(parsedPath);

        if (content is null || content.Length == 0)
            return ResponseBuilder.Error(
                "remember requires 'content' (a non-empty string array).",
                ErrorCodes.InvalidParameter,
                "remember({ path: '...', content: ['chunk 1', 'chunk 2'] })").ToYaml();
        foreach (var element in content)
        {
            if (element is null)
                return ResponseBuilder.Error("content elements must not be null.", ErrorCodes.InvalidParameter,
                    "Supply a string for each content element.").ToYaml();
            if (System.Text.Encoding.UTF8.GetByteCount(element) > MaxContentBytesPerElement)
                return ResponseBuilder.Error(
                    $"content element exceeds {MaxContentBytesPerElement / (1024 * 1024)} MB limit.",
                    ErrorCodes.InvalidParameter,
                    "Split the content into multiple smaller elements in the content array.").ToYaml();
        }

        McpResponse? reservedResponse = await new ReservedPathDispatcher(this).DispatchAsync(
            replace ? ReservedPathOperation.Write : ReservedPathOperation.Append, parsedPath,
            new ReservedPathRequest(content, null, false, false, CreateIfMissing: !replace), cancellationToken);
        if (reservedResponse is not null)
        {
            return reservedResponse.ToYaml();
        }

        McpResponse response;
        try
        {
            response = replace
                ? await Remember(content, parsedPath, keywords: keywords, attributes: attributes,
                    actionLabel: "remembered", cancellationToken: cancellationToken)
                : (await AppendChunks(content, parsedPath, keywords: keywords, attributes: attributes,
                    cancellationToken: cancellationToken)).WithAction("remembered");
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
        catch (ScopeContainmentException ex)
        {
            return ResponseBuilder.Error(ex, "Use a canonical logical address and retry.").ToYaml();
        }
        catch (DurableWriteException ex)
        {
            return DurableWriteFailureResponse(ex).ToYaml();
        }
        catch (ScopeReadOnlyException ex)
        {
            return DurableWriteFailureResponse(ex).ToYaml();
        }
        catch (FileLockTimeoutException ex)
        {
            return DurableWriteFailureResponse(ex).ToYaml();
        }
        catch (WholeMemoryTransactionException ex)
        {
            var (scope, _) = CurrentStore.ResolveAddress(parsedPath);
            return WholeMemoryFailureResponse(ex, scope).ToYaml();
        }

        // Was InjectInfoHint doing string surgery on rendered YAML. The no-op-on-error rule is
        // preserved deliberately: errors carry recovery text on actionNeeded, not info.
        string? typoHint = ReservedPrefixGuard.HintFor(path);
        if (typoHint is not null && !response.IsError)
            response = response.WithInfo([.. response.Info ?? [], typoHint]);

        return response.ToYaml();
    }

    private static McpResponse DurableWriteFailureResponse(Exception exception)
    {
        McpResponse response = ResponseBuilder.Error(
            exception.Message,
            ErrorCodes.DurableWriteFailed,
            "Check permissions, free disk space if needed, and retry after upgrading scri when the existing sidecar is newer.");
        string? operationId = (exception as DurableWriteException)?.AppendOperationId ??
            (exception as ScopeReadOnlyException)?.AppendOperationId;
        return operationId is null ? response : response.WithOperationId(operationId);
    }

    private static string SafeRecoveryEvidencePath(string path) => Path.GetFileName(path);

    private static McpResponse WholeMemoryFailureResponse(
        WholeMemoryTransactionException exception, string scope)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string SafeEvidencePath() => SafeRecoveryEvidencePath(exception.Recovery?.EvidencePath ?? "evidence");

        if (exception.Code == WholeMemoryErrorCode.RecoveryRequired)
        {
            RecoveryInfo recovery = exception.Recovery
                ?? throw new InvalidOperationException("Whole-memory recovery response is missing structured evidence.");
            return ResponseBuilder.Error(
                    exception.Message,
                    ErrorCodes.RecoveryRequired,
                    recovery.Action,
                    "Resolve the retained whole-memory transaction evidence, then retry.")
                .WithOperationId(exception.OperationId)
                .WithRecovery(scope, SafeEvidencePath());
        }

        return ResponseBuilder.Error(
                exception.Message,
                ErrorCodes.DurableWriteFailed,
                "Check permissions, free disk space if needed, and retry.")
            .WithOperationId(exception.OperationId);
    }

    internal static McpResponse RecoveryBlockedResponse(
        RestoreRecoveryException exception, string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        RecoveryBlock? block = exception.DiscoveryBlock;
        if (block is not null)
        {
            string reason = FormatRecoveryReason(block.Reason);
            string evidencePath = block.Operation == RecoveryOperation.FileEntity
                ? Path.GetFileName(block.EvidencePath)
                : block.EvidencePath;
            string evidenceKind = block.Operation switch
            {
                RecoveryOperation.Restore => "restore journal",
                RecoveryOperation.Append => "append journal",
                RecoveryOperation.FileEntity => "file-entity recovery evidence",
                _ => "recovery evidence"
            };
            string detail = block.Reason == RecoveryBlockReason.UnsupportedVersion && block.UnsupportedVersion is { } version
                ? $"{evidenceKind} version {version} is newer than this build supports (1)."
                : $"{evidenceKind} is {reason}.";
            string retention = block.Operation == RecoveryOperation.Restore &&
                block.Reason == RecoveryBlockReason.UnsupportedVersion
                ? $"The journal was left unchanged at '{evidencePath}'. Upgrade scri, then retry the operation."
                : block.Operation == RecoveryOperation.Restore
                    ? "No files were changed and the journal was retained. Inspect or restore it with a compatible scri build, then retry."
                    : "No files were changed and the retained evidence must be resolved before retry.";
            string action = block.Operation == RecoveryOperation.Restore
                ? "Resolve the retained restore journal for the named topic scope, then retry."
                : "Resolve the retained recovery evidence for the named topic scope, then retry.";
            McpResponse typedResponse = ResponseBuilder.Error(
                    $"Recovery is blocked for topic scope '{block.Scope}': {detail} " +
                    $"The retained evidence is at '{evidencePath}'. {retention}",
                    ErrorCodes.RecoveryBlocked,
                    action)
                .WithRecovery(block.Scope, evidencePath)
                .WithReason(reason);
            string? safeOperationId = block.OperationId.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                ? operationId
                : block.OperationId;
            return safeOperationId is null ? typedResponse : typedResponse.WithOperationId(safeOperationId);
        }

        string fallbackReason = exception.Failure switch
        {
            RestoreRecoveryFailure.UnsupportedVersion => "unsupportedVersion",
            RestoreRecoveryFailure.MalformedOrIntegrityFailed => "malformed",
            RestoreRecoveryFailure.ReplayIoFailed => "replayIoFailed",
            _ => "unknown"
        };
        string error = exception.Failure switch
        {
            RestoreRecoveryFailure.UnsupportedVersion =>
                $"Recovery is blocked for topic scope '{exception.Scope}': restore journal version {FormatUnsupportedVersion(exception)} is newer than this build supports (1). The journal was left unchanged at '{exception.JournalPath}'. Upgrade scri, then retry the operation.",
            RestoreRecoveryFailure.MalformedOrIntegrityFailed =>
                $"Recovery is blocked for topic scope '{exception.Scope}': restore journal '{exception.JournalPath}' is malformed or failed integrity verification. No files were changed and the journal was retained. Inspect or restore the journal with a compatible scri build, then retry.",
            _ =>
                $"Recovery could not complete for topic scope '{exception.Scope}'; the journal remains at '{exception.JournalPath}' and access is blocked until retry succeeds. Check permissions and free disk space, then retry."
        };

        McpResponse response = ResponseBuilder.Error(
            error,
            ErrorCodes.RecoveryBlocked,
            "Resolve the retained restore journal for the named topic scope, then retry.");
        response = response
            .WithRecovery(exception.Scope, exception.JournalPath)
            .WithReason(fallbackReason);
        return operationId is null ? response : response.WithOperationId(operationId);
    }

    private static string FormatRecoveryReason(RecoveryBlockReason reason) => reason switch
    {
        RecoveryBlockReason.UnsupportedVersion => "unsupportedVersion",
        RecoveryBlockReason.ReplayIoFailed => "replayIoFailed",
        RecoveryBlockReason.WrongScope => "wrongScope",
        RecoveryBlockReason.Malformed => "malformed",
        RecoveryBlockReason.Unsafe => "unsafe",
        RecoveryBlockReason.Ambiguous => "ambiguous",
        RecoveryBlockReason.Inconsistent => "inconsistent",
        _ => "unknown"
    };

    private static McpResponse AppendRecoveryBlockedResponse(
        AppendStoreException exception, string scope)
    {
        AppendRecoveryInfo recovery = exception.Recovery
            ?? throw new InvalidOperationException("Append recovery response is missing structured evidence.");
        string journalPath = recovery.JournalPath;

        if (exception.DiscoveryBlock is { } block)
        {
            string reason = FormatRecoveryReason(block.Reason);
            return ResponseBuilder.Error(
                    $"Recovery is blocked for topic scope '{block.Scope}': append journal is {reason}. " +
                    $"The retained evidence is at '{journalPath}'. No files were changed.",
                    ErrorCodes.RecoveryBlocked,
                    "Resolve the retained append journal for the named topic scope, then retry.")
                .WithOperationId(block.OperationId.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    ? exception.OperationId
                    : block.OperationId)
                .WithRecovery(block.Scope, journalPath)
                .WithReason(reason);
        }

        return ResponseBuilder.Error(
                $"Recovery is blocked for topic scope '{scope}': append journal '{journalPath}' " +
                "failed integrity verification. The journal was retained and no further append " +
                "mutation was admitted.",
                ErrorCodes.RecoveryBlocked,
                "Resolve the retained append journal for the named topic scope, then retry.")
            .WithOperationId(exception.OperationId)
            .WithRecovery(scope, journalPath)
            .WithReason("replayIoFailed");
    }

    private static McpResponse AppendRecoveryBlockedResponse(
        string scope, string journalPath, string? operationId)
    {
        McpResponse response = ResponseBuilder.Error(
                $"Recovery is blocked for topic scope '{scope}': restore journal '{journalPath}' " +
                "is malformed or failed integrity verification. No files were changed and the " +
                "journal was retained.",
                ErrorCodes.RecoveryBlocked,
                "Resolve the retained restore journal for the named topic scope, then retry.")
            .WithRecovery(scope, journalPath);
        return operationId is null ? response : response.WithOperationId(operationId);
    }

    private static string FormatUnsupportedVersion(RestoreRecoveryException exception) =>
        exception.UnsupportedVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

    [McpServerTool(Name = "recall"), Description(
        "Read a memory by path. Returns the full content unless a chunk index is provided. " +
        "Example: recall({ path: '/findings/SEC-001' }), or recall({ path: '/skill/qa' }) to load a skill.")]
    public async Task<string> MemoryRecall(
        [Description("Memory path (e.g. '/findings/SEC-001', '/skill/qa', '/api/auth-flow'). '/skill/' alone lists available skills.")] string? path,
        [Description("Optional 1-based chunk index to retrieve a single chunk.")] int? chunk = null,
        [Description("Skills only: when path is '/skill/{name}', return both the built-in and the project override side-by-side for merging. Ignored for non-skill paths.")] bool withBuiltin = false,
        CancellationToken cancellationToken = default)
    {
        if (TryClassifyArtifactInput(path, out string? inlineArtifact, out var recallError))
        {
            return (await Recall(default, inlineArtifact!, chunk, actionLabel: "recalled", cancellationToken: cancellationToken)).ToYaml();
        }
        if (recallError is not null)
            return recallError.ToYaml();
        if (!TryParseLogicalAddress(path, out var parsedPath, out var addressError))
            return addressError.ToYaml();
        path = CanonicalAddress(parsedPath);

        McpResponse? reservedResponse = await new ReservedPathDispatcher(this).DispatchAsync(
            ReservedPathOperation.Read, parsedPath,
            new ReservedPathRequest(null, inlineArtifact, withBuiltin, false), cancellationToken);
        if (reservedResponse is not null)
            return reservedResponse.ToYaml();

        try
        {
            return (await Recall(parsedPath, chunk: chunk, actionLabel: "recalled", cancellationToken: cancellationToken)).ToYaml();
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
        catch (ScopeContainmentException ex)
        {
            return ResponseBuilder.Error(ex, "Use a canonical logical address and retry.").ToYaml();
        }
    }

    [McpServerTool(Name = "search"), Description(
        "Find memories by keyword or phrase. Returns ranked matches with a content preview and the " +
        "entry's attributes per hit so you can decide what to recall without a follow-up call. " +
        "Example: search({ query: 'oauth flow', pathPrefix: '/findings/', " +
        "attributeFilter: { topic: ['auth'] } }).")]
    public async Task<string> MemorySearch(
        [Description("Search query string.")] string query,
        [Description("Optional context terms appended to the query for scoring only — bridges vocabulary mismatches when you know synonyms or related concepts the user didn't type.")] string? context = null,
        [Description("Optional scope order, e.g. ['local', 'api', 'ephemeral']. An empty array behaves the same as omitting it.")] string[]? scopes = null,
        [Description("Maximum results to return (default 20). Rich-result format is verbose, so prefer 10 for broad exploratory queries.")] int limit = 20,
        [Description("Optional topic names to exclude from results. An empty array behaves the same as omitting it.")] string[]? excludeTopics = null,
        [Description("Optional path prefix to scope results, e.g. '/findings/' or '/skill/'. Matches the qualified memory name.")] string? pathPrefix = null,
        [Description("Optional attribute filter — a map of key to values. Returns only memories whose attributes " +
                     "contain EVERY listed key; for a key with values, the memory must carry at least one of them. " +
                     "Pass an empty array to require only the key's presence. Example: { topic: ['auth'], review: [] }.")]
        Dictionary<string, string[]>? attributeFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ResponseBuilder.Error(
                "search requires 'query'.",
                ErrorCodes.InvalidParameter,
                "search({ query: 'keywords or phrase' })").ToYaml();

        try
        {
            if (!string.IsNullOrWhiteSpace(pathPrefix) &&
                TryParseLogicalAddress(pathPrefix, out MemoryAddress reservedAddress, out _))
            {
                McpResponse? reservedResponse = await new ReservedPathDispatcher(this).DispatchAsync(
                    ReservedPathOperation.Search, reservedAddress,
                    new ReservedPathRequest([query], null, false, false), cancellationToken);
                if (reservedResponse is not null)
                    return reservedResponse.ToYaml();
            }
            return (await Search(query, scopes, limit, excludeTopics, context, pathPrefix, attributeFilter, cancellationToken)).ToYaml();
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
        catch (ScopeContainmentException ex)
        {
            return ResponseBuilder.Error(ex, "Use a canonical logical address and retry.").ToYaml();
        }
    }

    [McpServerTool(Name = "list"), Description(
        "List stored memories. mode 'summary' (default) shows scopes + top keywords and attributes; " +
        "'full' paginates the table; 'drift' surfaces memories whose file attributes have changed.")]
    public async Task<string> MemoryList(
        [Description("Optional scope order, e.g. ['local', 'api', 'ephemeral']. Topic names filter to local topics. An empty array behaves the same as omitting it.")] string[]? scopes = null,
        [Description("'summary' (default), 'full' for a paginated table, 'drift' for a code-reference drift check.")] string mode = "summary",
        [Description("Starting index for full mode (0-based).")] int offset = 0,
        [Description("Maximum entries to return in full mode (default 50).")] int limit = 50,
        [Description("Optional topic names to exclude (e.g. ['plan', 'task', 'project', 'learn'] to hide planning namespaces). An empty array behaves the same as omitting it.")] string[]? excludeTopics = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return (await List(scopes, mode ?? "summary", offset, limit, excludeTopics, cancellationToken)).ToYaml();
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
    }

    internal async Task<string> MemoryAppend(
        [Description("Memory path to append to.")] string? path,
        [Description("Text content for the new chunk. Every attribute route below follows one rule: none " +
                     "of them read inside a properly closed fenced code block (```), but all of them read " +
                     "YAML frontmatter and inline `code` spans as ordinary text. An opener ``` that never " +
                     "closes isn't a fence at all — the opener line and everything after it read as prose " +
                     "too. A line-start 'key: value' pair or a '**Key:** value' bold label mints an " +
                     "attribute under any key that starts with a letter (so 'C#:' and '2024:' don't " +
                     "route); an inline 'key:value' with no space (e.g. 'file:src/x.cs') additionally " +
                     "requires the key to be one of the eight recommended keys — person, place, org, " +
                     "thing, topic, file, ref, review — and is left as plain text for any other key; a " +
                     "bare file path ending in one of a fixed set of code/doc extensions (.cs, .md, .json, " +
                     ".yaml and similar — a name with no matching extension, like 'Dockerfile' or " +
                     "'app.py', does not qualify) becomes a 'file' attribute; and a '[[wiki-link]]' or " +
                     "'~ephemeral' reference becomes a 'ref' attribute. So a pasted build log still " +
                     "contributes every path in it unless you wrap it in a properly closed fence, and a " +
                     "path this memory has not seen before is hashed into its drift baseline. This runs " +
                     "on the appended chunk only and merges what it finds with what's already stored " +
                     "(multi-valued keys union, others replace), so an attribute a fenced block minted " +
                     "before this change persists on this memory until it's re-stored with remember. " +
                     "Unlike remember, this text is not scanned for proper-noun 'mention' spans — only " +
                     "whole-memory writes (remember, compact) do that; create-via-append is unaffected " +
                      "since it delegates to a whole-memory write internally.")] string? content,
        [Description("Optional attributes merged into the memory's existing attributes. person/place/org/" +
                     "thing/topic/file/ref/mention always union with what's already stored. tag and anchor " +
                     "also union here (append-only: a tag or anchor you pass adds to the existing set rather " +
                     "than replacing it). Every other key, including review, is replaced by this value when " +
                     "both sides set it. Same key vocabulary as remember: file is hashed for drift detection; " +
                     "ref links to another memory and is NOT hashed; anchor is a retrieval-bridging phrase or " +
                     "question — write vocabulary you expect users to query with, not vocabulary already in " +
                     "the content, since it's indexed for both attribute-score lookup and BM25 via term-" +
                     "frequency seeding; tag is free-form categorization.")]
        Dictionary<string, string[]>? attributes = null,
        [Description("Optional keywords merged with the memory's existing + auto-extracted terms (deduplicated). " +
                     "A 'key:value' entry (any key) routes into attributes instead of keywords, same guard as " +
                     "remember (key starts with a letter, value is 1-120 characters, not a '//' URL); a plain " +
                     "keyword (no colon) stays a keyword.")] string[]? keywords = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ResponseBuilder.Error(
                "append requires 'content'.",
                ErrorCodes.InvalidParameter,
                "append({ path: '...', content: 'new chunk text' })").ToYaml();
        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "append requires 'path'.",
                ErrorCodes.InvalidParameter,
                "append({ path: '/sessions/2026-05-11', content: '...' })").ToYaml();
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxContentBytesPerElement)
            return ResponseBuilder.Error(
                $"content exceeds {MaxContentBytesPerElement / (1024 * 1024)} MB limit.",
                ErrorCodes.InvalidParameter,
                "Split the append into multiple smaller append calls.").ToYaml();

        if (!TryParseLogicalAddress(path, out var parsedPath, out var addressError))
            return addressError.ToYaml();
        path = CanonicalAddress(parsedPath);
        McpResponse? reservedResponse = await new ReservedPathDispatcher(this).DispatchAsync(
            ReservedPathOperation.Append, parsedPath,
            new ReservedPathRequest([content], null, false, false), cancellationToken);
        if (reservedResponse is not null)
            return reservedResponse.ToYaml();
        McpResponse response;
        try
        {
            response = await Append(content, parsedPath, keywords: keywords, attributes: attributes, cancellationToken: cancellationToken);
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
        catch (ScopeContainmentException ex)
        {
            return ResponseBuilder.Error(ex, "Use a canonical logical address and retry.").ToYaml();
        }
        catch (AppendStoreException ex) when (
            ex.Code == AppendStoreErrorCode.RecoveryRequired && ex.Recovery is not null)
        {
            var (scope, _) = CurrentStore.ResolveAddress(parsedPath);
            return AppendRecoveryBlockedResponse(ex, scope).ToYaml();
        }

        // Same hint application as MemoryRemember: applied to the typed response before
        // serialisation, and still a no-op on error (errors use actionNeeded, not info).
        string? typoHint = ReservedPrefixGuard.HintFor(path);
        if (typoHint is not null && !response.IsError)
            response = response.WithInfo([.. response.Info ?? [], typoHint]);

        return response.ToYaml();
    }

    [McpServerTool(Name = "forget"), Description(
        "Delete a memory by path. A '/skill/{name}' path deletes the stored skill file; a built-in " +
        "skill is not a file here and cannot be deleted, so forget says so rather than reporting " +
        "NOT_FOUND. " +
        "Example: forget({ path: '/temp/scratch' }), or forget({ path: '/skill/qa' }) to drop a " +
        "stored skill.")]
    public async Task<string> MemoryForget(
        [Description("Memory path (e.g. '/findings/SEC-001', '/skill/qa', '~scratch').")] string? path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "forget requires 'path'.",
                ErrorCodes.InvalidParameter,
                "forget({ path: '...' })").ToYaml();

        if (!TryParseLogicalAddress(path, out var parsedPath, out var addressError))
            return addressError.ToYaml();
        path = CanonicalAddress(parsedPath);

        try
        {
            McpResponse? reservedResponse = await new ReservedPathDispatcher(this).DispatchAsync(
                ReservedPathOperation.Delete, parsedPath,
                new ReservedPathRequest(null, null, false, false), cancellationToken);
            if (reservedResponse is not null) return reservedResponse.ToYaml();
        }
        catch (FileLockTimeoutException ex)
        {
            return DurableWriteFailureResponse(ex).ToYaml();
        }
        try
        {
            return (await Forget(parsedPath, cancellationToken)).ToYaml();
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
        catch (ScopeContainmentException ex)
        {
            return ResponseBuilder.Error(ex, "Use a canonical logical address and retry.").ToYaml();
        }
    }

    [McpServerTool(Name = "compact"), Description(
        "Merge a multi-chunk memory. Archives the previous version first — except for an " +
        "ephemeral (~name) memory, which has no versions/ directory, so compacting one is " +
        "irreversible, or a persistent memory with no prior index entry, which has no previous " +
        "version to archive. A compact no-op is returned as a warning. The response says which happened. " +
        "Example: compact({ path: '/sessions/2026-05-11', keepRecent: 3 }).")]
    public async Task<string> MemoryCompact(
        [Description("Memory path to compact.")] string? path,
        [Description("Keep only the N most recent chunks. 0 (default) merges all into one.")] int keepRecent = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "compact requires 'path'.",
                ErrorCodes.InvalidParameter,
                "compact({ path: '...', keepRecent: 3 })").ToYaml();

        if (!TryParseLogicalAddress(path, out var parsedPath, out var addressError))
            return addressError.ToYaml();
        path = CanonicalAddress(parsedPath);

        McpResponse? reservedResponse = await new ReservedPathDispatcher(this).DispatchAsync(
            ReservedPathOperation.Compact, parsedPath,
            new ReservedPathRequest(null, null, false, false), cancellationToken);
        if (reservedResponse is not null)
            return reservedResponse.ToYaml();

        try
        {
            return (await Compact(parsedPath, keepRecent, cancellationToken)).ToYaml();
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
        catch (ScopeContainmentException ex)
        {
            return ResponseBuilder.Error(ex, "Use a canonical logical address and retry.").ToYaml();
        }
        catch (WholeMemoryTransactionException ex)
        {
            var (scope, _) = CurrentStore.ResolveAddress(parsedPath);
            return WholeMemoryFailureResponse(ex, scope).ToYaml();
        }
    }

    [McpServerTool(Name = "link"), Description(
        "Create a bidirectional link between two memories by adding a 'ref' attribute on each pointing " +
        "at the other — queryable later via search's attributeFilter: { ref: [...] }. Either side may " +
        "be an ephemeral (~name) memory. Both must already exist: if either is missing, neither is " +
        "written, so there is never a link pointing at nothing. " +
        "Example: link({ path: '~scratch', destination: '/patterns/auth' }).")]
    public async Task<string> MemoryLink(
        [Description("Source memory path.")] string? path,
        [Description("Target memory path.")] string? destination,
        [Description("Optional reason for the link, shown only in this call's response text — not persisted on either memory.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "link requires 'path'.",
                ErrorCodes.InvalidParameter,
                "link({ path: '...', destination: '...' })").ToYaml();
        if (string.IsNullOrWhiteSpace(destination))
            return ResponseBuilder.Error(
                "link requires 'destination'.",
                ErrorCodes.InvalidParameter,
                "link({ path: '...', destination: '...' })").ToYaml();

        if (!TryParseLogicalAddress(path, out var parsedPath, out var addressError))
            return addressError.ToYaml();
        if (!TryParseLogicalAddress(destination, out var parsedDestination, out var destinationError))
            return destinationError.ToYaml();
        path = CanonicalAddress(parsedPath);
        destination = CanonicalAddress(parsedDestination);
        try
        {
            return (await Link(parsedPath, parsedDestination, reason, cancellationToken)).ToYaml();
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
        catch (DurableWriteException ex)
        {
            return DurableWriteFailureResponse(ex).ToYaml();
        }
        catch (ScopeReadOnlyException ex)
        {
            return DurableWriteFailureResponse(ex).ToYaml();
        }
        catch (FileLockTimeoutException ex)
        {
            return DurableWriteFailureResponse(ex).ToYaml();
        }
        catch (ArgumentException ex)
        {
            return ResponseBuilder.Error(ex.Message, ErrorCodes.InvalidParameter).ToYaml();
        }
    }

    [McpServerTool(Name = "restore"), Description(
        "Resume workspace context — the AI assistant's persona, patterns, today's session log, available skills, " +
        "unresolved merge conflicts. Usually unnecessary because guide() folds this in at session start; " +
        "call this mid-session to refresh after a long gap.")]
    public async Task<string> MemoryRestore(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await Restore(cancellationToken)).ToYaml();
        }
        catch (Scrinia.Core.Bundles.BundleImportException ex)
        {
            return ResponseBuilder.Error(ex, "Resolve the retained bundle recovery evidence before retrying.").ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
    }

}
