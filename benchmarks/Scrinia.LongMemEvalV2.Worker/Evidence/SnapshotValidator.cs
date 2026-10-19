using System.Text;
using Scrinia.Agent.Benchmarks;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.LongMemEvalV2.Worker.Learning;
namespace Scrinia.LongMemEvalV2.Worker.Evidence;

public sealed class SnapshotValidator
{
    public static string Fingerprint(string root)
    {
        PathSafety.RequireNoLinks(root);
        var files = new List<(string Name,string Hash)>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                PathSafety.RequireNoLinks(entry);
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                if (!File.Exists(entry)) throw new InvalidDataException("SNAPSHOT_FILE_INVALID");
                string relative = Path.GetRelativePath(root,entry).Replace('\\','/');
                if (relative == ".generation.json") continue;
                if (relative.Contains('\n') || relative.Contains('\r')) throw new InvalidDataException("SNAPSHOT_PATH_INVALID");
                files.Add((relative,SourceCatalog.Hash(File.ReadAllBytes(entry))));
            }
        }
        var bytes = Encoding.UTF8.GetBytes(string.Concat(files.OrderBy(f=>f.Name,StringComparer.Ordinal).Select(f=>f.Name+"\0"+f.Hash+"\n")));
        return SourceCatalog.Hash(bytes);
    }
    public async Task ValidateAsync(string snapshot,string expectedFingerprint,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Fingerprint(snapshot) != expectedFingerprint) throw new InvalidDataException("SNAPSHOT_HASH_INVALID");
        var catalogs = SourceCatalog.ReadAll(snapshot);
        if (catalogs.Length == 0 || catalogs.Select(c=>c.Trajectory.Id).Distinct(StringComparer.Ordinal).Count()!=catalogs.Length)
            throw new InvalidDataException("SOURCE_CATALOG_INVALID");
        var allSources = catalogs.SelectMany(c=>c.Fragments.Select(f=>(f.SourceId,c.Trajectory.SourceHash))).ToDictionary(p=>p.SourceId,p=>p.SourceHash,StringComparer.Ordinal);
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        var store = new FileMemoryStore(Path.Combine(snapshot,"workspace"));
        foreach (var catalog in catalogs)
        {
            SourceCatalog.ValidateFragments(catalog.Trajectory,catalog.Fragments);
            if (SourceCatalog.LogicalHash(catalog.Trajectory)!=catalog.Trajectory.SourceHash)
                throw new InvalidDataException("SOURCE_HASH_INVALID");
            foreach (var state in catalog.Trajectory.States)
            {
                var path = catalog.ResolveImage(snapshot,state.Screenshot);
                if (new FileInfo(path).Length != state.Screenshot.ByteLength || SourceCatalog.Hash(await File.ReadAllBytesAsync(path,ct)) != state.Screenshot.Sha256)
                    throw new InvalidDataException("SOURCE_IMAGE_INVALID");
            }
            var header = "/source/" + catalog.Trajectory.SourceHash + "/trajectory";
            await RequireMemoryAsync(store,header,catalog.Trajectory.OriginalJson,ct); expectedPaths.Add(header);
            foreach (var fragment in catalog.Fragments)
            {
                var path = SourceCatalog.SourcePath(catalog.Trajectory.SourceHash,fragment);
                await RequireMemoryAsync(store,path,SourceSeeder.ContentFor(catalog,fragment),ct);expectedPaths.Add(path);
            }
        }
        foreach (var item in store.ListScoped())
        {
            ct.ThrowIfCancellationRequested();
            var entry = item.Entry; var path = entry.Address.Value;
            if (expectedPaths.Contains(path)) continue;
            if (!path.StartsWith("/derived/",StringComparison.Ordinal) || entry.Attributes is null
                || !entry.Attributes.TryGetValue("source",out var references) || references.Length == 0
                || references.Any(id => !allSources.TryGetValue(id,out var hash)
                    || !path.StartsWith("/derived/"+hash+"/",StringComparison.Ordinal)))
                throw new InvalidDataException("DERIVED_SOURCE_REFERENCE_INVALID");
            // Decode all data as well; corrupted derived notes cannot be published.
            _ = Nmp2Strategy.Instance.Decode(await store.ReadArtifactAsync(entry.Address,ct));
        }
        // Store reads may perform legacy maintenance; any mutation invalidates this generation.
        if (Fingerprint(snapshot) != expectedFingerprint) throw new InvalidDataException("SNAPSHOT_CHANGED_DURING_VALIDATION");
    }
    private static async Task RequireMemoryAsync(FileMemoryStore store,string path,string expected,CancellationToken ct)
    {
        var address = MemoryAddress.Parse(path);
        var entry = await store.ReadAsync(address,ct);
        if (entry is null || entry.ChunkCount != 1) throw new InvalidDataException("SOURCE_MEMORY_MISSING");
        var text = Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(await store.ReadArtifactAsync(address,ct)));
        if (text != expected) throw new InvalidDataException("SOURCE_MEMORY_CHANGED");
    }
}
