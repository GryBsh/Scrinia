using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker.Protocol;
namespace Scrinia.LongMemEvalV2.Worker.Evidence;

public sealed record SourceMemory(string SourceId, int StateIndex, int Step, string Url, string? Action,
    string? Thoughts, int StartByte, int EndByte, string Text, string TextHash);

public sealed record SourceCatalog
{
    public required TrajectoryManifest Trajectory { get; init; }
    public required SourceFragment[] Fragments { get; init; }
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string LogicalHash(TrajectoryManifest trajectory)
    {
        var value=JsonSerializer.SerializeToElement(trajectory,WorkerJsonContext.Default.TrajectoryManifest);
        var output=new StringBuilder();
        Write(value,root:true);
        return Hash(Encoding.UTF8.GetBytes(output.ToString()));
        void Quote(string text) {
            output.Append('"');
            foreach (char character in text) {
                switch(character) {
                    case '"': output.Append("\\\"");break;
                    case '\\': output.Append("\\\\");break;
                    case '\b': output.Append("\\b");break;
                    case '\f': output.Append("\\f");break;
                    case '\n': output.Append("\\n");break;
                    case '\r': output.Append("\\r");break;
                    case '\t': output.Append("\\t");break;
                    default:
                        if (character<32 || character>126) output.Append("\\u").Append(((int)character).ToString("x4",System.Globalization.CultureInfo.InvariantCulture));
                        else output.Append(character);
                        break;
                }
            }
            output.Append('"');
        }
        void Write(JsonElement element,bool root=false,bool image=false) {
            switch(element.ValueKind) {
                case JsonValueKind.Object:
                    output.Append('{');bool first=true;
                    foreach(var property in element.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)) {
                        if ((root && property.Name=="sourceHash") || (image && property.Name=="path")) continue;
                        if (!first) output.Append(',');first=false;Quote(property.Name);output.Append(':');
                        Write(property.Value,image:property.Name=="screenshot");
                    }
                    output.Append('}');break;
                case JsonValueKind.Array:
                    output.Append('[');bool firstItem=true;
                    foreach(var item in element.EnumerateArray()) {if(!firstItem)output.Append(',');firstItem=false;Write(item);}
                    output.Append(']');break;
                case JsonValueKind.String: Quote(element.GetString()!);break;
                default: output.Append(element.GetRawText());break;
            }
        }
    }
    public static string SourceId(string hash, int state, int fragment) =>
        Hash(Encoding.UTF8.GetBytes($"{hash}:{state}:{fragment}"));
    public static string SourcePath(string hash, SourceFragment fragment) =>
        $"/source/{hash}/{fragment.StateIndex:D6}/{fragment.FragmentIndex:D6}";
    public SourceMemory Materialize(SourceFragment fragment)
    {
        var state = Trajectory.States[fragment.StateIndex];
        return new(fragment.SourceId, state.StateIndex, state.Step, state.Url, state.Action,
            state.Thoughts, fragment.StartByte, fragment.EndByte, fragment.Text, fragment.TextHash);
    }
    public static void ValidateFragments(TrajectoryManifest trajectory, IReadOnlyList<SourceFragment> fragments)
    {
        if (trajectory.States.Length == 0 || fragments.Count == 0) throw new InvalidDataException("SOURCE_INCOMPLETE");
        int cursor = 0;
        for (int stateIndex = 0; stateIndex < trajectory.States.Length; stateIndex++)
        {
            var state = trajectory.States[stateIndex];
            if (state.StateIndex != stateIndex) throw new InvalidDataException("SOURCE_ORDER");
            var text = Encoding.UTF8.GetBytes(state.Text);
            int offset = 0, index = 0;
            while (cursor < fragments.Count && fragments[cursor].StateIndex == stateIndex)
            {
                var fragment = fragments[cursor++];
                var bytes = Encoding.UTF8.GetBytes(fragment.Text);
                if (fragment.TextHash != Hash(bytes) || fragment.FragmentIndex != index || fragment.StartByte != offset
                    || fragment.EndByte != offset + bytes.Length || fragment.EndByte > text.Length
                    || !bytes.AsSpan().SequenceEqual(text.AsSpan(offset, bytes.Length))
                    || fragment.SourceId != SourceId(trajectory.SourceHash, stateIndex, index))
                    throw new InvalidDataException("SOURCE_FRAGMENT_INVALID");
                offset = fragment.EndByte; index++;
            }
            if (index == 0 || offset != text.Length) throw new InvalidDataException("SOURCE_INCOMPLETE");
        }
        if (cursor != fragments.Count) throw new InvalidDataException("SOURCE_ORDER");
    }
    public static SourceCatalog Create(TrajectoryManifest trajectory, SourceFragment[] fragments, string snapshot)
    {
        ValidateFragments(trajectory, fragments);
        if (LogicalHash(trajectory)!=trajectory.SourceHash) throw new InvalidDataException("SOURCE_HASH_INVALID");
        var states = trajectory.States.Select(state =>
        {
            var image = state.Screenshot;
            var path = Path.GetFullPath(image.Path);
            PathSafety.RequireNoLinks(path);
            if (!PathSafety.Contains(snapshot, path) || !File.Exists(path)
                || new FileInfo(path).Length != image.ByteLength || Hash(File.ReadAllBytes(path)) != image.Sha256)
                throw new InvalidDataException("SOURCE_IMAGE_INVALID");
            return state with { Screenshot = image with { Path = Path.GetRelativePath(snapshot, path).Replace('\\','/') } };
        }).ToArray();
        return new() { Trajectory = trajectory with { States = states }, Fragments = fragments };
    }
    public static SourceCatalog[] ReadAll(string snapshot)
    {
        var directory = Path.Combine(snapshot, "catalogs");
        PathSafety.RequireNoLinks(directory);
        if (!Directory.Exists(directory)) throw new InvalidDataException("SOURCE_CATALOG_MISSING");
        return Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).Select(path =>
        {
            PathSafety.RequireNoLinks(path);
            return JsonSerializer.Deserialize(File.ReadAllText(path), WorkerJsonContext.Default.SourceCatalog)
                ?? throw new InvalidDataException("SOURCE_CATALOG_INVALID");
        }).ToArray();
    }
    public string ResolveImage(string snapshot, ImageReference image)
    {
        if (Path.IsPathFullyQualified(image.Path)) throw new InvalidDataException("SOURCE_IMAGE_INVALID");
        var path = Path.GetFullPath(Path.Combine(snapshot, image.Path));
        PathSafety.RequireNoLinks(path);
        if (!PathSafety.Contains(snapshot, path)) throw new InvalidDataException("SOURCE_IMAGE_INVALID");
        return path;
    }
}
