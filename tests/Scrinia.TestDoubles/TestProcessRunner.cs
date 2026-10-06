using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Scrinia.TestDoubles;

/// <summary>Runs the built test harness with bounded event/control I/O and child-only cleanup.</summary>
public sealed class TestProcessRunner : IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly Process _process;
    private readonly StringBuilder _diagnostics = new();
    private readonly Lock _diagnosticsGate = new();
    private readonly Task _stderrDrain;
    private int _disposed;

    private TestProcessRunner(Process process)
    {
        _process = process;
        _stderrDrain = DrainStderrAsync();
    }

    public static Task<TestProcessRunner> StartAsync(string[] args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = output.Parent?.Name
            ?? throw new InvalidOperationException("Cannot locate the test output configuration.");
        string harness = RepoPaths.Combine("tests", "Scrinia.TestProcess", "bin", configuration,
            output.Name, "Scrinia.TestProcess.dll");
        if (!File.Exists(harness))
            throw new FileNotFoundException("The test harness was not built by the test project reference.", harness);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoPaths.Root()
        };
        startInfo.ArgumentList.Add(harness);
        foreach (string argument in args)
            startInfo.ArgumentList.Add(argument);
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The test harness process did not start.");
            return Task.FromResult(new TestProcessRunner(process));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async Task ReadEventAsync(string expected, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(expected);
        string? line = await WithDeadlineAsync(
            token => _process.StandardOutput.ReadLineAsync(token).AsTask(), ct).ConfigureAwait(false);
        if (line is null)
            throw await ProtocolFailureAsync($"Expected event '{expected}', but stdout ended.").ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("event", out var eventValue)
                || eventValue.ValueKind != JsonValueKind.String
                || !string.Equals(eventValue.GetString(), expected, StringComparison.Ordinal))
                throw await ProtocolFailureAsync($"Expected event '{expected}', received '{line}'.").ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw await ProtocolFailureAsync($"Expected event '{expected}', received malformed JSON '{line}'.").ConfigureAwait(false);
        }
    }

    public async Task SendAsync(string command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        if (command.Contains('\r') || command.Contains('\n'))
            throw new ArgumentException("A control command must be one line.", nameof(command));
        await WithDeadlineAsync(async token =>
        {
            await _process.StandardInput.WriteLineAsync(command.AsMemory(), token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public Task<int> WaitForExitAsync(CancellationToken ct) => WithDeadlineAsync(async token =>
    {
        await _process.WaitForExitAsync(token).ConfigureAwait(false);
        await _stderrDrain.WaitAsync(token).ConfigureAwait(false);
        return _process.ExitCode;
    }, ct);

    private async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(OperationTimeout);
        try { return await action(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw;
            throw new TimeoutException($"The test harness exceeded its 30-second deadline. Stderr: {Diagnostics()}");
        }
    }

    private async Task DrainStderrAsync()
    {
        while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (_diagnosticsGate)
                _diagnostics.AppendLine(line);
        }
    }

    private string Diagnostics()
    {
        lock (_diagnosticsGate)
            return _diagnostics.ToString();
    }

    private async Task<InvalidDataException> ProtocolFailureAsync(string message)
    {
        await StopAsync().ConfigureAwait(false);
        return new InvalidDataException($"{message} Stderr: {Diagnostics()}");
    }

    private async Task StopAsync()
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) when (_process.HasExited) { }
        }
        using var deadline = new CancellationTokenSource(OperationTimeout);
        await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        await _stderrDrain.WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { await StopAsync().ConfigureAwait(false); }
        finally { _process.Dispose(); }
    }
}
