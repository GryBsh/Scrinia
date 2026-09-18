using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests.Golden;

/// <summary>
/// Pins the import facade's YAML envelope so the B1 wiring of the per-memory
/// <c>ImportMemoryResult</c> through <c>FileMemoryStore.ImportTopicEntries</c> and
/// <c>BundleFormatService.ImportTopicsFromZip</c> cannot change the multi-topic summary a model
/// sees. The facade write pass now captures the journaled outcome but discards it
/// (<c>_ = importResult</c>); the summary string is the only thing <c>Bundle('import')</c>
/// renders, so this golden is the guard against reintroducing an <c>operationId</c>/<c>entry</c>/
/// <c>recovery</c> clause into the success path.
///
/// <para>Two seeded topics are exported through the real <see cref="BundleFormatService.ExportTopicsToZip"/>
/// path and imported through the real facade; the manifest preserves export order, so the
/// <c>names</c> list is deterministic.</para>
/// </summary>
public sealed class ImportFacadeMappingTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task Seed(IMemoryStore store)
    {
        store.Upsert(new ArtifactEntry("api-auth-flow", "file://export", 40, 1, Fixed,
            ContentPreview: "How session rotation works."), "local:api");
        await store.WriteArtifactAsync("api-auth-flow", "local:api",
            Nmp2ChunkedEncoder.Encode("How session rotation works."));

        store.Upsert(new ArtifactEntry("arch-decision", "file://export", 32, 1, Fixed,
            ContentPreview: "Why we chose this architecture."), "local:arch");
        await store.WriteArtifactAsync("arch-decision", "local:arch",
            Nmp2ChunkedEncoder.Encode("Why we chose this architecture."));
    }

    /// <summary>
    /// Exports the seeded topics to a bundle inside the workspace (the facade's SEC-041 path
    /// containment check rejects any bundle outside <c>store.WorkspaceRoot</c>) and imports it
    /// through the <c>Bundle('import')</c> facade, returning the rendered YAML.
    /// </summary>
    private static async Task<string> ImportThroughFacade(ScriniaMcpTools tools, IMemoryStore store)
    {
        string bundlePath = Path.Combine(store.WorkspaceRoot, ".scrinia", "exports", "import-success.scrinia-bundle");
        Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
        using var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            BundleFormatService.ExportTopicsToZip(zip, store, ["api", "arch"]);

        return await tools.Bundle("import", bundlePath: bundlePath, overwrite: true);
    }

    [Fact]
    public async Task Import_Success_MapsToStableEnvelope()
    {
        using var scope = new TestHelpers.StoreScope();
        await Seed(MemoryStoreContext.Current!);
        var tools = new ScriniaMcpTools();

        string yaml = await ImportThroughFacade(tools, MemoryStoreContext.Current!);
        string actual = GoldenNormalizer.Normalize(yaml);

        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable("SCRINIA_UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        string expected = GoldenNormalizer.Normalize(File.ReadAllText(path));
        actual.Should().Be(expected,
            "import facade envelope must be byte-identical across the ImportMemoryResult wiring; " +
            "set SCRINIA_UPDATE_GOLDEN=1 only for an intentional change");
    }

    /// <summary>Golden files live next to the source, not in bin/ — they are committed artifacts.</summary>
    private static string GoldenPath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", "import-success.yaml");
}
