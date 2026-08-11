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
    // Ruling: !Path.IsPathRooted(name) is redundant, not unreachable, given the checks around
    // it. It DOES run and it DOES reject inputs: "C:foo" passes the IndexOfAny and ".." checks
    // above (no slash, no "..") and reaches this clause, which rejects it because
    // Path.IsPathRooted("C:foo") is true on Windows (drive-relative rooted form). The clause is
    // redundant only in the sense that the char whitelist below would also have rejected
    // "C:foo" (it admits no ':') had this clause not fired first — verified by running
    // Path.IsPathRooted against representative inputs, not by inspection alone. It is kept as
    // defence-in-depth: if the slash check above is ever relaxed, this is precisely the clause
    // that starts catching rooted forms like "/foo" or "C:\foo" that the slash check no longer
    // does.
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
