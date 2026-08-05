namespace Scrinia.Core;

/// <summary>
/// Validation for user-supplied skill/agent name slugs that become on-disk filenames.
/// Rejects anything that could escape the target directory or is not a plain slug.
/// <para>Lives in Core (not Mcp) because <see cref="PathRouter.TryToFileEntityPath"/> is the
/// single place that resolves a file-entity name to a path, and it enforces this check so no
/// caller can forget it.</para>
/// </summary>
public static class FileEntityName
{
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
