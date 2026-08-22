using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
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
    private bool _hasSearch, _hasUpsert, _hasRemove, _hasStatus, _hasIndex;
    // Set to true only when the plugin's status reports available=true (i.e. a real provider, not NullEmbeddingProvider)
    private bool _isAvailable;

    /// <summary>Production construction path — connects lazily via <see cref="StartAsync"/>.</summary>
    public McpPluginHost() { }

    /// <summary>Test-only: supply a tool caller and capability set directly, skipping StartAsync.</summary>
    internal McpPluginHost(IPluginToolCaller toolCaller, IEnumerable<string> toolNames)
    {
        _toolCaller = toolCaller;
        var names = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
        _hasSearch = names.Contains("search");
        _hasUpsert = names.Contains("upsert");
        _hasRemove = names.Contains("remove");
        _hasStatus = names.Contains("status");
        _hasIndex = names.Contains("index");
        _isAvailable = true;
    }

    public bool HasSearchCapability => _hasSearch && _isAvailable && !_degraded;
    public bool HasEventSinkCapability => (_hasIndex || _hasUpsert || _hasRemove) && _isAvailable && !_degraded;

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

        // Verify the plugin is alive
        if (_hasStatus)
        {
            try
            {
                var result = await _client!.CallToolAsync("status", cancellationToken: ct);
                var text = GetTextContent(result);
                if (text is not null)
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    _isAvailable = GetBool(root, "available");
                    Console.Error.WriteLine(
                        $"[scrinia:info] Embeddings plugin ready " +
                        $"(provider={GetString(root, "provider")}, hardware={GetString(root, "hardware")}, " +
                        $"available={_isAvailable}, dims={GetInt(root, "dimensions")}, " +
                        $"vectors={GetInt(root, "vectorCount")})");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[scrinia:warn] Plugin status check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
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
            StandardErrorLines = line => Console.Error.WriteLine(line),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        });

        _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        _toolCaller = new McpClientToolCaller(_client);

        // Discover capabilities
        var tools = await _client.ListToolsAsync(cancellationToken: ct);
        var names = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        _hasSearch = names.Contains("search");
        _hasUpsert = names.Contains("upsert");
        _hasRemove = names.Contains("remove");
        _hasStatus = names.Contains("status");
        _hasIndex = names.Contains("index");

        // Capability fact, logged once per connection rather than once per memory — see
        // OnStoredAsync/OnAppendedAsync, which used to log this on every legacy-fallback call and
        // would spam one identical line per memory during a bulk reindex.
        if (!_hasIndex && _hasUpsert)
        {
            Console.Error.WriteLine(
                "[scrinia:warn] Plugin offers 'upsert' but not 'index'; indexing without windowing. "
                + "Long memories will be less reachable by semantic search.");
        }
    }

    // ── ISearchScoreContributor ──────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        if (_degraded || !_hasSearch) return null;

        var scopes = candidates.Select(c => c.Scope).Distinct().ToArray();
        if (scopes.Length == 0) return null;

        var text = await CallToolWithRetryAsync("search", new Dictionary<string, object?>
        {
            ["query"] = query,
            ["scopes"] = scopes,
        }, ct);

        if (text is null) return null;

        return JsonSerializer.Deserialize(text, PluginClientJsonContext.Default.DictionaryStringDouble);
    }

    // ── IMemoryEventSink ─────────────────────────────────────────────────

    public async Task OnStoredAsync(string qualifiedName, string[] content,
        IPluginStore store, CancellationToken ct)
    {
        // Restored the early return in front of ParseQualifiedName: a plugin advertising only
        // `remove` has neither flag set, and must not pay for a parse (which throws on a
        // malformed name) on every store/append when it was never going to call out anyway.
        if (_degraded || (!_hasIndex && !_hasUpsert)) return;

        var (scope, name) = store.ParseQualifiedName(qualifiedName);

        if (_hasIndex)
        {
            // Preferred: hand the plugin the whole content array and let MemoryVectorIndexer run
            // inside the plugin process. The chunking policy lives in one place instead of being
            // restated by every caller that speaks this protocol (finding H3).
            await CallToolWithRetryAsync("index", new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["name"] = name,
                ["content"] = content,
            }, ct);
            return;
        }

        // Legacy fallback for plugins that predate the `index` tool. This path does not window, so
        // a long memory becomes one mean-pooled vector — the exact H3 regression. Kept so a
        // third-party plugin keeps being indexed rather than silently stopping. The capability
        // warning for this fallback is logged once per connection, in ConnectAsync — not here,
        // where it would repeat once per memory during a bulk reindex.
        string joined = string.Concat(content);
        if (string.IsNullOrWhiteSpace(joined)) return;

        await CallToolWithRetryAsync("upsert", new Dictionary<string, object?>
        {
            ["scope"] = scope, ["name"] = name, ["text"] = joined,
        }, ct);

        if (content.Length > 1)
        {
            for (int i = 0; i < content.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(content[i])) continue;
                await CallToolWithRetryAsync("upsert", new Dictionary<string, object?>
                {
                    ["scope"] = scope, ["name"] = name, ["text"] = content[i], ["chunkIndex"] = i + 1,
                }, ct);
            }
        }
    }

    public async Task OnAppendedAsync(string qualifiedName, string content,
        IPluginStore store, CancellationToken ct)
    {
        // See OnStoredAsync — same early-return-before-parse fix, same reason.
        if (_degraded || (!_hasIndex && !_hasUpsert)) return;

        var (scope, name) = store.ParseQualifiedName(qualifiedName);

        if (_hasIndex)
        {
            // An append mutates the memory, and the plugin-side indexer replaces a memory's
            // vectors atomically — sending only the appended fragment would destroy every vector
            // for the rest of the memory. Re-read the current artifact and send the full
            // post-append content array, same reasoning as CoreEmbeddingEventHandler.
            string artifact = await store.ReadArtifactAsync(name, scope, ct);
            if (string.IsNullOrWhiteSpace(artifact)) return;

            var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
            if (elements.Length == 0) return;

            await CallToolWithRetryAsync("index", new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["name"] = name,
                ["content"] = elements,
            }, ct);
            return;
        }

        // Legacy fallback: the old protocol has no way to express a replace, so this keeps
        // today's fragment-only behaviour. The top guard already guarantees _hasUpsert here,
        // since _hasIndex is false and at least one of the two must be true to reach this line.
        if (string.IsNullOrWhiteSpace(content)) return;

        await CallToolWithRetryAsync("upsert", new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["name"] = name,
            ["text"] = content,
        }, ct);
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted,
        IPluginStore store, CancellationToken ct)
    {
        if (!wasDeleted || _degraded || !_hasRemove) return;

        var (scope, name) = store.ParseQualifiedName(qualifiedName);
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
            Console.Error.WriteLine(
                $"[scrinia:warn] Plugin call '{toolName}' failed: {ex.GetType().Name}: {ex.Message}");

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
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Plugin reconnect failed: {ex.GetType().Name}: {ex.Message}");
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
            Console.Error.WriteLine(
                $"[scrinia:warn] Embeddings plugin did not shut down cleanly: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
