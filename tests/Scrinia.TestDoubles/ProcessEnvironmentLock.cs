namespace Scrinia.TestDoubles;

/// <summary>Serializes tests that mutate process-wide environment variables, including across test assemblies.</summary>
public sealed class ProcessEnvironmentLock : IDisposable
{
    private static readonly string LockPath = Path.Combine(Path.GetTempPath(), "scrinia-tests-process-environment.lock");
    private readonly FileStream _lockFile;
    private bool _released;

    private ProcessEnvironmentLock(FileStream lockFile) => _lockFile = lockFile;

    public static ProcessEnvironmentLock Acquire()
    {
        while (true)
        {
            try
            {
                return new ProcessEnvironmentLock(new FileStream(
                    LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }
    }

    public void Dispose()
    {
        if (_released)
            return;

        _released = true;
        _lockFile.Dispose();
    }
}

/// <summary>Restores one process-wide environment variable when the scope ends.</summary>
public sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previousValue;
    private bool _disposed;

    public EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previousValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Environment.SetEnvironmentVariable(_name, _previousValue);
    }
}
