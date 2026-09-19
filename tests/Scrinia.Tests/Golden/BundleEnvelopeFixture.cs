using System.Text.RegularExpressions;
using Scrinia.Core;
using Scrinia.Mcp;
using Scrinia.Tests.Golden;

namespace Scrinia.Tests.Golden;

/// <summary>
/// Drives the <c>Export</c> bundle facade so its successful envelope can be captured and pinned.
///
/// <para>The facade renders the absolute bundle path into the success content, which embeds the
/// throwaway <c>TestHelpers.StoreScope</c> GUID (<c>scrinia_test_-&lt;32 hex&gt;</c>). That GUID is a
/// pure function of the test host, not of the export behaviour, so <see cref="RenderSuccess"/> strips
/// it through the shared <see cref="GoldenNormalizer"/> before returning — the same structural
/// equivalence rule the remember / compact / import goldens rely on. A fixed export filename is used
/// so the volatile <c>export-&lt;timestamp&gt;</c> default never reaches the pinned envelope.</para>
///
/// <para>The topic is seeded deterministically (a single fixed-length body), so the exported topic
/// count, entry count, and bundled file size reported by the facade are a pure function of the input
/// and stay byte-stable against the committed golden.</para>
/// </summary>
internal static class BundleEnvelopeFixture
{
    private const string Topic = "api";
    private const string Subject = "api-flow";
    private const string Body = "How session rotation works.";
    private const string FixedBundleName = "bundle-success";

    // The facade reports the real on-disk bundle size ("773 B"), but that size drifts by a byte or
    // two between runs: the bundle carries per-entry timestamps whose serialization length is not
    // constant, so a raw size pin would be flaky. Strip it to a placeholder so the golden stays
    // structural — the same reasoning that strips the workspace GUID above.
    private static readonly Regex BundleSize = new(@"\d+(?:\.\d+)? (?:B|KB|MB|GB)", RegexOptions.Compiled);

    /// <summary>Renders the successful <c>Export</c> facade envelope with the volatile workspace
    //  GUID and bundle-file-size stripped, so the pinned golden is structural rather than run-dependent.</summary>
    public static McpResponse RenderSuccess()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        BundleExportFixture.SeedValidTopic(store, Topic, Subject, Body);

        McpResponse facade = new ScriniaMcpTools()
            .Export([Topic], filename: FixedBundleName)
            .GetAwaiter()
            .GetResult();

        string content = BundleSize.Replace(GoldenNormalizer.Normalize(facade.Content ?? string.Empty), "<SIZE>");
        return facade with { Content = content };
    }
}
