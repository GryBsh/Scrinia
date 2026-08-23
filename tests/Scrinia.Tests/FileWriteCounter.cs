using Scrinia.Core;

namespace Scrinia.Tests;

public sealed record FileWriteMeasurement(string Path, long Bytes);
public sealed record FileReadMeasurement(string Path, long Bytes);

public sealed class FileWriteCounter : IDisposable
{
    private readonly string? _pathPrefix;
    private readonly List<FileWriteMeasurement> _writes = [];
    private readonly List<FileReadMeasurement> _reads = [];
    private readonly object _gate = new();
    private readonly Action<string, byte[]>? _previousObserver;
    private readonly Action<string, long>? _previousReadObserver;

    public FileWriteCounter(string? pathPrefix = null)
    {
        _pathPrefix = pathPrefix is null ? null : Path.GetFullPath(pathPrefix);
        _previousObserver = FileMemoryStore.WriteObserver;
        _previousReadObserver = FileMemoryStore.ReadObserver;
        FileMemoryStore.WriteObserver = RecordWrite;
        FileMemoryStore.ReadObserver = RecordRead;
    }

    public IReadOnlyList<FileWriteMeasurement> Writes
    {
        get { lock (_gate) return _writes.ToArray(); }
    }

    public IReadOnlyList<FileReadMeasurement> Reads
    {
        get { lock (_gate) return _reads.ToArray(); }
    }

    public IReadOnlyList<FileWriteMeasurement> FactsJsonWrites =>
        Writes
            .Where(write => _pathPrefix is null || write.Path.StartsWith(_pathPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(write => write.Path.EndsWith("facts.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public long FactsJsonBytes => FactsJsonWrites.Sum(write => write.Bytes);

    public IReadOnlyList<FileReadMeasurement> FactsJsonReads =>
        Reads
            .Where(read => _pathPrefix is null || read.Path.StartsWith(_pathPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(read => read.Path.EndsWith("facts.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public long FactsJsonReadBytes => FactsJsonReads.Sum(read => read.Bytes);

    public void Reset()
    {
        lock (_gate)
        {
            _writes.Clear();
            _reads.Clear();
        }
    }

    public void Dispose()
    {
        FileMemoryStore.WriteObserver = _previousObserver;
        FileMemoryStore.ReadObserver = _previousReadObserver;
    }

    private void RecordWrite(string path, byte[] bytes)
    {
        lock (_gate) _writes.Add(new FileWriteMeasurement(path, bytes.LongLength));
    }

    private void RecordRead(string path, long bytes)
    {
        lock (_gate) _reads.Add(new FileReadMeasurement(path, bytes));
    }
}
