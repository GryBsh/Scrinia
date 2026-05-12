using System.Globalization;
using System.Text;

namespace Scrinia.Mcp;

/// <summary>
/// Well-known machine-readable error codes emitted on <c>status: error</c> responses.
/// Models read these directively to choose recovery paths — keep the set small and stable.
/// </summary>
public static class ErrorCodes
{
    /// <summary>Missing required parameter, wrong shape, or value exceeds a size limit.</summary>
    public const string InvalidParameter = "INVALID_PARAMETER";

    /// <summary>Unknown or unsupported action name.</summary>
    public const string InvalidAction = "INVALID_ACTION";

    /// <summary>Path syntax invalid or outside the workspace sandbox.</summary>
    public const string InvalidPath = "INVALID_PATH";

    /// <summary>Memory, skill, conflict, or bundle file does not exist.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>Merge-conflict resolution failed (bad choice value, missing content, etc.).</summary>
    public const string Conflict = "CONFLICT";

    /// <summary>Fallback for unexpected internal failures and uncategorised errors.</summary>
    public const string Internal = "INTERNAL";
}

/// <summary>
/// Structured response returned by every MCP tool.
/// Serialised to YAML via <see cref="McpResponseExtensions.ToYaml"/>.
/// </summary>
public sealed record McpResponse
{
    public required string Status { get; init; }   // "success", "error", "warning"
    public string? Action { get; init; }           // "created", "completed", "listed", etc.
    public string? Path { get; init; }              // "/goal/G-60-abc", "/concern/SEC-054", etc.
    public string? Instruction { get; init; }      // what the agent must do next
    public string[]? ActionNeeded { get; init; }    // action-needed items
    public string[]? Info { get; init; }           // informational items
    public string[]? FollowUp { get; init; }      // suggested next tool calls
    public string? Content { get; init; }          // narrative text (multi-line)
    public string? Error { get; init; }            // error message (only when status=error)
    public string? ErrorCode { get; init; }        // machine-readable error code (see ErrorCodes)

    /// <summary>
    /// True when <see cref="Content"/> was truncated to fit the response cap. The YAML serialiser
    /// surfaces this as a followUp entry so the agent knows to fetch the next segment instead of
    /// proceeding on partial data.
    /// </summary>
    internal bool ContentTruncated { get; init; }
}

/// <summary>
/// Factory methods and fluent extensions for building <see cref="McpResponse"/> instances.
/// </summary>
public static class ResponseBuilder
{
    private const int MaxResponseChars = 8 * 1024;
    private const int YamlOverhead = 200;

    public static McpResponse Success(string? content = null)
    {
        string? truncated = Truncate(content, out bool wasTruncated);
        return new() { Status = "success", Content = truncated, ContentTruncated = wasTruncated };
    }

    /// <summary>
    /// Creates an uncategorised error. Prefer the overload that takes an
    /// <see cref="ErrorCodes"/> code so models can react programmatically.
    /// </summary>
    public static McpResponse Error(string error) =>
        new() { Status = "error", Error = error, ErrorCode = ErrorCodes.Internal };

    /// <summary>
    /// Creates an error with a machine-readable code and zero or more recovery
    /// hints that populate <see cref="McpResponse.ActionNeeded"/>.
    /// </summary>
    public static McpResponse Error(string error, string code, params string[] recovery) =>
        new()
        {
            Status = "error",
            Error = error,
            ErrorCode = code,
            ActionNeeded = recovery.Length > 0 ? recovery : null,
        };

    public static McpResponse Warning(string? content = null)
    {
        string? truncated = Truncate(content, out bool wasTruncated);
        return new() { Status = "warning", Content = truncated, ContentTruncated = wasTruncated };
    }

    // Fluent extensions --------------------------------------------------

    public static McpResponse WithInstruction(this McpResponse r, string? instruction) =>
        instruction is null ? r : r with { Instruction = instruction };

    public static McpResponse WithActionNeeded(this McpResponse r, params string[] warnings) =>
        warnings.Length == 0 ? r : r with { ActionNeeded = warnings };

    public static McpResponse WithInfo(this McpResponse r, params string[] info) =>
        info.Length == 0 ? r : r with { Info = info };

    public static McpResponse WithFollowUp(this McpResponse r, params string[] names) =>
        names.Length == 0 ? r : r with { FollowUp = names };

    public static McpResponse WithPath(this McpResponse r, string path) =>
        r with { Path = path };

    public static McpResponse WithAction(this McpResponse r, string action) =>
        r with { Action = action };

    private const string FileChangesNotice = "Files in .scrinia/ were updated — these are your changes.";

    /// <summary>Appends the standard file-changes notice to response content.</summary>
    public static McpResponse WithFileChanges(this McpResponse r)
    {
        if (r.Content is null)
            return r with { Content = FileChangesNotice };
        if (r.Content.Contains(FileChangesNotice, StringComparison.Ordinal))
            return r;
        return r with { Content = r.Content.TrimEnd() + "\n" + FileChangesNotice };
    }

