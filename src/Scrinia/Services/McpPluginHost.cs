using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Search;

namespace Scrinia.Services;

/// <summary>
/// Manages a child-process embeddings plugin via MCP over stdio.
/// The plugin is an MCP server; this host is the MCP client.
/// Implements <see cref="ISearchScoreContributor"/> and <see cref="IMemoryEventSink"/>
/// so the host can wire it into the standard AsyncLocal contexts.
///
/// If the plugin process crashes, it reconnects automatically up to <see cref="MaxRestarts"/>
/// times. After that, it degrades permanently to BM25-only search for the session.
/// </summary>
internal sealed class McpPluginHost : ISearchScoreContributor, IMemoryEventSink, IAsyncDisposable
{
    /// <summary>
    /// The one operation this host performs against the plugin process. Extracted so tests can
    /// observe which tool a write dispatches to without starting a child process; production
    /// still goes through McpClient.CallToolAsync.
    /// </summary>
    internal interface IPluginToolCaller
    {
        Task<string?> CallToolAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);
    }

    private sealed class McpClientToolCaller(McpClient client) : IPluginToolCaller
    {
        public async Task<string?> CallToolAsync(
            string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: ct);
            return GetTextContent(result);
        }
    }

    private McpClient? _client;
    private IPluginToolCaller? _toolCaller;
    private string _exePath = "";
    private string[] _arguments = [];
    private Dictionary<string, string?> _environment = new();
    private int _failCount;
    private const int MaxRestarts = 3;
    private bool _degraded;

    // Capability flags discovered via ListToolsAsync
    private bool _hasSearch, _hasRemove, _hasStatus, _hasIndex;
    // Set to true only when the plugin's status reports available=true (i.e. a real provider, not NullEmbeddingProvider)
    private bool _isAvailable;
    internal string? ProtocolFailureCode { get; private set; }

    /// <summary>Production construction path — connects lazily via <see cref="StartAsync"/>.</summary>
    public McpPluginHost() { }

    /// <summary>Test-only: supply a tool caller and capability set directly, skipping StartAsync.</summary>
    internal McpPluginHost(IPluginToolCaller toolCaller, IEnumerable<string> toolNames)
    {
        _toolCaller = toolCaller;
        var names = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
        _hasSearch = names.Contains("search");
        _hasRemove = names.Contains("remove");
        _hasStatus = names.Contains("status");
        _hasIndex = names.Contains("index");
        _isAvailable = true;
    }

    public bool HasSearchCapability => _hasSearch && _isAvailable && !_degraded;
    public bool HasEventSinkCapability => (_hasIndex || _hasRemove) && _isAvailable && !_degraded;

    /// <summary>
    /// Starts the plugin process via MCP stdio transport. Verifies it's alive with a status call.
    /// </summary>
    public async Task StartAsync(string exePath, string dataDir, string modelsDir,
        Func<string, string?> getConfig, CancellationToken ct)
    {
        _exePath = exePath;
        _arguments = BuildArguments(dataDir, modelsDir, getConfig);
        _environment = BuildSecretEnvironment(getConfig);
        await ConnectAsync(ct);

        await NegotiateStatusAsync(ct);
        if (ProtocolFailureCode is not null && _client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
            _toolCaller = null;
        }
    }

    internal async Task NegotiateStatusAsync(CancellationToken ct = default)
    {
        if (!_hasStatus || _toolCaller is null)
        {
            DisableForSession("EMBEDDINGS_PROTOCOL_INVALID", "status capability is missing");
            return;
        }

        try
        {
            string? text = await _toolCaller.CallToolAsync("status", new Dictionary<string, object?>(), ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                DisableForSession("EMBEDDINGS_PROTOCOL_INVALID", "status response is empty");
                return;
            }

            EmbeddingsStatusResponse? status = JsonSerializer.Deserialize(
                text, PluginClientJsonContext.Default.EmbeddingsStatusResponse);
            Exception? failure = EmbeddingsProtocol.ValidateStatus(status);
            if (failure is not null)
            {
                DisableForSession(
                    failure is EmbeddingsProtocolUnsupportedException
                        ? "EMBEDDINGS_PROTOCOL_UNSUPPORTED"
                        : "EMBEDDINGS_PROTOCOL_INVALID",
                    failure.Message);
                return;
            }

            _isAvailable = true;
            Console.Error.WriteLine(
                $"[scrinia:info] Embeddings plugin ready (protocol=1, dimensions={status!.Dimensions}, " +
                $"signature={FormatPluginStatusValue(status.VectorStoreSignature)})");
        }
        catch (JsonException ex)
        {
            DisableForSession("EMBEDDINGS_PROTOCOL_INVALID", $"malformed status response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DisableForSession("EMBEDDINGS_PROTOCOL_INVALID", ex.Message);
        }
    }

    private void DisableForSession(string code, string detail)
    {
        ProtocolFailureCode = code;
        _isAvailable = false;
        _degraded = true;
        Console.Error.WriteLine($"[scrinia:warn] {code}: {MultiMemoryStableDetail.SanitizeOrFallback(detail)}. Search falls back to lexical matching.");
    }

    internal static string[] BuildArguments(string dataDir, string modelsDir, Func<string, string?> getConfig)
    {
        var args = new List<string> { "--data-dir", dataDir, "--models-dir", modelsDir };
        foreach (var key in PluginConfigProtocol.Keys)
        {
            if (PluginConfigProtocol.IsSecret(key)) continue; // secrets go via the environment
            var val = getConfig(key);
            if (val is not null)
            {
                args.Add("--config");
                args.Add($"{key}={val}");
            }
        }
        return args.ToArray();
    }

    internal static Dictionary<string, string?> BuildSecretEnvironment(Func<string, string?> getConfig)
    {
        var env = new Dictionary<string, string?>();
        foreach (var key in PluginConfigProtocol.Keys)
        {
            if (!PluginConfigProtocol.IsSecret(key)) continue;
            var val = getConfig(key);
            if (val is not null)
                env[PluginConfigProtocol.EnvVarName(key)] = val;
        }
        return env;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = _exePath,
            Arguments = _arguments,
            // Secret config (API keys) is handed to the child via its environment, never argv.
            EnvironmentVariables = _environment,
            Name = "scrinia-plugin",
            StandardErrorLines = line => Console.Error.WriteLine(FormatPluginStderr(line)),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        });

        _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        _toolCaller = new McpClientToolCaller(_client);

        // Discover capabilities
        var tools = await _client.ListToolsAsync(cancellationToken: ct);
        var names = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        _hasSearch = names.Contains("search");
        _hasRemove = names.Contains("remove");
        _hasStatus = names.Contains("status");
        _hasIndex = names.Contains("index");

    }

    // ── ISearchScoreContributor ──────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        if (_degraded || !_hasSearch) return null;

        var candidatesByScope = candidates
            .GroupBy(candidate => candidate.Scope, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(candidate => ResolveCandidateName(candidate, store))
                    .Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        if (candidatesByScope.Count == 0) return null;

        var request = new EmbeddingsSearchRequest(query, candidatesByScope);
        var text = await CallToolWithRetryAsync("search", ToArguments(request), ct);

        if (text is null) return null;

        EmbeddingsSearchResponse? response = JsonSerializer.Deserialize(
            text, PluginClientJsonContext.Default.EmbeddingsSearchResponse);
        if (response is null || response.Error is not null || response.Scores is null) return null;

        var allowed = candidatesByScope
            .SelectMany(pair => pair.Value.Select(name => $"{pair.Key}|{name}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return response.Scores
            .Where(score => allowed.Contains(score.Key))
            .ToDictionary(score => score.Key, score => score.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveCandidateName(ScopedArtifact candidate, IMemoryStore store)
    {
        if (store is not null)
            return store.ResolveAddress(candidate.Entry.Address).Subject;

        return candidate.Entry.Address.ToString().TrimStart('/');
    }

    // ── IMemoryEventSink ─────────────────────────────────────────────────

    public async Task OnStoredAsync(MemoryAddress address, string[] content,
        IPluginStore store, CancellationToken ct)
    {
        try { await OnStoredCoreAsync(address, content, store, ct); }
        catch (Exception ex) { LogObserverFailure("store", ex); }
    }

    private async Task OnStoredCoreAsync(MemoryAddress address, string[] content,
        IPluginStore store, CancellationToken ct)
    {
        // Restored the early return in front of ParseQualifiedName: a plugin advertising only
        // `remove` has neither flag set, and must not pay for a parse (which throws on a
        // malformed name) on every store/append when it was never going to call out anyway.
        if (_degraded || !_hasIndex) return;

        var (scope, name) = store.ResolveAddress(address);

        var request = new EmbeddingsIndexRequest(scope, name, content);
        await CallToolWithRetryAsync("index", ToArguments(request), ct);
    }

    public async Task OnAppendedAsync(MemoryAddress address, string content,
        IPluginStore store, CancellationToken ct)
    {
        try { await OnAppendedCoreAsync(address, content, store, ct); }
        catch (Exception ex) { LogObserverFailure("append", ex); }
    }

    private async Task OnAppendedCoreAsync(MemoryAddress address, string content,
        IPluginStore store, CancellationToken ct)
    {
        // See OnStoredAsync — same early-return-before-parse fix, same reason.
        if (_degraded || !_hasIndex) return;

        var (scope, name) = store.ResolveAddress(address);

        string artifact = await store.ReadArtifactAsync(address, ct);
        if (string.IsNullOrWhiteSpace(artifact)) return;

        var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
        if (elements.Length == 0) return;

        var request = new EmbeddingsIndexRequest(scope, name, elements);
        await CallToolWithRetryAsync("index", ToArguments(request), ct);
    }

    public async Task OnForgottenAsync(MemoryAddress address, bool wasDeleted,
        IPluginStore store, CancellationToken ct)
    {
        try { await OnForgottenCoreAsync(address, wasDeleted, store, ct); }
        catch (Exception ex) { LogObserverFailure("forget", ex); }
    }

    private async Task OnForgottenCoreAsync(MemoryAddress address, bool wasDeleted,
        IPluginStore store, CancellationToken ct)
    {
        if (!wasDeleted || _degraded || !_hasRemove) return;

        var (scope, name) = store.ResolveAddress(address);
        await CallToolWithRetryAsync("remove", new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["name"] = name,
        }, ct);
    }

    // ── Communication ────────────────────────────────────────────────────

    private async Task<string?> CallToolWithRetryAsync(
        string toolName, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        if (_degraded || _toolCaller is null) return null;

        try
        {
            var text = await _toolCaller.CallToolAsync(toolName, arguments, ct);
            ThrowIfPluginReportedFailure(text, toolName);
            _failCount = 0; // success → reset counter
            return text;
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(FormatPluginWarning($"Plugin call '{toolName}' failed", ex));

            // Try reconnect + retry once
            if (!await TryReconnectAsync(ct)) return null;

            try
            {
                var text = await _toolCaller!.CallToolAsync(toolName, arguments, ct);
                ThrowIfPluginReportedFailure(text, toolName);
                _failCount = 0;
                return text;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task<bool> TryReconnectAsync(CancellationToken ct)
    {
        _failCount++;
        if (_failCount > MaxRestarts)
        {
            _degraded = true;
            Console.Error.WriteLine(
                $"[scrinia:warn] Embeddings plugin failed {MaxRestarts} times — " +
                "degrading to BM25-only search for this session.");
            return false;
        }

        Console.Error.WriteLine(
            $"[scrinia:info] Reconnecting embeddings plugin (attempt {_failCount}/{MaxRestarts})...");

        try
        {
            if (_client is not null)
                await _client.DisposeAsync();
            await ConnectAsync(ct);
            await NegotiateStatusAsync(ct);
            if (ProtocolFailureCode is not null) return false;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(FormatPluginWarning("Plugin reconnect failed", ex));
            return false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void ThrowIfPluginReportedFailure(string? text, string toolName)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException(
                $"Plugin tool '{toolName}' reported failure: {GetErrorMessage(root)}");

        if (root.TryGetProperty("error", out var error)
            && error.ValueKind != JsonValueKind.Null
            && (error.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(error.GetString())))
        {
            throw new InvalidOperationException(
                $"Plugin tool '{toolName}' reported failure: {GetErrorMessage(root)}");
        }
    }

    private static string GetErrorMessage(JsonElement root) =>
        root.TryGetProperty("error", out var error)
            ? error.ToString()
            : "ok=false";

    private static Dictionary<string, object?> ToArguments<T>(T request)
    {
        var metadata = PluginClientJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"No JSON metadata exists for {typeof(T).Name}.");
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(request, metadata));
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
    }

    private static string? GetTextContent(CallToolResult result)
    {
        foreach (var item in result.Content)
        {
            if (item is TextContentBlock textBlock)
                return textBlock.Text;
        }
        return null;
    }

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    private static bool GetBool(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.GetBoolean();

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetInt32() : 0;

    private static string FormatPluginWarning(string prefix, Exception exception)
    {
        string detail = MultiMemoryStableDetail.SanitizeOrFallback(exception.Message);
        return $"[scrinia:warn] {prefix}: {detail}";
    }

    private static string FormatPluginStderr(string? line) =>
        $"[scrinia:warn] plugin stderr: {MultiMemoryStableDetail.SanitizeOrFallback(line)}";

    private static string FormatPluginStatusValue(string? value) =>
        MultiMemoryStableDetail.SanitizeOrFallback(value);

    private static void LogObserverFailure(string operation, Exception exception) =>
        Console.Error.WriteLine(FormatPluginWarning($"Plugin {operation} observer failed", exception));

    // ── Disposal ─────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;

        // Disposal talks to the plugin child over stdio. If it already exited (crash, or a
        // shutdown race where it goes first), that write fails — a broken pipe is not a reason
        // to fault the caller, which is usually a process-exit path.
        try
        {
            await _client.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(FormatPluginWarning("Embeddings plugin did not shut down cleanly", ex));
        }
    }
}
