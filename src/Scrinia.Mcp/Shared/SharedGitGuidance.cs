namespace Scrinia.Mcp.SharedContracts;

/// <summary>
/// Renders the print-only, operation-based Git guidance for a successful shared mutation (§7.2). The
/// guidance is exactly one staging command carrying every manifest path as an individual shell-quoted
/// argument, followed by the fixed commit and push. Scrinia never executes these commands, never
/// substitutes a directory pathspec for the store, and never claims the commands ran or that the
/// repository is tracked, committed, or synchronized.
/// </summary>
public static class SharedGitGuidance
{
    private const string CommitMessage = "docs(memory): update shared Scrinia memory";

    /// <summary>
    /// Returns the three-line guidance for a nonempty manifest, or <c>null</c> when the manifest is
    /// empty so a successful no-op emits no staging command (§7.2).
    /// </summary>
    /// <param name="registeredRoot">The repository root the manifest paths are relative to.</param>
    /// <param name="manifest">The canonical, repository-root-relative authoritative files (§7.2).</param>
    public static string? Render(string registeredRoot, IReadOnlyList<string> manifest)
    {
        if (manifest.Count == 0)
            return null;

        string root = QuoteRoot(registeredRoot);
        string paths = string.Join(" ", manifest.Select(QuoteManifestPath));
        string commandRoot = $"git -C {root}";

        return string.Join('\n',
            $"{commandRoot} add -A -- {paths}",
            $"{commandRoot} commit -m \"{CommitMessage}\"",
            $"{commandRoot} push");
    }

    /// <summary>
    /// POSIX single-quotes each manifest path and represents an embedded single quote as <c>'"'"'</c>
    /// so the copied argument re-parses to exactly the authoritative file and never interpolates.
    /// </summary>
    private static string QuoteManifestPath(string path) => "'" + path.Replace("'", "'\"'\"'") + "'";

    /// <summary>
    /// Double-quotes the registered root so a copied <c>git -C</c> argument survives spaces,
    /// backslashes, quotes, and shell expansion characters, matching the §7.2 staging command shape.
    /// </summary>
    private static string QuoteRoot(string root)
    {
        var quoted = new System.Text.StringBuilder(root.Length + 2);
        quoted.Append('"');
        foreach (char character in root)
        {
            if (character is '\\' or '"' or '$' or '`')
                quoted.Append('\\');

            quoted.Append(character);
        }

        return quoted.Append('"').ToString();
    }
}