    // Internals ----------------------------------------------------------

    private static string? Truncate(string? content, out bool truncated)
    {
        truncated = false;
        if (content is null) return null;
        int maxContent = MaxResponseChars - YamlOverhead;
        if (content.Length <= maxContent) return content;
        truncated = true;
        return content[..maxContent] + "\n... (truncated)";
    }
}

/// <summary>
/// Hand-built YAML serialiser for <see cref="McpResponse"/>.
/// No YamlDotNet dependency — AOT-safe, zero reflection.
/// </summary>
public static class McpResponseExtensions
{
    public static string ToYaml(this McpResponse r)
    {
        // YAML output uses InvariantCulture explicitly — values being interpolated are
        // identifiers, not numbers/dates, but the analyzer can't prove that and we want
        // the build to stay green under any locale.
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(ci, $"status: {r.Status}");

        if (r.Action is not null)
            sb.AppendLine(ci, $"action: {r.Action}");
        if (r.Path is not null)
            sb.AppendLine(ci, $"path: {YamlEscape(r.Path)}");
        if (r.Error is not null)
            sb.AppendLine(ci, $"error: {YamlEscape(r.Error)}");
        if (r.ErrorCode is not null)
            sb.AppendLine(ci, $"errorCode: {r.ErrorCode}");
        if (r.Instruction is not null)
            sb.AppendLine(ci, $"instruction: {YamlEscape(r.Instruction)}");

        if (r.ActionNeeded is { Length: > 0 })
        {
            sb.AppendLine("actionNeeded:");
            foreach (var w in r.ActionNeeded)
                sb.AppendLine(ci, $"  - {YamlEscape(w)}");
        }

        if (r.Info is { Length: > 0 })
        {
            sb.AppendLine("info:");
            foreach (var i in r.Info)
                sb.AppendLine(ci, $"  - {YamlEscape(i)}");
        }

        if (r.FollowUp is { Length: > 0 } || r.ContentTruncated)
        {
            sb.AppendLine("followUp:");
            if (r.FollowUp is { Length: > 0 })
                foreach (var name in r.FollowUp)
                    sb.AppendLine(ci, $"  - {YamlEscape(name)}");
            if (r.ContentTruncated)
                sb.AppendLine("  - " + YamlEscape(
                    "Content truncated. Call memory('recall', { path, chunk: N+1 }) for the next chunk, " +
                    "or memory('search') to scope down to the segment you need."));
        }

        if (r.Content is not null)
        {
            sb.AppendLine("content: |");
            foreach (var line in r.Content.Split('\n'))
                sb.AppendLine(ci, $"  {line}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Inject an additional info-bullet into an already-serialised success/warning YAML
    /// response. Used by the <c>memory()</c> dispatcher to surface advisory hints
    /// (e.g. reserved-prefix typo suggestions) without re-running the handler.
    /// No-op on error responses — error has its own actionNeeded channel.
    /// </summary>
    public static string InjectInfoHint(string yaml, string hint)
    {
        if (string.IsNullOrEmpty(hint)) return yaml;
        if (yaml.Contains("status: error", StringComparison.Ordinal)) return yaml;

        string escaped = YamlEscape(hint);
        string newBullet = $"  - {escaped}";

        // Extend an existing info: section if present.
        int infoIdx = yaml.IndexOf("\ninfo:\n", StringComparison.Ordinal);
        if (infoIdx == 0 || (infoIdx < 0 && yaml.StartsWith("info:\n", StringComparison.Ordinal)))
            infoIdx = 0;
        if (infoIdx >= 0 || yaml.StartsWith("info:\n", StringComparison.Ordinal))
        {
            int headerStart = yaml.StartsWith("info:\n", StringComparison.Ordinal) ? 0 : infoIdx + 1;
            int afterHeader = yaml.IndexOf('\n', headerStart) + 1;
            return yaml[..afterHeader] + newBullet + "\n" + yaml[afterHeader..];
        }

        // Otherwise create a new info section ahead of followUp/content (whichever comes first).
        int anchor = -1;
        foreach (var token in new[] { "\nfollowUp:\n", "\ncontent:" })
        {
            int idx = yaml.IndexOf(token, StringComparison.Ordinal);
            if (idx >= 0 && (anchor < 0 || idx < anchor)) anchor = idx;
        }
        string section = $"info:\n{newBullet}\n";
        if (anchor >= 0)
            return yaml[..(anchor + 1)] + section + yaml[(anchor + 1)..];

        return yaml.TrimEnd('\n', '\r') + "\n" + section.TrimEnd('\n');
    }

    private static string YamlEscape(string value)
    {
        if (value.Contains(':') || value.Contains('#') || value.Contains('"') ||
            value.Contains('\'') || value.Contains('{') || value.Contains('}') ||
            value.Contains('[') || value.Contains(']') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")}\"";
        }

        return value;
    }
}
