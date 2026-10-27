using System.Text;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using Scrinia.Agent.Benchmarks;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.LongMemEvalV2.Worker.Protocol;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
namespace Scrinia.LongMemEvalV2.Worker.Evidence;

public sealed class EvidenceMaterializer(string snapshot)
{
    public Task<EvidenceItem[]> MaterializeAsync(EvidenceSelection selection,SourceCatalog catalog,ScriniaMcpSession session,CancellationToken ct)
        =>MaterializeAsync(selection,[catalog],session,ct);
    public async Task<EvidenceItem[]> MaterializeAsync(EvidenceSelection selection,IReadOnlyList<SourceCatalog> catalogs,ScriniaMcpSession session,CancellationToken ct)
    {
        selection.Validate(catalogs);
        var items=new List<EvidenceItem>();
        var sources=catalogs.SelectMany(c=>c.Fragments.Select(f=>(f.SourceId,Catalog:c,Fragment:f))).ToDictionary(p=>p.SourceId,StringComparer.Ordinal);
        var store=new FileMemoryStore(Path.Combine(snapshot,"workspace"));
        foreach(var reference in selection.References) {
            ct.ThrowIfCancellationRequested();
            if(reference.Kind=="memory") {
                var address=MemoryAddress.Parse(reference.Path!);
                var entry=await store.ReadAsync(address,ct);
                if(entry is null || entry.Address.Value!=reference.Path)throw new InvalidDataException("EVIDENCE_MEMORY_MISSING");
                string artifact=await store.ReadArtifactAsync(address,ct);
                foreach(int chunk in reference.Chunks!) {
                    if(chunk>entry.ChunkCount)throw new InvalidDataException("EVIDENCE_CHUNK_INVALID");
                    string actual=Nmp2ChunkedEncoder.DecodeChunk(artifact,chunk);
                    // Recall is independently checked against the complete original chunk. Never
                    // pass a truncated display or controller quotation to the official reader.
                    await RequireRecallAsync(session,reference.Path!,chunk,entry.ChunkCount,actual,ct);
                    items.Add(new() {Type="text",Value=$"Memory {reference.Path}, chunk {chunk}/{entry.ChunkCount}\n{actual}"});
                }
            } else {
                var source=sources[reference.SourceId!];var catalog=source.Catalog;var fragment=source.Fragment;
                string path=SourceCatalog.SourcePath(catalog.Trajectory.SourceHash,fragment);
                string record=Learning.SourceSeeder.ContentFor(catalog,fragment);
                var address=MemoryAddress.Parse(path);
                var entry=await store.ReadAsync(address,ct);
                string[] chunks=StorageChunks.Split(record);
                if(entry is null || entry.ChunkCount!=chunks.Length)throw new InvalidDataException("EVIDENCE_MEMORY_MISSING");
                string artifact=await store.ReadArtifactAsync(address,ct);
                var reconstructed=new StringBuilder();
                for(int chunk=1;chunk<=chunks.Length;chunk++) {
                    string actual=Nmp2ChunkedEncoder.DecodeChunk(artifact,chunk);
                    if(actual!=chunks[chunk-1])throw new InvalidDataException("EVIDENCE_SOURCE_CHANGED");
                    await RequireRecallAsync(session,path,chunk,chunks.Length,actual,ct);
                    reconstructed.Append(actual);
                }
                if(reconstructed.ToString()!=record)throw new InvalidDataException("EVIDENCE_SOURCE_CHANGED");
                if(SourceCatalog.Hash(Encoding.UTF8.GetBytes(fragment.Text))!=fragment.TextHash)throw new InvalidDataException("EVIDENCE_TEXT_CHANGED");
                var state=catalog.Trajectory.States[fragment.StateIndex];
                items.Add(new() {Type="text",Value=$"Original source {fragment.SourceId}; trajectory {catalog.Trajectory.Id}; state {state.StateIndex}; step {state.Step}; URL {state.Url}; UTF-8 bytes [{fragment.StartByte},{fragment.EndByte})\nAction: {state.Action}\nThoughts: {state.Thoughts}\n{fragment.Text}"});
                string imagePath=RequireSourceImagePath(catalog,snapshot,state.Screenshot);
                await VerifyImageAsync(imagePath,state.Screenshot,ct);
                items.Add(new() {Type="image",Value=imagePath});
            }
        }
        await session.CheckHealthAsync();
        return items.ToArray();
    }
    public static string RequireSourceImagePath(SourceCatalog catalog,string snapshot,ImageReference image) {
        if(!image.Path.StartsWith("sources/",StringComparison.Ordinal) || image.Path.Contains('\\')
            || image.Path.Split('/').Any(part=>part is ".." or "." or ""))throw new InvalidDataException("EVIDENCE_IMAGE_PATH_INVALID");
        return catalog.ResolveImage(snapshot,image);
    }
    public static async Task<byte[]> VerifyImageAsync(string path,ImageReference image,CancellationToken ct) {
        PathSafety.RequireNoLinks(path);
        var bytes=await File.ReadAllBytesAsync(path,ct);
        if(bytes.LongLength!=image.ByteLength || SourceCatalog.Hash(bytes)!=image.Sha256)throw new InvalidDataException("EVIDENCE_IMAGE_CHANGED");
        return bytes;
    }
    private static async Task RequireRecallAsync(ScriniaMcpSession session,string path,int chunk,int count,string expected,CancellationToken ct) {
        var result=await session.Tools.Single(t=>t.Name=="recall").InvokeAsync(new AIFunctionArguments {["path"]=path,["chunk"]=chunk},ct);
        RequireRecallContent(Extract(result),chunk,count,expected);
    }
    internal static void RequireRecallContent(string yaml,int chunk,int count,string expected) {
        // Parse the YAML scalar so quoting and block chomping preserve the exact
        // recalled characters. The representation model also rejects duplicate keys.
        var stream=new YamlStream();
        try {
            using var reader=new StringReader(yaml);
            stream.Load(reader);
        } catch(YamlException error) {
            throw new InvalidDataException("EVIDENCE_RECALL_INVALID",error);
        }
        if(stream.Documents.Count!=1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("EVIDENCE_RECALL_INVALID");
        if(!root.Children.TryGetValue(new YamlScalarNode("status"),out var status) || status is not YamlScalarNode {Value:"success"})
            throw new InvalidDataException("EVIDENCE_RECALL_FAILED");
        if(!root.Children.TryGetValue(new YamlScalarNode("content"),out var content) || content is not YamlScalarNode scalar)
            throw new InvalidDataException("EVIDENCE_RECALL_FAILED");
        if(!string.Equals(scalar.Value,$"Chunk {chunk}/{count}\n\n{expected}",StringComparison.Ordinal))
            throw new InvalidDataException("EVIDENCE_RECALL_CHANGED_OR_TRUNCATED");
    }
    private static string Extract(object? result)=>result switch {
        string text=>text,
        TextContent text=>text.Text,
        IEnumerable<AIContent> contents=>string.Join("\n",contents.OfType<TextContent>().Select(c=>c.Text)),
        CallToolResult call when call.IsError!=true=>string.Join("\n",call.Content.OfType<TextContentBlock>().Select(c=>c.Text)),
        _=>throw new InvalidDataException("EVIDENCE_RECALL_INVALID")
    };
}
