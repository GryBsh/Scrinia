using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scrinia.Commands;

namespace Scrinia.Tests;

internal sealed class EmbeddingsBundleTestFixture : IDisposable
{
    private readonly string _container = Path.Combine(Path.GetTempPath(), "scrinia-bundle-" + Guid.NewGuid().ToString("N"));
    internal string Root { get; }
    internal string Home { get; }
    internal string Workspace { get; }
    internal string PayloadDirectory { get; }
    internal string ManifestPath => Path.Combine(Root, "embeddings-bundle.json");
    internal EmbeddingsBundleManifest Manifest { get; private set; }

    private EmbeddingsBundleTestFixture(string version, string rid)
    {
        Root = Path.Combine(_container, "bundle");
        Home = Path.Combine(_container, "home");
        Workspace = Path.Combine(_container, "workspace");
        PayloadDirectory = Path.Combine(Root, "plugins", "scri-plugin-embeddings", version, rid);
        Directory.CreateDirectory(PayloadDirectory);
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Path.Combine(Root, rid == "win-x64" ? "scri.exe" : "scri"), "host");
        var files = new List<EmbeddingsBundleFile>();
        foreach (string name in EmbeddingsBundlePolicy.GetFileNames(rid))
        {
            byte[] bytes = Encoding.UTF8.GetBytes("deterministic bundle payload: " + rid + "/" + name);
            File.WriteAllBytes(Path.Combine(PayloadDirectory, name), bytes);
            files.Add(new(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        Manifest = new(1, "scri-plugin-embeddings", version, rid, files.ToArray());
        WriteManifest(Manifest);
    }

    internal static EmbeddingsBundleTestFixture Create(string version, string rid) => new(version, rid);

    internal void WriteManifest(EmbeddingsBundleManifest manifest)
    {
        Manifest = manifest;
        File.WriteAllBytes(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, CliJsonContext.Default.EmbeddingsBundleManifest));
    }

    public void Dispose()
    {
        string container = Path.GetFullPath(_container);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!container.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped temp.");
        if (Directory.Exists(container)) Directory.Delete(container, recursive: true);
    }
}
