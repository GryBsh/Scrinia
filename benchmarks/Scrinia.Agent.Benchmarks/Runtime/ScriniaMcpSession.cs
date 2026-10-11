using ModelContextProtocol.Client;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Scrinia.Agent.Benchmarks;

public sealed class ScriniaMcpSession : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly Diagnostics _diagnostics;
    private readonly IRunEvents _events;
    private bool _disposed;
    public IReadOnlyList<McpClientTool> Tools { get; }
    public string CatalogHash { get; }
    public string ServerVersion => _client.ServerInfo.Version;
    public CancellationToken FailureToken => _diagnostics.FailureToken;
    internal int? CompletedProcessId { get; private set; }

    private ScriniaMcpSession(McpClient client, IReadOnlyList<McpClientTool> tools, Diagnostics diagnostics, IRunEvents events)
    {
        _client = client; Tools = tools; CatalogHash = ToolCatalog.ComputeHash(tools);
        _diagnostics = diagnostics; _events = events;
    }

    public static async Task<ScriniaMcpSession> OpenAsync(ScriniaSettings settings, AttemptPaths paths, IRunEvents events, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(settings.Executable)) throw new FileNotFoundException("Scrinia executable is missing.", settings.Executable);
        PathSafety.RequireNoLinks(settings.Executable);
        var diagnostics = new Diagnostics(events);
        McpClient? client = null;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, diagnostics.FailureToken);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = settings.Executable,
                Arguments = [.. settings.PrefixArguments, "serve", "--no-auto-setup", "--workspace-root", paths.Workspace],
                WorkingDirectory = paths.Workspace,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = new AttemptWorkspace().CreateEnvironment(paths),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
                StandardErrorLines = diagnostics.Accept
            });
            client = await McpClient.CreateAsync(transport, cancellationToken: startup.Token);
            var tools = await client.ListToolsAsync(cancellationToken: startup.Token);
            if (client.ServerInfo.Name != "scri" || !tools.Any(t => t.Name == "guide"))
                throw new InvalidDataException("Expected the Scrinia workspace MCP server.");
            bool requiresPlugin = settings.WorkspaceConfig.Keys.Any(k => k.StartsWith("plugins:embeddings", StringComparison.OrdinalIgnoreCase)) ||
                (Directory.Exists(Path.Combine(paths.Home, "plugins")) && Directory.EnumerateFileSystemEntries(Path.Combine(paths.Home, "plugins")).Any());
            if (requiresPlugin) await diagnostics.RequirePluginReadyAsync(startup.Token);
            await diagnostics.CheckAsync();
            var result = new ScriniaMcpSession(client, tools.ToArray(), diagnostics, events);
            await events.WriteAsync("mcp", "connected", JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["name"] = client.ServerInfo.Name, ["version"] = client.ServerInfo.Version, ["catalogHash"] = result.CatalogHash
            }, RecordJsonContext.Default.DictionaryStringString), ct);
            return result;
        }
        catch
        {
            try { if (client is not null) await client.DisposeAsync(); }
            finally { await diagnostics.DisposeAsync(); }
            throw;
        }
    }

    public Task CheckHealthAsync() => _diagnostics.CheckAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _client.DisposeAsync();
            var completion = await _client.Completion;
            if (completion is StdioClientCompletionDetails stdio) CompletedProcessId = stdio.ProcessId;
        }
        finally { await _diagnostics.DisposeAsync(); }
        await _events.WriteAsync("mcp", "disconnected", JsonSerializer.SerializeToElement(
            CompletedProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable", ConfigurationJsonContext.Default.String), CancellationToken.None);
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
