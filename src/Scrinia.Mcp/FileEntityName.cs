namespace Scrinia.Mcp;

/// <summary>
/// Validation for user-supplied skill/agent name slugs that become on-disk filenames.
/// Rejects anything that could escape the target directory or is not a plain slug.
/// </summary>
internal static class FileEntityName
{
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
