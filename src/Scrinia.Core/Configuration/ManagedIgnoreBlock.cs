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
        if (policy == TrackingPolicy.ProjectGit && TryGetProjectRegion(content, out _, out _))
            return ManagedIgnoreValidation.Valid;
        if (policy == TrackingPolicy.Local && HasExactlyOneBlock(content, expected) && !HasAnyProjectMarker(content))
            return ManagedIgnoreValidation.Valid;

        bool hasOpening = HasAnyProjectMarker(content);
        bool hasClosing = hasOpening;
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

        bool hasLocalBlock = HasExactlyOneBlock(normalized, Local);
        bool hasProjectBlock = TryGetProjectRegion(normalized, out _, out _);
        if (hasLocalBlock && hasProjectBlock)
            throw new ScriniaConfigurationException("The ignore file contains both local and project-git managed policies.");

        if (HasAnyProjectMarker(normalized) && !TryGetProjectRegion(normalized, out _, out _))
            throw new ScriniaConfigurationException("The managed ignore block is duplicated, malformed, or collides with custom content.");

        if (normalized == Local || normalized == ProjectGit)
            return block;

        if (hasLocalBlock || hasProjectBlock)
        {
            if (!TryReplaceBlock(normalized, block, out string replaced))
                throw new ScriniaConfigurationException("The managed ignore block is duplicated, malformed, or collides with custom content.");
            return replaced;
        }

        if (HasAnyProjectMarker(normalized))
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
        string source = content.Contains(Local, StringComparison.Ordinal) ? Local : ProjectGit;
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

    private static bool TryGetProjectRegion(string content, out int start, out int end)
    {
        start = end = -1;
        string[] lines = content.Split('\n');
        int openingCount = 0;
        int closingCount = 0;
        int openingLine = -1;
        int closingLine = -1;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line == OpeningMarker)
            {
                openingCount++;
                openingLine = index;
                continue;
            }
            if (line == ClosingMarker)
            {
                closingCount++;
                closingLine = index;
                continue;
            }
            if (IsMarkerLike(line))
                return false;
        }

        if (openingCount != 1 || closingCount != 1 || openingLine < 0 || closingLine <= openingLine)
            return false;

        int lineStart = lines[..openingLine].Sum(line => line.Length + 1);
        int lineEnd = lines[..(closingLine + 1)].Sum(line => line.Length + 1);
        string region = content[lineStart..Math.Min(lineEnd, content.Length)];
        if (region != ProjectGit)
            return false;

        start = lineStart;
        end = lineEnd;
        return true;
    }

    private static bool HasAnyProjectMarker(string content) =>
        content.Split('\n').Any(IsMarkerLike);

    private static bool IsMarkerLike(string line) =>
        line.StartsWith("# >>> Scrinia managed:", StringComparison.Ordinal) ||
        line.StartsWith("# <<< Scrinia managed:", StringComparison.Ordinal);

    private static string EnsureTrailingNewline(string text) => text.EndsWith('\n') ? text : text + "\n";
}
