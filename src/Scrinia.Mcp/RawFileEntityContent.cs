using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using System.Text;

namespace Scrinia.Mcp;

internal enum FileEntityContentSource
{
    Disk,
    BuiltIn,
    PromotedAgent,
}

internal sealed record RawFileEntityContent<TMeta>(
    string Body, TMeta? Metadata, FileEntityContentSource Source) where TMeta : class;

public sealed partial class ScriniaMcpTools
{
    // Match File.ReadAllText's BOM handling for existing hand-authored markdown.
    private static string DecodeFileEntityBody(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static RawFileEntityContent<SkillFileMeta>? LoadRawSkill(
        string name, string? diskBody, SkillFileMeta? metadata)
    {
        if (diskBody is not null)
            return new(diskBody, metadata, FileEntityContentSource.Disk);
        return BuiltInSkills.TryGetValue(name, out string? builtIn)
            ? new(builtIn, metadata, FileEntityContentSource.BuiltIn)
            : null;
    }

    private static async ValueTask<RawFileEntityContent<AgentFileMeta>?> LoadRawAgentAsync(
        IMemoryStore store, string name, string? diskBody, AgentFileMeta? metadata, CancellationToken ct)
    {
        if (diskBody is not null)
            return new(diskBody, metadata, FileEntityContentSource.Disk);
        try
        {
            string artifact = await store.ReadArtifactAsync(MemoryAddress.Parse($"/agent/{name}"), ct);
            return new(DecodeFileEntityBody(Nmp2Strategy.Instance.Decode(artifact)), metadata,
                FileEntityContentSource.PromotedAgent);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
