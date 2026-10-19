using System.Text;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using Scrinia.Agent.Benchmarks;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.LongMemEvalV2.Worker.Protocol;
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
        string yaml=Extract(result).Replace("\r\n","\n",StringComparison.Ordinal);
        if(!yaml.StartsWith("status: success\n",StringComparison.Ordinal))throw new InvalidDataException("EVIDENCE_RECALL_FAILED");
        int marker=yaml.IndexOf("\ncontent: |\n",StringComparison.Ordinal);
        if(marker<0)throw new InvalidDataException("EVIDENCE_RECALL_FAILED");
        string content=string.Join("\n",yaml[(marker+12)..].Split('\n').Select(line=>line.StartsWith("  ",StringComparison.Ordinal)?line[2..]:throw new InvalidDataException("EVIDENCE_RECALL_INVALID")));
        // Scrinia's YAML boundary trims only trailing whitespace. Comparing that exact
        // projection detects truncation while retaining original whitespace for the reader.
        string projection=$"Chunk {chunk}/{count}\n\n{expected}";
        if(content.TrimEnd()!=projection.Replace("\r\n","\n",StringComparison.Ordinal).TrimEnd())throw new InvalidDataException("EVIDENCE_RECALL_CHANGED_OR_TRUNCATED");
    }
    private static string Extract(object? result)=>result switch {
        string text=>text,
        TextContent text=>text.Text,
        IEnumerable<AIContent> contents=>string.Join("\n",contents.OfType<TextContent>().Select(c=>c.Text)),
        CallToolResult call when call.IsError!=true=>string.Join("\n",call.Content.OfType<TextContentBlock>().Select(c=>c.Text)),
        _=>throw new InvalidDataException("EVIDENCE_RECALL_INVALID")
    };
}
