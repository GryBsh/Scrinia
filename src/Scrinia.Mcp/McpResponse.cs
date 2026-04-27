using System.Text;

namespace Scrinia.Mcp;

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
}

/// <summary>
/// Factory methods and fluent extensions for building <see cref="McpResponse"/> instances.
/// </summary>
public static class ResponseBuilder
{
    private const int MaxResponseChars = 8 * 1024;
    private const int YamlOverhead = 200;

    public static McpResponse Success(string? content = null) =>
        new() { Status = "success", Content = Truncate(content) };

    public static McpResponse Error(string error) =>
        new() { Status = "error", Error = error };

    public static McpResponse Warning(string? content = null) =>
        new() { Status = "warning", Content = Truncate(content) };

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

    private static string? Truncate(string? content)
    {
        if (content is null) return null;
        int maxContent = MaxResponseChars - YamlOverhead;
        return content.Length > maxContent
            ? content[..maxContent] + "\n... (truncated)"
            : content;
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
        var sb = new StringBuilder();
        sb.AppendLine($"status: {r.Status}");

        if (r.Action is not null)
            sb.AppendLine($"action: {r.Action}");
        if (r.Path is not null)
            sb.AppendLine($"path: {YamlEscape(r.Path)}");
        if (r.Error is not null)
            sb.AppendLine($"error: {YamlEscape(r.Error)}");
        if (r.Instruction is not null)
            sb.AppendLine($"instruction: {YamlEscape(r.Instruction)}");

        if (r.ActionNeeded is { Length: > 0 })
        {
            sb.AppendLine("actionNeeded:");
            foreach (var w in r.ActionNeeded)
                sb.AppendLine($"  - {YamlEscape(w)}");
        }

        if (r.Info is { Length: > 0 })
        {
            sb.AppendLine("info:");
            foreach (var i in r.Info)
                sb.AppendLine($"  - {YamlEscape(i)}");
        }

        if (r.FollowUp is { Length: > 0 })
        {
            sb.AppendLine("followUp:");
            foreach (var name in r.FollowUp)
                sb.AppendLine($"  - {YamlEscape(name)}");
        }

        if (r.Content is not null)
        {
            sb.AppendLine("content: |");
            foreach (var line in r.Content.Split('\n'))
                sb.AppendLine($"  {line}");
        }

        return sb.ToString().TrimEnd();
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
