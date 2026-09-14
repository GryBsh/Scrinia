namespace Scrinia.Core;

internal static class MultiMemoryStableDetail
{
    internal const string Fallback = "observer diagnostic omitted";

    internal static string? Sanitize(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail.Length > 240 ||
            !detail.All(IsStableDetailCharacter) ||
            detail.StartsWith('~') || detail.StartsWith("//", StringComparison.Ordinal) ||
            detail.StartsWith('/') ||
            detail.Contains('/', StringComparison.Ordinal) || detail.Contains('\\') ||
            detail.Contains("System.", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("sidecar", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            IsColonOnlyUriOrDriveRelative(detail))
            return null;

        return detail;
    }

    internal static bool IsStableDetailCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == ' ' ||
        ".,:;_()-".Contains(character, StringComparison.Ordinal);

    internal static string SanitizeOrFallback(string? detail) => Sanitize(detail) ?? Fallback;

    private static bool IsColonOnlyUriOrDriveRelative(string detail)
    {
        int colon = detail.IndexOf(':');
        if (colon <= 0) return false;

        for (int i = 0; i < colon; i++)
        {
            char character = detail[i];
            if (i == 0 && !char.IsLetter(character)) return false;
            if (i > 0 && !char.IsLetterOrDigit(character) && character is not '+' and not '-' and not '.')
                return false;
        }

        if (colon == 1 && char.IsLetter(detail[0]))
        {
            if (detail.Length == colon + 1) return true;
            return !char.IsWhiteSpace(detail[colon + 1]);
        }

        return detail.Length > colon + 1 && !char.IsWhiteSpace(detail[colon + 1]);
    }
}
