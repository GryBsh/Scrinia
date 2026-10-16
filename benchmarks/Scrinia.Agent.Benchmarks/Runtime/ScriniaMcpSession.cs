using ModelContextProtocol.Client;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Scrinia.Agent.Benchmarks;

public sealed class ScriniaMcpSession : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly OwnedMcpProcess _process;
    private readonly Diagnostics _diagnostics;
    private readonly IRunEvents _events;
    private readonly object _lifetime = new();
    private readonly CancellationTokenSource _failure;
    private readonly Task _completionMonitor;
    private bool _prematureExit;
    private bool _disposed;
    public IReadOnlyList<McpClientTool> Tools { get; }
    public string CatalogHash { get; }
    public string ServerVersion => _client.ServerInfo.Version;
    public CancellationToken FailureToken => _failure.Token;
    internal int? CompletedProcessId { get; private set; }
    internal Task<ClientCompletionDetails> Completion => _client.Completion;

    private ScriniaMcpSession(McpClient client, OwnedMcpProcess process, IReadOnlyList<McpClientTool> tools, Diagnostics diagnostics, IRunEvents events)
    {
        _client = client; _process = process; Tools = tools; CatalogHash = ToolCatalog.ComputeHash(tools);
        _diagnostics = diagnostics; _events = events;
        _failure = CancellationTokenSource.CreateLinkedTokenSource(diagnostics.FailureToken);
        _completionMonitor = MonitorCompletionAsync();
    }

    public static async Task<ScriniaMcpSession> OpenAsync(ScriniaSettings settings, AttemptPaths paths, IRunEvents events, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(settings.Executable)) throw new FileNotFoundException("Scrinia executable is missing.", settings.Executable);
        PathSafety.RequireNoLinks(settings.Executable);
        var diagnostics = new Diagnostics(events);
        McpClient? client = null;
        OwnedMcpProcess? process = null;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, diagnostics.FailureToken);
            process = OwnedMcpProcess.Start(settings, paths, diagnostics.Accept);
            client = await McpClient.CreateAsync(process.Transport, cancellationToken: startup.Token);
            var tools = await client.ListToolsAsync(cancellationToken: startup.Token);
            if (client.ServerInfo.Name != "scri" || !tools.Any(t => t.Name == "guide"))
                throw new InvalidDataException("Expected the Scrinia workspace MCP server.");
            bool requiresPlugin = settings.WorkspaceConfig.Keys.Any(k => k.StartsWith("plugins:embeddings", StringComparison.OrdinalIgnoreCase)) ||
                (Directory.Exists(Path.Combine(paths.Home, "plugins")) && Directory.EnumerateFileSystemEntries(Path.Combine(paths.Home, "plugins")).Any());
            if (requiresPlugin) await diagnostics.RequirePluginReadyAsync(startup.Token);
            await diagnostics.CheckAsync();
            await events.WriteAsync("mcp", "connected", JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["name"] = client.ServerInfo.Name, ["version"] = client.ServerInfo.Version, ["catalogHash"] = ToolCatalog.ComputeHash(tools.ToArray())
            }, RecordJsonContext.Default.DictionaryStringString), ct);
            await events.WriteAsync("mcp", "catalog", ChatEvidence.Build(w =>
            {
                w.WriteStartArray("tools");
                foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                    JsonSerializer.Serialize(w, tool.ProtocolTool, RecordJsonContext.Default.Tool);
                w.WriteEndArray();
            }), ct);
            return new ScriniaMcpSession(client, process, tools.ToArray(), diagnostics, events);
        }
        catch
        {
            try { if (process is not null) await process.StopAsync(); }
            finally
            {
                try { if (client is not null) await client.DisposeAsync(); }
                finally
                {
                    process?.Dispose();
                    await diagnostics.DisposeAsync();
                }
            }
            throw;
        }
    }

    private async Task MonitorCompletionAsync()
    {
        await _client.Completion;
        lock (_lifetime)
        {
            if (_disposed) return;
            _prematureExit = true;
        }
        await _failure.CancelAsync();
    }

    public async Task CheckHealthAsync()
    {
        await _diagnostics.CheckAsync();
        lock (_lifetime)
        {
            if (_prematureExit || (!_disposed && (_client.Completion.IsCompleted || _process.HasExited)))
                throw new BenchmarkFailureException(BenchmarkFailureCode.MCP_DISCONNECTED, "Scrinia exited before the episode completed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _prematureExit |= _client.Completion.IsCompleted || _process.HasExited;
            _disposed = true;
        }
        try
        {
            try { await _process.StopAsync(); }
            finally { await _client.DisposeAsync(); }
            await _client.Completion;
            CompletedProcessId = _process.Id;
        }
        finally
        {
            try { await _completionMonitor; await _diagnostics.DisposeAsync(); }
            finally { _process.Dispose(); _failure.Dispose(); }
        }
        await _events.WriteAsync("mcp", "disconnected", JsonSerializer.SerializeToElement(
            CompletedProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable", ConfigurationJsonContext.Default.String), CancellationToken.None);
        if (_prematureExit)
            throw new BenchmarkFailureException(BenchmarkFailureCode.MCP_DISCONNECTED, "Scrinia exited before the episode completed.");
    }

    private sealed class Diagnostics : IAsyncDisposable
    {
        private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _failure = new();
        private readonly TaskCompletionSource _pluginReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _pump;
        private int _received;
        private int _dropped;
        private bool _disposed;
        public CancellationToken FailureToken => _failure.Token;

        public Diagnostics(IRunEvents events) => _pump = PumpAsync(events);

        public void Accept(string line)
        {
            if (line.Contains("Embeddings plugin ready (protocol=1,", StringComparison.Ordinal)) _pluginReady.TrySetResult();
            if (Interlocked.Increment(ref _received) > 512) { Interlocked.Increment(ref _dropped); return; }
            line = Regex.Replace(line, @"(?i)(authorization|api[_-]?key|password|secret)\s*[:=]\s*\S+", "$1=[redacted]", RegexOptions.CultureInvariant);
            if (line.Length > 4096) line = line[..4096] + " [truncated]";
            if (!_lines.Writer.TryWrite(line)) Interlocked.Increment(ref _dropped);
        }

        private async Task PumpAsync(IRunEvents events)
        {
            try
            {
                await foreach (var line in _lines.Reader.ReadAllAsync())
                    await events.WriteAsync("mcp", "stderr", JsonSerializer.SerializeToElement(line, ConfigurationJsonContext.Default.String), CancellationToken.None);
                if (_dropped > 0)
                    await events.WriteAsync("mcp", "stderr-truncated", JsonSerializer.SerializeToElement(
                        _dropped.ToString(System.Globalization.CultureInfo.InvariantCulture), ConfigurationJsonContext.Default.String), CancellationToken.None);
            }
            catch { await _failure.CancelAsync(); throw; }
        }

        public async Task RequirePluginReadyAsync(CancellationToken ct)
        {
            try { await _pluginReady.Task.WaitAsync(TimeSpan.FromSeconds(2), ct); }
            catch (TimeoutException) { throw new InvalidDataException("Requested embeddings plugin did not report usable capabilities; lexical fallback is not this configuration."); }
        }
        public async Task CheckAsync() { if (_pump.IsCompleted) await _pump; }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _lines.Writer.TryComplete();
            try { await _pump; }
            finally { _failure.Dispose(); }
        }
    }
}
