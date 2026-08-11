using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for a defect where <c>BuildSummary</c>/<c>BuildFullList</c> used
/// <c>StringBuilder.AppendLine</c>, which emits <c>\r\n</c> on Windows, while <c>ToYaml</c>
/// splits the rendered content on <c>\n</c> only — leaving a stray <c>\r</c> at the end of
/// every rendered line.
///
/// This asserts directly against the builders' raw return value, not the full MCP response
/// via <c>ToYaml</c>/<c>GoldenNormalizer</c>: <see cref="Golden.GoldenNormalizer"/> strips
/// every <c>\r</c> unconditionally (deliberately, for cross-platform golden stability — see
/// its own comment), so a golden-based test can never see this defect. Calling the builders
/// directly is the only way to observe the raw, unnormalized output.
///
/// <para><b>This guard binds only on a host where <c>Environment.NewLine</c> is <c>\r\n</c>
/// (Windows).</b> <c>AppendLine</c> emits the platform's <c>Environment.NewLine</c>, which is
/// <c>\n</c> on Linux, so on Linux there is no stray <c>\r</c> for <c>NotContain("\r")</c> to
/// catch even if the builders regressed back to <c>AppendLine</c>. CI runs on
/// <c>ubuntu-latest</c> (<c>.github/workflows/ci.yml</c>), so CI does NOT enforce this guard —
/// a regression here can reintroduce the defect and stay green. No Linux-hosted test can detect
/// a Windows-only defect, so this is not "fixed" by making the assertion cross-platform; the
/// goldens can't cover the gap either, since <c>GoldenNormalizer</c> strips <c>\r</c> on the
/// write path before a fixture is ever compared. This test is a Windows-only regression guard,
/// known and accepted as such.</para>
/// </summary>
public sealed class ListLineEndingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public ListLineEndingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-list-crlf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static List<ScopedArtifact> OneEntry() =>
    [
        new("local", new ArtifactEntry(
            Name: "some-note",
            Uri: "file://some-note",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: "a preview")),
    ];

    [Fact]
    public void BuildSummary_RenderedOutput_ContainsNoCarriageReturn()
    {
        string output = ScriniaMcpTools.BuildSummary(OneEntry(), _store);

        output.Should().NotContain("\r",
            because: "AppendLine emits \\r\\n on Windows; the builder must be \\n-only so " +
                      "ToYaml's Split('\\n') doesn't leave a stray \\r on every line");
    }

    [Fact]
    public void BuildFullList_RenderedOutput_ContainsNoCarriageReturn()
    {
        string output = ScriniaMcpTools.BuildFullList(OneEntry(), _store, 0, 50);

        output.Should().NotContain("\r",
            because: "AppendLine emits \\r\\n on Windows; the builder must be \\n-only so " +
                      "ToYaml's Split('\\n') doesn't leave a stray \\r on every line");
    }
}
