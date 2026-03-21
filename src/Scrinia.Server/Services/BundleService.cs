using System.IO.Compression;
using Scrinia.Core;
using Scrinia.Core.Bundles;

namespace Scrinia.Server.Services;

/// <summary>
/// HTTP API bridge for bundle export/import.
/// Delegates format I/O to BundleFormatService; handles stream management.
/// </summary>
public static class BundleService
{
    public static MemoryStream ExportToStream(IMemoryStore store, string[] topics)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            BundleFormatService.ExportTopicsToZip(zip, store, topics);

        ms.Position = 0;
        return ms;
    }

    public static (int TopicCount, int EntryCount, List<string> Names) ImportFromStream(
        IMemoryStore store, Stream bundle, string[]? topics, bool overwrite)
    {
        using var zip = new ZipArchive(bundle, ZipArchiveMode.Read);
        return BundleFormatService.ImportTopicsFromZip(zip, store, topics, overwrite);
    }
}
