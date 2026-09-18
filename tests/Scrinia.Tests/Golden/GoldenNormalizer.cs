using System.Text.RegularExpressions;

namespace Scrinia.Tests.Golden;

/// <summary>
/// Strips the three fields of an MCP YAML response that are not a pure function of the
/// fixture, so a golden file stays stable across days and machines.
///
/// Deliberately blunt about dates: this harness pins STRUCTURE across a refactor, not date
/// formatting. `created:` / `updated:` rendering is pinned by the existing search-result
/// tests. Do not narrow these patterns to "make the golden stricter" — recency scoring makes
/// scores drift daily and the temp-dir GUID changes every run.
/// </summary>
internal static class GoldenNormalizer
{
    private static readonly Regex Score = new(@"score=\d+(\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex TempDir = new(@"scrinia_test_[0-9a-fA-F]{32}", RegexOptions.Compiled);
    private static readonly Regex OperationId = new(@"(?<=operationId: )[0-9a-fA-F]{32}", RegexOptions.Compiled);
    // The file-entity coordinator names its retained recovery evidence <operationId>.recovery, so a
    // sidecar/skill/agent success_with_warning or recovery_required envelope embeds a volatile GUID
    // in journalPath (".file-entity-<32hex>.recovery"). The operationId: field is already normalised
    // above; this catches the same GUID wherever it survives in the path so the sidecar goldens stay
    // structural, not machine-dependent — exactly the same rule the remember/compact goldens rely on.
    private static readonly Regex FileEntityEvidence = new(@".file-entity-[0-9a-fA-F]{32}\.recovery", RegexOptions.Compiled);
    private static readonly Regex IsoTimestamp = new(@"\d{4}-\d{2}-\d{2}T[\d:.]+(?:Z|[+\-]\d{2}:\d{2})", RegexOptions.Compiled);
    private static readonly Regex BareDate = new(@"\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    public static string Normalize(string yaml)
    {
        // Strip every CR outright rather than Replace("\r\n", "\n"): some formatted bodies
        // (e.g. list's Markdown summary) double up the carriage return to "\r\r\n" on Windows,
        // and a single left-to-right Replace("\r\n", "\n") pass leaves one \r behind — stable
        // under a SECOND Normalize() pass (which is exactly what re-reading an already-written
        // golden file does), but not the first, which made list-summary/list-full fail only on
        // the write-then-compare cycle. Deleting every \r sidesteps the pairing question
        // entirely: this harness only cares about line content, not which platform wrote it.
        //
        // This is deliberately broader than the \r\r\n bug required: it also makes a plain
        // CRLF-vs-LF line-ending CHANGE invisible to every golden. That is an intentional
        // trade-off, not an oversight — line-ending fidelity is out of scope for a harness whose
        // job is structural equivalence across a refactor. Do not narrow this back to
        // Replace("\r\n", "\n") to "catch" line-ending drift; that reintroduces the exact
        // write-then-compare flakiness this comment is describing.
        string s = yaml.Replace("\r", "");
        s = Score.Replace(s, "score=<SCORE>");
        s = TempDir.Replace(s, "<WORKSPACE>");
        s = OperationId.Replace(s, "<OPERATION_ID>");
        s = FileEntityEvidence.Replace(s, ".file-entity-<EVIDENCE>");
        s = IsoTimestamp.Replace(s, "<TIMESTAMP>");
        s = BareDate.Replace(s, "<DATE>");
        return s.TrimEnd('\n');
    }
}
