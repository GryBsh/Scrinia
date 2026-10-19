using System.Text.Json;
namespace Scrinia.LongMemEvalV2.Worker.Evidence;

public sealed record EvidenceReference(string Kind,string? Path=null,int[]? Chunks=null,string? SourceId=null,int? StateIndex=null);
public sealed record EvidenceSelection(EvidenceReference[] References)
{
    public static EvidenceSelection Parse(string json)
    {
        using var document=JsonDocument.Parse(json);
        var root=document.RootElement;
        Protocol.WorkerProtocol.RejectDuplicateKeys(root);
        Exact(root,"references");
        var references=new List<EvidenceReference>();
        foreach(var item in root.GetProperty("references").EnumerateArray()) {
            switch(item.GetProperty("kind").GetString()) {
                case "memory":
                    Exact(item,"kind","path","chunks");
                    references.Add(new("memory",item.GetProperty("path").GetString(),item.GetProperty("chunks").EnumerateArray().Select(c=>c.GetInt32()).ToArray()));break;
                case "source":
                    Exact(item,"kind","sourceId","stateIndex");
                    references.Add(new("source",SourceId:item.GetProperty("sourceId").GetString(),StateIndex:item.GetProperty("stateIndex").GetInt32()));break;
                default:throw new InvalidDataException("EVIDENCE_SELECTION_INVALID");
            }
        }
        return new(references.ToArray());
    }
    private static void Exact(JsonElement value,params string[] names) {
        if(value.ValueKind!=JsonValueKind.Object || !value.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("EVIDENCE_SELECTION_INVALID");
    }
    public void Validate(IReadOnlyList<SourceCatalog> catalogs)
    {
        if(References is null)throw new InvalidDataException("EVIDENCE_SELECTION_INVALID");
        var sources=catalogs.SelectMany(c=>c.Fragments).ToDictionary(f=>f.SourceId,StringComparer.Ordinal);
        foreach(var reference in References) {
            if(reference.Kind=="source") {
                if(reference.SourceId is null || !sources.TryGetValue(reference.SourceId,out var source) || reference.StateIndex!=source.StateIndex
                    || reference.Path is not null || reference.Chunks is not null)throw new InvalidDataException("EVIDENCE_SOURCE_INVALID");
            } else if(reference.Kind!="memory" || reference.Path is null || reference.SourceId is not null || reference.StateIndex is not null
                || reference.Chunks is not {Length:>0} || reference.Chunks.Any(i=>i<1) || reference.Chunks.Distinct().Count()!=reference.Chunks.Length
                || !(reference.Path.StartsWith("/source/",StringComparison.Ordinal) || reference.Path.StartsWith("/derived/",StringComparison.Ordinal))
                || reference.Path.Contains("..",StringComparison.Ordinal) || reference.Path.Contains('\\') || reference.Path.Contains(':'))
                throw new InvalidDataException("EVIDENCE_MEMORY_INVALID");
        }
    }
}
