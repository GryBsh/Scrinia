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
    // Ruling: !Path.IsPathRooted(name) is unreachable given the checks that precede it. On
    // Windows, "rooted" needs either a leading separator (already barred by the IndexOfAny
    // check above) or a drive-relative form like "C:foo" (barred by the char whitelist below,
    // which admits no ':'). On POSIX, "rooted" needs a leading '/' (also barred by IndexOfAny).
    // No string can be rooted while passing both sibling checks, so this clause never fires —
    // verified by running Path.IsPathRooted against representative inputs, not by inspection
    // alone. It is kept as defence-in-depth against a future reordering of these clauses:
    // anyone relaxing the slash check must NOT assume IsPathRooted still covers rooted paths,
    // because as written it never gets the chance to run first.
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
