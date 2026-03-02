using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scrinia.Core;
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
    private McpClient? _client;
    private string _exePath = "";
    private string[] _arguments = [];
    private int _failCount;
    private const int MaxRestarts = 3;
    private bool _degraded;

    // Capability flags discovered via ListToolsAsync
    private bool _hasSearch, _hasUpsert, _hasRemove, _hasStatus;

    public bool HasSearchCapability => _hasSearch && !_degraded;
    public bool HasEventSinkCapability => (_hasUpsert || _hasRemove) && !_degraded;

    private static readonly string[] ConfigKeys =
    [
        "Scrinia:Embeddings:Provider",
        "Scrinia:Embeddings:Hardware",
        "Scrinia:Embeddings:SemanticWeight",
        "Scrinia:Embeddings:OllamaBaseUrl",
        "Scrinia:Embeddings:OllamaModel",
        "Scrinia:Embeddings:OpenAiApiKey",
        "Scrinia:Embeddings:OpenAiModel",
        "Scrinia:Embeddings:OpenAiBaseUrl",
    ];

    /// <summary>
    /// Starts the plugin process via MCP stdio transport. Verifies it's alive with a status call.
    /// </summary>
    public async Task StartAsync(string exePath, string dataDir, string modelsDir,
        Func<string, string?> getConfig, CancellationToken ct)
    {
        _exePath = exePath;
        _arguments = BuildArguments(dataDir, modelsDir, getConfig);
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
                    Console.Error.WriteLine(
                        $"[scrinia:info] Embeddings plugin ready " +
                        $"(provider={GetString(root, "provider")}, hardware={GetString(root, "hardware")}, " +
                        $"available={GetBool(root, "available")}, dims={GetInt(root, "dimensions")}, " +
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

    private static string[] BuildArguments(string dataDir, string modelsDir, Func<string, string?> getConfig)
    {
        var args = new List<string> { "--data-dir", dataDir, "--models-dir", modelsDir };
        foreach (var key in ConfigKeys)
        {
            var val = getConfig(key);
            if (val is not null)
            {
                args.Add("--config");
                args.Add($"{key}={val}");
            }
        }
        return args.ToArray();
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = _exePath,
            Arguments = _arguments,
            Name = "scrinia-plugin",
            StandardErrorLines = line => Console.Error.WriteLine(line),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        });

        _client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        // Discover capabilities
        var tools = await _client.ListToolsAsync(cancellationToken: ct);
        var names = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        _hasSearch = names.Contains("search");
        _hasUpsert = names.Contains("upsert");
        _hasRemove = names.Contains("remove");
        _hasStatus = names.Contains("status");
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
        IMemoryStore store, CancellationToken ct)
    {
        if (_degraded || !_hasUpsert) return;

        var (scope, name) = store.ParseQualifiedName(qualifiedName);
        string joined = string.Concat(content);
        if (string.IsNullOrWhiteSpace(joined)) return;

        await CallToolWithRetryAsync("upsert", new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["name"] = name,
            ["text"] = joined,
        }, ct);

        // Also embed individual chunks if multi-chunk
        if (content.Length > 1)
        {
            for (int i = 0; i < content.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(content[i])) continue;
                await CallToolWithRetryAsync("upsert", new Dictionary<string, object?>
                {
                    ["scope"] = scope,
                    ["name"] = name,
                    ["text"] = content[i],
                    ["chunkIndex"] = i + 1,
                }, ct);
            }
        }
    }

    public async Task OnAppendedAsync(string qualifiedName, string content,
        IMemoryStore store, CancellationToken ct)
    {
        if (_degraded || !_hasUpsert) return;

        var (scope, name) = store.ParseQualifiedName(qualifiedName);
        if (string.IsNullOrWhiteSpace(content)) return;

        await CallToolWithRetryAsync("upsert", new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["name"] = name,
            ["text"] = content,
        }, ct);
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted,
        IMemoryStore store, CancellationToken ct)
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
        if (_degraded || _client is null) return null;

        try
        {
            var result = await _client.CallToolAsync(toolName, arguments, cancellationToken: ct);
            _failCount = 0; // success → reset counter
            return GetTextContent(result);
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
                var result = await _client!.CallToolAsync(toolName, arguments, cancellationToken: ct);
                _failCount = 0;
                return GetTextContent(result);
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
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
