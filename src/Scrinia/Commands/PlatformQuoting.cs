using System.Text;

namespace Scrinia.Commands;

/// <summary>
/// Reversible, platform-specific rendering of a single argv element for human-readable command
/// guidance. Per the plan §8.3, human output must render each argument with the platform's standard
/// reversible quoting so a copied command re-parses to exactly the original argument — never
/// interpolating <c>$</c>, backticks, backslashes, spaces, or quotes. Scrinia never executes these
/// strings; it only prints them for the agent to run.
/// </summary>
internal static class PlatformQuoting
{
    /// <summary>
    /// Renders <paramref name="arg"/> with the current OS's standard reversible quoting:
    /// <see cref="Windows"/> on Windows, <see cref="Posix"/> everywhere else.
    /// </summary>
    internal static string Current(string arg) =>
        OperatingSystem.IsWindows() ? Windows(arg) : Posix(arg);

    /// <summary>
    /// Windows "native process" escaping — the inverse of
    /// <see cref="CommandLineToArgvW(string, out int)">CommandLineToArgvW</see>. Outside quotes
    /// every character is literal and only a space splits the token, so a space-less argument
    /// round-trips verbatim (preserving embedded quotes and backslashes). A space forces the
    /// whole argument to be quoted; inside the quotes backslashes stay literal (a trailing run
    /// needs no doubling) but a lone quote would close the region, so each embedded quote is
    /// doubled. This is the platform's standard reversible rendering for human-copied commands.
    /// </summary>
    internal static string Windows(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        // No space: every character is literal outside quotes, so emit verbatim.
        if (arg.IndexOf(' ') < 0)
            return arg;

        // A space requires quoting; inside quotes backslashes are literal and quotes are doubled.
        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        foreach (char c in arg)
            sb.Append(c == '"' ? "\"" : c);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// POSIX (sh/bash) single-quote escaping: wrap in single quotes and represent each embedded
    /// single quote as <c>'"'"'</c> (close the quote, emit a double-quoted single quote, reopen).
    /// Inside single quotes <c>$</c>, backticks, backslashes, and spaces are all literal, so nothing
    /// interpolates.
    /// </summary>
    internal static string Posix(string arg)
    {
        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('\'');
        sb.Append(arg.Replace("'", "'\"'\"'"));
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>
    /// PowerShell single-quote escaping: wrap in single quotes and double every embedded single
    /// quote, the PowerShell rule for a literal quote inside a single-quoted string.
    /// </summary>
    internal static string PowerShell(string arg)
    {
        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('\'');
        sb.Append(arg.Replace("'", "''"));
        sb.Append('\'');
        return sb.ToString();
    }
}
