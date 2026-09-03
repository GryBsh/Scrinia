namespace Scrinia.Core.Migration;
// Note: System.Text.Encoding is fully-qualified below because Scrinia.Core.Encoding (the NMP/2
// encoding strategy namespace) would otherwise shadow it from within the Scrinia.Core tree.

/// <summary>
/// Migration-only recognition of legacy Scrinia merge configuration.
///
/// This type is recognition alone. It exposes the exact legacy <c>.gitattributes</c> lines, the
/// exact legacy <c>merge.config</c> bytes, and the static driver-unset argument sequences that
/// Phase 2's migration implementation consumes. It performs no file mutation, no command
/// execution, no registry mutation, no merge algorithm, and no conflict resolution.
///
/// Per the merge-retirement plan this is the only merge contract that survives Phase 1; every
/// active merge surface is removed by later tasks.
/// </summary>
public static class LegacyMergeRecognition
{
    /// <summary>The exact managed <c>*.meta.json</c> line written to <c>.scrinia/.gitattributes</c>.</summary>
    public const string ScriniaMetaAttributeLine = "*.meta.json merge=scrinia-meta";

    /// <summary>The exact managed <c>*.nmp2</c> line written to <c>.scrinia/.gitattributes</c>.</summary>
    public const string ScriniaNmp2AttributeLine = "*.nmp2 merge=scrinia-nmp2";

    // Canonical .scrinia/merge.config: the default managed serialization plus one trailing LF.
    // System.Text.Json emits indented camelCase JSON with CRLF internal newlines; the final byte
    // is the single LF the spec names. Verified byte-exact at 62 bytes.
    private static readonly byte[] LegacyMergeConfigBytes = System.Text.Encoding.UTF8.GetBytes(
        "{\r\n  \"jaccardThreshold\": 0.7,\r\n  \"conflictDir\": \"conflict\"\r\n}\n");

    /// <summary>
    /// Returns true only when <paramref name="line"/> equals one of the two exact managed
    /// <c>.gitattributes</c> lines. Per the §8.3 line-ending rule a trailing carriage return from a
    /// CRLF file is normalized away before the exact comparison.
    /// </summary>
    /// <param name="line">A single line from <c>.scrinia/.gitattributes</c>, or null.</param>
    /// <returns><c>true</c> if and only if <paramref name="line"/> is a managed line.</returns>
    public static bool IsRecognizedGitAttributesLine(string? line)
    {
        if (line is null)
            return false;

        var normalized = line.Length > 0 && line[^1] == '\r' ? line[..^1] : line;
        return normalized == ScriniaMetaAttributeLine || normalized == ScriniaNmp2AttributeLine;
    }

    /// <summary>
    /// Returns true only when <paramref name="bytes"/> equal the exact legacy merge output plus one
    /// trailing LF. Any other sequence — different threshold, different conflict directory, missing
    /// or extra byte, or different internal newlines — is rejected.
    /// </summary>
    /// <param name="bytes">The raw bytes of a candidate <c>.scrinia/merge.config</c>.</param>
    /// <returns><c>true</c> if and only if <paramref name="bytes"/> is the canonical legacy output.</returns>
    public static bool IsExactLegacyMergeConfig(ReadOnlySpan<byte> bytes) =>
        bytes.SequenceEqual(LegacyMergeConfigBytes);

    /// <summary>
    /// Returns the two static <c>git config --local --unset-all</c> argument sequences in classifier
    /// order (scrinia-meta before scrinia-nmp2), mirroring §8.3. Each carries a
    /// <c>&lt;workspace-root&gt;</c> placeholder for the CLI to substitute with the absolute workspace
    /// root before printing. This returns no process launch, no mutation, and no merge logic.
    /// </summary>
    public static IReadOnlyList<string> DriverUnsetArgv() =>
    [
        "git", "-C", "<workspace-root>", "config", "--local", "--unset-all", "merge.scrinia-meta.driver",
        "git", "-C", "<workspace-root>", "config", "--local", "--unset-all", "merge.scrinia-nmp2.driver",
    ];
}
