using System.Runtime.InteropServices;
using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// Task 4 (round 1) — reversible, platform-specific argv rendering for the retired setup's
/// print-only hint. Human output must render each argument with the platform's standard quoting
/// (§8.3) so a copied command re-parses to exactly the original argument and can never
/// interpolate <c>$</c>, backticks, backslashes, spaces, or quotes. Nothing here launches a
/// process — the renderers only format strings.
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
                string parsed = Marshal.PtrToStringUni(strPtr);
                parsed.Should().Be(input,
                    $"'{escaped}' must round-trip to the original '{input}' with no interpolation");
            }
            finally
            {
                if (argv != IntPtr.Zero) LocalFree(argv);
            }
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
