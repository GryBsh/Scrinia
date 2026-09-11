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
        if (input.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
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
        bool isNmp2 = candidate.StartsWith("NMP/2", StringComparison.Ordinal);
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
        "needed for the session in one tool call. Then commit content to your project's agent file.")]
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
        var ctx = await BuildRestoreContextAsync(cancellationToken);
        return ApplyRestoreContext(
            ResponseBuilder.SuccessUntruncated(guideContent).WithAction("guide"),
            ctx).ToYaml();
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
        "Persist a memory at a path. content is an array — each element becomes one independently " +
        "retrievable chunk. attributes is a map of typed facets that link and filter memories " +
        "(e.g. person/org/topic/file/review); keywords are the distinctive terms and query phrasings " +
        "a searcher would use. A keywords entry shaped 'key:value' (e.g. 'person:maria') is routed " +
        "into attributes instead of kept as a keyword — see the keywords and content parameter " +
        "descriptions for the exact rule. Example: remember({ path: '/findings/SEC-001', " +
        "content: ['observation', 'recommendation'], attributes: { org: ['acme'], topic: ['auth'] }, " +
        "keywords: ['oauth flow', 'token refresh'] }).")]
    public async Task<string> MemoryRemember(
        [Description("Memory path (e.g. '/findings/SEC-001', '/patterns/retry', '/api/auth-flow', '/temp/scratch').")] string? path,
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
                     "'mention' attribute. So a pasted build log or diff still contributes every path in it " +
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

        // Skill-path routing: '/skill/{name}' → SkillCreate.
        var skillRoute = TryRouteSkillRemember(path, content, attributes, keywords, cancellationToken);
        if (skillRoute is not null)
        {
            try
            {
                return (await skillRoute).ToYaml();
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
        }

        if (content is null || content.Length == 0)
            return ResponseBuilder.Error(
                "remember requires 'content' (a non-empty string array).",
                ErrorCodes.InvalidParameter,
                "remember({ path: '...', content: ['chunk 1', 'chunk 2'] })").ToYaml();
        foreach (var element in content)
        {
            if (element != null && System.Text.Encoding.UTF8.GetByteCount(element) > MaxContentBytesPerElement)
                return ResponseBuilder.Error(
                    $"content element exceeds {MaxContentBytesPerElement / (1024 * 1024)} MB limit.",
                    ErrorCodes.InvalidParameter,
                    "Split the content into multiple smaller elements in the content array.").ToYaml();
        }

        McpResponse response;
        try
        {
            response = await Remember(content, parsedPath,
                keywords: keywords, attributes: attributes,
                actionLabel: "remembered", cancellationToken: cancellationToken);
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

    private static McpResponse WholeMemoryFailureResponse(
        WholeMemoryTransactionException exception, string scope)
    {
        if (exception.Code == WholeMemoryErrorCode.RecoveryRequired)
        {
            RecoveryInfo recovery = exception.Recovery
                ?? throw new InvalidOperationException("Whole-memory recovery response is missing structured evidence.");
            return ResponseBuilder.Error(
                    exception.Message,
                    ErrorCodes.RecoveryRequired,
                    "Resolve the retained whole-memory transaction evidence, then retry.")
                .WithOperationId(exception.OperationId)
                .WithRecovery(scope, recovery.EvidencePath);
        }

        return ResponseBuilder.Error(
                exception.Message,
                ErrorCodes.DurableWriteFailed,
                "Check permissions, free disk space if needed, and retry.")
            .WithOperationId(exception.OperationId);
    }

    private static McpResponse RecoveryBlockedResponse(
        RestoreRecoveryException exception, string? operationId = null)
    {
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
        response = response.WithRecovery(exception.Scope, exception.JournalPath);
        return operationId is null ? response : response.WithOperationId(operationId);
    }

    private static McpResponse AppendRecoveryBlockedResponse(
        AppendStoreException exception, string scope)
    {
        AppendRecoveryInfo recovery = exception.Recovery
            ?? throw new InvalidOperationException("Append recovery response is missing structured evidence.");
        string journalPath = recovery.JournalPath;

        return ResponseBuilder.Error(
                $"Recovery is blocked for topic scope '{scope}': append journal '{journalPath}' " +
                "failed integrity verification. The journal was retained and no further append " +
                "mutation was admitted.",
                ErrorCodes.RecoveryBlocked,
                "Resolve the retained append journal for the named topic scope, then retry.")
            .WithOperationId(exception.OperationId)
            .WithRecovery(scope, journalPath);
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

        // Skill-path routing: '/skill/...' → SkillLoad. Handles both '/skill/{name}' (load)
        // and '/skill' or '/skill/' (list view).
        var skillRoute = TryRouteSkillRecall(path, withBuiltin, cancellationToken);
        if (skillRoute is not null)
        {
            try
            {
                return (await skillRoute).ToYaml();
            }
            catch (FileLockTimeoutException ex)
            {
                return DurableWriteFailureResponse(ex).ToYaml();
            }
        }

        try
        {
            return (await Recall(parsedPath, chunk: chunk, actionLabel: "recalled", cancellationToken: cancellationToken)).ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
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
            return (await Search(query, scopes, limit, excludeTopics, context, pathPrefix, attributeFilter, cancellationToken)).ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
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
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
    }

    [McpServerTool(Name = "append"), Description(
        "Append a new chunk to an existing memory (or create it if missing). Preserves the memory's " +
        "existing retrieval metadata and merges in any new attributes (multi-valued keys union) and " +
        "keywords (deduplicated) — same 'key:value' routing as remember: a keywords entry shaped " +
        "'key:value' becomes an attribute instead of a keyword, and the eight recommended keys route " +
        "the same way from inline content prose. Example: append({ path: '/sessions/2026-05-11', " +
        "content: 'session note', attributes: { topic: ['standup'] } }).")]
    public async Task<string> MemoryAppend(
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
        McpResponse response;
        try
        {
            response = await Append(content, parsedPath, keywords: keywords, attributes: attributes, cancellationToken: cancellationToken);
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
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

        // Skill-path routing: '/skill/{name}' → SkillDelete. Placed first, like recall and
        // remember, so the three tools agree about who owns the namespace.
        try
        {
            var skillRoute = TryRouteSkillForget(path, cancellationToken);
            if (skillRoute is not null) return (await skillRoute).ToYaml();
        }
        catch (FileLockTimeoutException ex)
        {
            return DurableWriteFailureResponse(ex).ToYaml();
        }
        try
        {
            return (await Forget(parsedPath, cancellationToken)).ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
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
        try
        {
            return (await Compact(parsedPath, keepRecent, cancellationToken)).ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
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
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
    }

    [McpServerTool(Name = "restore"), Description(
        "Resume workspace context — agent profile, patterns, today's session log, available skills, " +
        "unresolved merge conflicts. Usually unnecessary because guide() folds this in at session start; " +
        "call this mid-session to refresh after a long gap.")]
    public async Task<string> MemoryRestore(CancellationToken cancellationToken = default)
    {
        return (await Restore(cancellationToken)).ToYaml();
    }

    // ── Skill-path routing ────────────────────────────────────────────────────
    //
    // Skills are conceptually memory at a reserved path — we route /skill/{name}
    // requests to SkillLoad / SkillCreate / SkillDelete inside recall, remember and
    // forget rather than exposing a separate skill_* tool surface. The routing key is
    // `path`, so a dedicated tool wouldn't reduce ambiguity — it would just
    // create a second surface the agent has to choose between.

    /// <summary>
    /// Routes a recall call to <see cref="SkillLoad"/> when the path is under
    /// /skill/. Handles both '/skill/{name}' (load one) and '/skill' or '/skill/'
    /// (list available). Returns null when the call should fall through to normal recall.
    /// </summary>
    private static Task<McpResponse>? TryRouteSkillRecall(string? path, bool withBuiltin, CancellationToken ct)
    {
        if (!TryParseSkillPath(path, out string? skillName))
            return null;
        return SkillLoad(skillName, withBuiltin, ct);
    }

    /// <summary>
    /// Routes a remember call to <see cref="SkillCreate"/> when the path is under
    /// /skill/. Returns null when the call should fall through to normal remember.
    /// <para>Takes <paramref name="attributes"/> and <paramref name="keywords"/> only so it can
    /// report that it did not use them. A skill is markdown instructions on disk, not an indexed
    /// memory, so there is nowhere for facets to live — but remember's schema spends two kilobytes
    /// telling the caller what they do, and dropping them under a plain success reads as if they
    /// were stored.</para>
    /// </summary>
    private static Task<McpResponse>? TryRouteSkillRemember(
        string? path,
        string[]? content,
        Dictionary<string, string[]>? attributes,
        string[]? keywords,
        CancellationToken ct)
    {
        if (!TryParseSkillPath(path, out string? skillName))
            return null;

        if (content is null || content.Length == 0)
            return Task.FromResult(ResponseBuilder.Error(
                "remember on a /skill/... path requires 'content' with at least one element (the skill instructions).",
                ErrorCodes.InvalidParameter,
                "remember({ path: '/skill/my-helper', content: ['instructions here'] })"));

        if (string.IsNullOrWhiteSpace(skillName))
            return Task.FromResult(ResponseBuilder.Error(
                "remember on a /skill/... path requires a skill name in the path.",
                ErrorCodes.InvalidParameter,
                "remember({ path: '/skill/your-name-here', content: [...] })"));

        var create = SkillCreate(
            name: skillName,
            scaffold: "custom",
            instructions: string.Join("\n\n", content),
            tools: null,
            ct);

        // Conditional on purpose. A notice attached to every skill write is noise the caller
        // learns to skip, which costs more than saying nothing.
        var unused = new List<string>();
        if (attributes is { Count: > 0 }) unused.Add("attributes");
        if (keywords is { Length: > 0 }) unused.Add("keywords");
        return unused.Count == 0 ? create : ReportUnusedSkillArgs(create, unused);
    }

    /// <summary>
    /// Appends a note naming the remember arguments that a /skill/ write had no place to put.
    /// No-op on an error response — an error already carries its own recovery text.
    /// </summary>
    private static async Task<McpResponse> ReportUnusedSkillArgs(
        Task<McpResponse> create, List<string> unused)
    {
        McpResponse response = await create;
        if (response.IsError) return response;

        string names = string.Join(" and ", unused);
        bool one = unused.Count == 1;
        string verb = one ? "does" : "do";
        return response.WithInfo([
            .. response.Info ?? [],
            $"{names} {verb} not apply to a skill and {(one ? "was" : "were")} not "
            + "stored. A skill is markdown instructions on disk, not an indexed memory, so it has "
            + "nowhere to hold facets. Put anything that must be searchable in the skill text, or "
            + "store it as a memory at a path outside /skill/.",
        ]);
    }

    /// <summary>
    /// Routes a forget call to <see cref="SkillDelete"/> when the path is under
    /// /skill/. Returns null when the call should fall through to normal forget.
    /// </summary>
    private static Task<McpResponse>? TryRouteSkillForget(string? path, CancellationToken ct)
    {
        if (!TryParseSkillPath(path, out string? skillName))
            return null;

        if (string.IsNullOrWhiteSpace(skillName))
            return Task.FromResult(ResponseBuilder.Error(
                "forget on a /skill/... path requires a skill name in the path.",
                ErrorCodes.InvalidParameter,
                "forget({ path: '/skill/the-one-to-delete' })",
                "recall({ path: '/skill/' }) to list available skills"));

        return SkillDelete(skillName, ct);
    }

    /// <summary>
    /// True when <paramref name="path"/> is a /skill/... path (case-insensitive).
    /// Sets <paramref name="skillName"/> to the parsed name, or null when the path
    /// is just '/skill' or '/skill/' (the list view).
    /// </summary>
    private static bool TryParseSkillPath(string? path, out string? skillName)
    {
        skillName = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (!path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("/skill", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Length > "/skill/".Length)
        {
            skillName = path["/skill/".Length..].Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(skillName))
                skillName = null;
        }
        return true;
    }
}
