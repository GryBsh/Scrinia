using System.Runtime.InteropServices;
using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// Task 4 — reversible, platform-specific argv rendering for the retired setup's print-only hint.
/// Human output must render each argument with the platform's standard quoting (§8.3) so a copied
/// command re-parses to exactly the original argument and can never interpolate <c>$</c>, backticks,
/// backslashes, spaces, or quotes. Nothing here launches a process — the renderers only format
/// strings. Windows output is additionally cmd.exe-safe: a root containing a metacharacter that
/// cmd.exe would still interpret inside a copied command (for example <c>%</c>, <c>^</c>, operators,
/// or quotes) falls back to the exact <c>&lt;workspace-root&gt;</c> argv placeholder rather than
/// emitting unsafe runnable text.
/// </summary>
public sealed class CommandLineQuotingTests
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string pCmdLine, out int pArgvCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    // ---- Windows: the documented CommandLineToArgvW inverse escaping round-trips ----

    [Fact]
    public void Windows_RoundTripsThroughCommandLineToArgvW()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "CommandLineToArgvW (shell32.dll) is Windows-only; the round-trip is verified on Windows.");

        string[] inputs =
        [
            "C:\\repos\\team-memory",   // no quoting needed → returned verbatim
            "C:\\my repo\\team",        // space → wrapped in quotes
            "C:\\repo\\foo\\",          // one trailing backslash must survive (1:1, not doubled)
            "C:\\Program Files\\Scrinia", // space + backslashes
            "trailing\\\\",             // two trailing backslashes must survive
            "$(`x)\\\\",                // $ and backtick are literal; two trailing backslashes
            "C:\\a b\\c\\",             // space AND trailing backslash
            "",                         // empty
        ];

        foreach (string input in inputs)
        {
            string escaped = PlatformQuoting.Windows(input);
            int argc;
            IntPtr argv = CommandLineToArgvW(escaped, out argc);
            argv.Should().NotBeNull(
                $"Windows escaping of '{input}' must parse back through CommandLineToArgvW");

            try
            {
                argc.Should().Be(1, "the workspace root is a single argv element");
                IntPtr strPtr = Marshal.ReadIntPtr(argv);
                string? parsed = Marshal.PtrToStringUni(strPtr);
                parsed.Should().Be(input,
                    $"'{escaped}' must round-trip to the original '{input}' with no interpolation");
            }
            finally
            {
                if (argv != IntPtr.Zero) LocalFree(argv);
            }
        }
    }

    // ---- Windows: cmd.exe metacharacters fall back to the §8.3 placeholder ----

    [Theory]
    [InlineData("C:\\repo\\%USERPROFILE%", "<workspace-root>")] // % → variable expansion
    [InlineData("C:\\repo\\!var!", "<workspace-root>")]          // ! → delayed expansion
    [InlineData("C:\\repo\\a&b", "<workspace-root>")]            // & → command separator
    [InlineData("C:\\repo\\a|b", "<workspace-root>")]            // | → pipe
    [InlineData("C:\\repo\\a<b", "<workspace-root>")]            // < → redirection
    [InlineData("C:\\repo\\a>b", "<workspace-root>")]            // > → redirection
    [InlineData("C:\\repo\\a^b", "<workspace-root>")]            // ^ → escape (alters next char)
    [InlineData("C:\\repo\\a\tb", "<workspace-root>")]           // tab → separator inside quotes
    [InlineData("C:\\repo\\\"quote\"", "<workspace-root>")]      // " → quote toggle
    public void Windows_FallsBackToPlaceholderWhenShellInterpretsRoot(string input, string expected)
    {
        PlatformQuoting.Windows(input).Should().Be(expected);
    }

    [Fact]
    public void Windows_PlaceholderIsAValidSingleArgvElement()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "CommandLineToArgvW (shell32.dll) is Windows-only; the placeholder argv shape is verified on Windows.");

        // The §8.3 fallback placeholder must itself be a single, parseable argv element: a copied
        // "git -C <workspace-root> ..." command still has exactly the right shape.
        string placeholder = PlatformQuoting.Windows("C:\\repo\\%x");
        placeholder.Should().Be("<workspace-root>");

        int argc;
        IntPtr argv = CommandLineToArgvW(placeholder, out argc);
        argv.Should().NotBeNull();
        try
        {
            argc.Should().Be(1);
            string? parsed = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv));
            parsed.Should().Be("<workspace-root>");
        }
        finally
        {
            if (argv != IntPtr.Zero) LocalFree(argv);
        }
    }

    // ---- POSIX: single-quote with the '"'"' embedded-quote escape ----

    [Theory]
    [InlineData("normal", "'normal'")]
    [InlineData("with space", "'with space'")]
    // 'a' + '"'"' + 'b'  →  'a'"'"'b'
    [InlineData("a'b", "'a'\"'\"'b'")]
    public void Posix_SingleQuotesAndEscapesEmbeddedQuotes(string input, string expected)
    {
        PlatformQuoting.Posix(input).Should().Be(expected);
    }

    [Fact]
    public void Posix_PreservesInterpolationCharactersLiterally()
    {
        // $, backtick, and backslash are preserved verbatim inside the single-quoted render:
        // that is precisely what prevents a shell from interpolating them.
        string input = "$(`x)\\trailing";
        string rendered = PlatformQuoting.Posix(input);

        rendered.Should().Be($"'{input}'");
        rendered.Should().StartWith("'").And.EndWith("'");
        // Only the wrapping single quotes are added — no double quote is introduced, so the
        // literal $, backtick, and backslashes cannot escape the quoting.
        rendered.Should().NotContain("\"");
    }

    // ---- PowerShell: single-quote with doubled embedded quotes ----

    [Theory]
    [InlineData("normal", "'normal'")]
    [InlineData("with space", "'with space'")]
    [InlineData("a'b", "'a''b'")]
    public void PowerShell_DoublesEmbeddedSingleQuotes(string input, string expected)
    {
        PlatformQuoting.PowerShell(input).Should().Be(expected);
    }

    // ---- Current picks the platform's renderer ----

    [Fact]
    public void Current_UsesWindowsQuotingOnWindowsAndPosixOtherwise()
    {
        string spaced = "a b";
        if (OperatingSystem.IsWindows())
            PlatformQuoting.Current(spaced).Should().Be("\"a b\"");
        else
            PlatformQuoting.Current(spaced).Should().Be("'a b'");
    }
}
