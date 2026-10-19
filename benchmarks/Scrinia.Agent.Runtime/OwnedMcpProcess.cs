using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Protocol;

namespace Scrinia.Agent.Benchmarks;

/// <summary>Owns a native stdio child so shutdown can signal EOF before waiting for exit.</summary>
internal sealed class OwnedMcpProcess : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly Process _process;
    private readonly Task _stderr;
    private Task? _stop;

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public StreamClientTransport Transport { get; }

    private OwnedMcpProcess(Process process, Action<string> stderr)
    {
        _process = process;
        Transport = new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        _stderr = ReadStderrAsync(stderr);
    }

    public static OwnedMcpProcess Start(ScriniaSettings settings, AttemptPaths paths, Action<string> stderr)
    {
        var start = new ProcessStartInfo(settings.Executable)
        {
            WorkingDirectory = paths.Workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in settings.PrefixArguments.Concat(["serve", "--no-auto-setup", "--workspace-root", paths.Workspace]))
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (key, value) in RuntimeEnvironment.Create(paths))
            start.Environment[key] = value;
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("Scrinia process did not start.");
            return new OwnedMcpProcess(process, stderr);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private async Task ReadStderrAsync(Action<string> accept)
    {
        while (await _process.StandardError.ReadLineAsync() is { } line) accept(line);
    }

    public Task StopAsync() => _stop ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        // MCP stdio shutdown starts with EOF. Waiting first spends the entire timeout
        // on a healthy server that is still waiting for the next request.
        try { _process.StandardInput.Close(); }
        catch (IOException) { /* An already-exited child may have closed its pipe. */ }
        try { await _process.WaitForExitAsync().WaitAsync(ShutdownTimeout); }
        catch (TimeoutException)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(ShutdownTimeout);
        }
        await _stderr.WaitAsync(ShutdownTimeout);
    }

    public void Dispose() => _process.Dispose();
}
