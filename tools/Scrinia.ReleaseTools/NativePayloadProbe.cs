using System.Runtime.InteropServices;
using Scrinia.Commands;

namespace Scrinia.ReleaseTools;

internal static class NativePayloadProbe
{
    internal static void Run(string payload, string rid)
    {
        if (EmbeddingsRuntimeIdentity.CurrentRid != rid)
            throw new InvalidOperationException($"Native RID mismatch: raw={EmbeddingsRuntimeIdentity.RawRid}, resolved={EmbeddingsRuntimeIdentity.CurrentRid}, requested={rid}.");
        Console.WriteLine($"OS={RuntimeInformation.OSDescription}; process={RuntimeInformation.ProcessArchitecture}; rawRID={EmbeddingsRuntimeIdentity.RawRid}; resolvedRID={rid}");
        payload = Path.GetFullPath(payload);
        string[] approved = EmbeddingsBundlePolicy.GetFileNames(rid).Order(StringComparer.Ordinal).ToArray();
        string[] actual = Directory.EnumerateFileSystemEntries(payload).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        if (!actual.SequenceEqual(approved, StringComparer.Ordinal)) throw new InvalidDataException("Native payload inventory differs from approved policy.");
        foreach (string file in Directory.EnumerateFileSystemEntries(payload))
            if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Linked or nested native payload refused.");
        var handles = new List<nint>();
        try
        {
            // Load dependencies first; Linux and macOS ggml link their backends by SONAME/install name.
            // Every approved library is independently required, then released in reverse order.
            string[] order = rid == "win-x64"
                ? ["ggml-base.dll", "ggml.dll", "ggml-cpu.dll", "ggml-vulkan.dll", "llama.dll", "mtmd.dll"]
                : rid == "linux-x64"
                    ? ["libggml-base.so", "libggml-cpu.so", "libggml-vulkan.so", "libggml.so", "libllama.so", "libmtmd.so"]
                    : ["libggml-base.dylib", "libggml-cpu.dylib", "libggml-blas.dylib", "libggml-metal.dylib", "libggml.dylib", "libllama.dylib", "libmtmd.dylib"];
            foreach (string name in order)
            {
                handles.Add(NativeLibrary.Load(Path.Combine(payload, name), typeof(NativePayloadProbe).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies));
                Console.WriteLine($"Loaded {name} by absolute path.");
            }
        }
        finally
        {
            for (int i = handles.Count - 1; i >= 0; i--) NativeLibrary.Free(handles[i]);
        }
    }
}
