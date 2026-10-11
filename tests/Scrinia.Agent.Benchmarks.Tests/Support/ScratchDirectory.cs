namespace Scrinia.Agent.Benchmarks.Tests;

internal sealed class ScratchDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "scrinia-agent-tests", Guid.NewGuid().ToString("N"));
    public ScratchDirectory() => Directory.CreateDirectory(Root);
    public string Write(string name, string content)
    {
        string path = Path.Combine(Root, name);
        File.WriteAllText(path, content);
        return path;
    }
    public void Dispose()
    {
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "scrinia-agent-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scratch directory left test root.");
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
