namespace Scrinia.Core;

internal static class MultiMemoryStableDetail
{
    internal static string? Sanitize(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail.Length > 240 ||
            !detail.All(IsStableDetailCharacter) || Path.IsPathRooted(detail) ||
            detail.StartsWith('~') || detail.StartsWith("//", StringComparison.Ordinal) ||
            detail.StartsWith('/') ||
            detail.Contains('/', StringComparison.Ordinal) || detail.Contains('\\') ||
            detail.Contains("System.", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("sidecar", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("secret", StringComparison.OrdinalIgnoreCase))
            return null;

        return detail;
    }

    internal static bool IsStableDetailCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == ' ' ||
        ".,:;_()-".Contains(character, StringComparison.Ordinal);
}
