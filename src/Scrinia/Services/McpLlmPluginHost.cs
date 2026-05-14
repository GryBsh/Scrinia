using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scrinia.Core.Llm;

namespace Scrinia.Services;

/// <summary>
/// Manages a child-process LLM plugin (<c>scri-plugin-llm</c>) via MCP over stdio
/// and adapts its generic <c>complete</c> tool to <see cref="IBackgroundLlm"/>.
/// Mirrors <see cref="McpPluginHost"/> for the embeddings plugin: 3 reconnect attempts,
/// then permanent degradation for the rest of the session.
///
/// <para>Prompts and post-processing live in <see cref="LlmPrompts"/> (Scrinia.Core)
/// so adding a Tier 2 task does not require rebuilding the plugin process.</para>
/// </summary>
internal sealed class McpLlmPluginHost : IBackgroundLlm, IAsyncDisposable
{
    private McpClient? _client;
    private string _exePath = "";
    private string[] _arguments = [];
    private int _failCount;
    private const int MaxRestarts = 3;
    private bool _degraded;

    private bool _hasComplete, _hasStatus;
    private bool _isAvailable;
    private string _modelArch = "unknown";
    private string _hardware = "none";

    public bool HasCompleteCapability => _hasComplete && _isAvailable && !_degraded;
    public string ModelArchitecture => _modelArch;
    public string Hardware => _hardware;

    private static readonly string[] ConfigKeys =
    [
        "Scrinia:Llm:LocalModelFile",
        "Scrinia:Llm:LocalContextSize",
    ];

    /// <summary>
    /// Starts the plugin subprocess and verifies it has a loaded model via <c>status</c>.
    /// </summary>
    public async Task StartAsync(string exePath, string dataDir, string modelsDir,
        Func<string, string?> getConfig, CancellationToken ct)
    {
        _exePath = exePath;
        _arguments = BuildArguments(dataDir, modelsDir, getConfig);
        await ConnectAsync(ct);

        if (_hasStatus)
        {
            try
            {
                var result = await _client!.CallToolAsync("status", cancellationToken: ct);
                string? text = GetTextContent(result);
                if (text is not null)
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    _isAvailable = GetBool(root, "available");
                    _modelArch = GetString(root, "modelArch");
                    _hardware = GetString(root, "hardware");
                    string lastError = GetString(root, "lastError");

                    Console.Error.WriteLine(
                        $"[scrinia:info] LLM plugin ready " +
                        $"(provider={GetString(root, "provider")}, available={_isAvailable}, " +
                        $"arch={_modelArch}, hardware={_hardware})");
                    if (!string.IsNullOrWhiteSpace(lastError))
                        Console.Error.WriteLine($"[scrinia:warn] LLM plugin reports lastError: {lastError}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[scrinia:warn] LLM plugin status check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string[] BuildArguments(string dataDir, string modelsDir, Func<string, string?> getConfig)
    {
        var args = new List<string> { "--data-dir", dataDir, "--models-dir", modelsDir };
        foreach (var key in ConfigKeys)
        {
            string? val = getConfig(key);
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
            Name = "scrinia-plugin-llm",
            StandardErrorLines = line => Console.Error.WriteLine(line),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        });

        _client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        var tools = await _client.ListToolsAsync(cancellationToken: ct);
        var names = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        _hasComplete = names.Contains("complete");
        _hasStatus = names.Contains("status");
    }

    // ── IBackgroundLlm ───────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (_degraded || !_hasComplete) return false;
        if (_isAvailable) return true;

        // Status flag was false at startup — re-check in case the user dropped a model in.
        if (!_hasStatus) return false;
        try
        {
            var result = await _client!.CallToolAsync("status", cancellationToken: ct);
            string? text = GetTextContent(result);
            if (text is null) return false;
            using var doc = JsonDocument.Parse(text);
            _isAvailable = GetBool(doc.RootElement, "available");
            return _isAvailable;
        }
        catch
        {
            return false;
        }
    }

    public Task<string?> GenerateDescriptionAsync(string content, CancellationToken ct) =>
        CompleteAsync(LlmPrompts.DescriptionSystem, LlmPrompts.DescriptionUser(content),
            maxTokens: 80, temperature: DefaultTemperature, ct);

    public Task<string?> SummarizeAsync(string text, CancellationToken ct) =>
        CompleteAsync(LlmPrompts.SummarySystem, LlmPrompts.SummaryUser(text),
            maxTokens: 320, temperature: DefaultTemperature, ct);

    public async Task<string[]?> ExtractFactsAsync(string content, CancellationToken ct)
    {
        string? raw = await CompleteAsync(LlmPrompts.FactsSystem, LlmPrompts.FactsUser(content),
            maxTokens: 400, temperature: DefaultTemperature, ct);
        if (raw is null) return null;
        string[] parsed = LlmPrompts.ParseFacts(raw);
        return parsed.Length == 0 ? null : parsed;
    }

    // Tier 2 favours deterministic output (descriptions and fact lists need reproducibility
    // across runs) so the default is low. Per-task overrides could be added later if needed.
    private const double DefaultTemperature = 0.3;

    private async Task<string?> CompleteAsync(string system, string user, int maxTokens,
        double temperature, CancellationToken ct)
    {
        if (_degraded || !_hasComplete) return null;

        string? raw = await CallToolWithRetryAsync("complete", new Dictionary<string, object?>
        {
            ["system"] = system,
            ["user"] = user,
            ["maxTokens"] = maxTokens,
            ["temperature"] = temperature,
        }, ct);

        if (raw is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("text", out var textEl))
            {
                string? text = textEl.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            if (root.TryGetProperty("error", out var errEl))
            {
                string err = errEl.GetString() ?? "unknown";
                Console.Error.WriteLine($"[scrinia:warn] LLM plugin complete returned error: {err}");
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Communication ────────────────────────────────────────────────────

    private async Task<string?> CallToolWithRetryAsync(
        string toolName, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        if (_degraded || _client is null) return null;

        try
        {
            var result = await _client.CallToolAsync(toolName, arguments, cancellationToken: ct);
            _failCount = 0;
            return GetTextContent(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] LLM plugin call '{toolName}' failed: {ex.GetType().Name}: {ex.Message}");

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
                $"[scrinia:warn] LLM plugin failed {MaxRestarts} times — " +
                "Tier 2 consolidation disabled for this session.");
            return false;
        }

        Console.Error.WriteLine(
            $"[scrinia:info] Reconnecting LLM plugin (attempt {_failCount}/{MaxRestarts})...");

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
                $"[scrinia:warn] LLM plugin reconnect failed: {ex.GetType().Name}: {ex.Message}");
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
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
