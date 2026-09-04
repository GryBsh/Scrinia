namespace Scrinia.Core.Configuration;

public enum ManagedIgnoreValidation
{
    Valid,
    Missing,
    Drift,
}

/// <summary>Builds and validates Scrinia's owned, line-exact ignore regions.</summary>
public static class ManagedIgnoreBlock
{
    public const string Local = "# Scrinia tracking policy: local\n*\n";
    public static string ProjectGit => BuildProjectGitBlock();

    private const string OpeningMarker = "# >>> Scrinia managed: project-git derived files >>>";
    private const string ClosingMarker = "# <<< Scrinia managed: project-git derived files <<<";

    private static string BuildProjectGitBlock() =>
        OpeningMarker + "\n" + string.Join("\n", SharedClassifier.Rules.Select(rule => rule.IgnorePattern)) + "\n" + ClosingMarker + "\n";

    public static string For(TrackingPolicy policy) => policy switch
    {
        TrackingPolicy.Local => Local,
        TrackingPolicy.ProjectGit => ProjectGit,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unsupported tracking policy."),
    };

    public static ManagedIgnoreValidation Validate(string? content, TrackingPolicy policy)
    {
        if (content is null)
            return ManagedIgnoreValidation.Missing;

        if (content.StartsWith('\uFEFF') || content.Contains('\r'))
            return ManagedIgnoreValidation.Drift;

        string expected = For(policy);
        if (content == expected || HasExactlyOneBlock(content, expected))
            return ManagedIgnoreValidation.Valid;

        bool hasOpening = content.Contains(OpeningMarker, StringComparison.Ordinal);
        bool hasClosing = content.Contains(ClosingMarker, StringComparison.Ordinal);
        if (policy == TrackingPolicy.ProjectGit && !hasOpening && !hasClosing)
            return ManagedIgnoreValidation.Missing;

        return ManagedIgnoreValidation.Drift;
    }

    /// <summary>Replaces one attributable block, or appends it to an otherwise unrelated file.</summary>
    public static string Apply(string? existing, TrackingPolicy policy)
    {
        string normalized = Normalize(existing);
        string block = For(policy);
        if (string.IsNullOrEmpty(normalized))
            return block;

        if (normalized == Local || normalized == ProjectGit)
            return block;

        if (HasExactlyOneBlock(normalized, Local) || HasExactlyOneBlock(normalized, ProjectGit))
        {
            if (!TryReplaceBlock(normalized, block, out string replaced))
                throw new ScriniaConfigurationException("The managed ignore block is duplicated, malformed, or collides with custom content.");
            return replaced;
        }

        if (normalized.Contains(OpeningMarker, StringComparison.Ordinal) || normalized.Contains(ClosingMarker, StringComparison.Ordinal))
            throw new ScriniaConfigurationException("The managed ignore block is duplicated, malformed, or collides with custom content.");

        return EnsureTrailingNewline(normalized) + block;
    }

    private static string Normalize(string? content)
    {
        if (content is null)
            return "";
        if (content.StartsWith('\uFEFF'))
            throw new ScriniaConfigurationException("The managed ignore file must be UTF-8 without a BOM.");
        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static bool TryReplaceBlock(string content, string replacement, out string result)
    {
        string source = content == Local || content.Contains(Local, StringComparison.Ordinal) ? Local : ProjectGit;
        int blockStart = content.IndexOf(source, StringComparison.Ordinal);
        if (blockStart < 0 || content.IndexOf(source, blockStart + source.Length, StringComparison.Ordinal) >= 0)
        {
            result = "";
            return false;
        }

        result = content[..blockStart] + replacement + content[(blockStart + source.Length)..];
        return true;
    }

    private static bool HasExactlyOneBlock(string content, string block)
    {
        int index = content.IndexOf(block, StringComparison.Ordinal);
        return index >= 0 && content.IndexOf(block, index + block.Length, StringComparison.Ordinal) < 0 &&
            IsLineBoundary(content, index) && IsLineBoundary(content, index + block.Length);
    }

    private static bool IsLineBoundary(string text, int index) =>
        index == 0 || index == text.Length || text[index - 1] == '\n';

    private static string EnsureTrailingNewline(string text) => text.EndsWith('\n') ? text : text + "\n";
}
