using System.Diagnostics;
using System.Text.Json;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Services;

/// <summary>
/// Manages a child-process embeddings plugin.
/// Communicates via newline-delimited JSON on stdin/stdout.
/// Implements <see cref="ISearchScoreContributor"/> and <see cref="IMemoryEventSink"/>
/// so the host can wire it into the standard AsyncLocal contexts.
///
/// If the plugin process crashes, it restarts automatically up to <see cref="MaxRestarts"/>
/// times. After that, it degrades permanently to BM25-only search for the session.
/// </summary>
internal sealed class PluginProcessHost : ISearchScoreContributor, IMemoryEventSink, IAsyncDisposable
{
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string _exePath = "";
    private string _arguments = "";
    private int _failCount;
    private const int MaxRestarts = 3;
    private bool _degraded;

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
    /// Starts the plugin process. Verifies it's alive with a status request.
    /// </summary>
    /// <param name="exePath">Path to the plugin executable.</param>
    /// <param name="dataDir">Workspace-local directory for vector data (e.g. .scrinia/).</param>
    /// <param name="modelsDir">Global directory for shared model caches (e.g. %LOCALAPPDATA%/scrinia/plugins).</param>
    /// <param name="getConfig">Reads config values by key.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(string exePath, string dataDir, string modelsDir,
        Func<string, string?> getConfig, CancellationToken ct)
    {
        _exePath = exePath;

        var argParts = new List<string> { $"--data-dir \"{dataDir}\"", $"--models-dir \"{modelsDir}\"" };
        foreach (var key in ConfigKeys)
        {
            var val = getConfig(key);
            if (val is not null)
                argParts.Add($"--config \"{key}={val}\"");
        }
        _arguments = string.Join(' ', argParts);

        await StartProcessAsync(ct);

        // Verify the plugin is alive
        var status = await SendAsync(new HostRequest { Method = "status" }, ct);
        if (status is not null && status.Status is { } s)
        {
            Console.Error.WriteLine(
                $"[scrinia:info] Embeddings plugin ready " +
                $"(provider={s.Provider}, hardware={s.Hardware}, " +
                $"available={s.Available}, dims={s.Dimensions}, vectors={s.VectorCount})");
        }
    }

    private Task StartProcessAsync(CancellationToken ct)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = _arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        _process.Start();
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;

        // Forward plugin stderr to our stderr (diagnostic logging)
        _ = Task.Run(() => ForwardStderr(_process.StandardError), ct);

        return Task.CompletedTask;
    }

    private static async Task ForwardStderr(StreamReader stderr)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync()) is not null)
                Console.Error.WriteLine(line);
        }
        catch
        {
            // Plugin exited — expected
        }
    }

    // ── ISearchScoreContributor ──────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        if (_degraded) return null;

        var scopes = candidates.Select(c => c.Scope).Distinct().ToArray();
        if (scopes.Length == 0) return null;

        var resp = await SendAsync(
            new HostRequest { Method = "search", Query = query, Scopes = scopes }, ct);

        return resp?.Scores;
    }

    // ── IMemoryEventSink ─────────────────────────────────────────────────

    public async Task OnStoredAsync(string qualifiedName, string[] content,
        IMemoryStore store, CancellationToken ct)
    {
        if (_degraded) return;

        var (scope, name) = store.ParseQualifiedName(qualifiedName);
        string joined = string.Concat(content);
        if (string.IsNullOrWhiteSpace(joined)) return;

        await SendAsync(new HostRequest
        {
            Method = "upsert", Scope = scope, Name = name, Text = joined
        }, ct);

        // Also embed individual chunks if multi-chunk
        if (content.Length > 1)
        {
            for (int i = 0; i < content.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(content[i])) continue;
                await SendAsync(new HostRequest
                {
                    Method = "upsert", Scope = scope, Name = name,
                    ChunkIndex = i + 1, Text = content[i]
                }, ct);
            }
        }
    }

    public async Task OnAppendedAsync(string qualifiedName, string content,
        IMemoryStore store, CancellationToken ct)
    {
        if (_degraded) return;

        var (scope, name) = store.ParseQualifiedName(qualifiedName);
        if (string.IsNullOrWhiteSpace(content)) return;

        await SendAsync(new HostRequest
        {
            Method = "upsert", Scope = scope, Name = name, Text = content
        }, ct);
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted,
        IMemoryStore store, CancellationToken ct)
    {
        if (!wasDeleted || _degraded) return;

        var (scope, name) = store.ParseQualifiedName(qualifiedName);
        await SendAsync(new HostRequest
        {
            Method = "remove", Scope = scope, Name = name
        }, ct);
    }

    // ── Communication ────────────────────────────────────────────────────

    private async Task<HostResponse?> SendAsync(HostRequest request, CancellationToken ct)
    {
        if (_degraded) return null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_process is null or { HasExited: true })
            {
                if (!await TryRestartAsync(ct))
                    return null;
            }

            var json = JsonSerializer.Serialize(request, HostJsonContext.Default.HostRequest);
            await _stdin!.WriteLineAsync(json.AsMemory(), ct);
            await _stdin.FlushAsync(ct);

            var line = await _stdout!.ReadLineAsync(ct);
            if (line is null)
            {
                // Process died mid-request — try restart + retry once
                if (!await TryRestartAsync(ct))
                    return null;

                await _stdin!.WriteLineAsync(json.AsMemory(), ct);
                await _stdin.FlushAsync(ct);
                line = await _stdout!.ReadLineAsync(ct);
                if (line is null) return null;
            }

            _failCount = 0; // success → reset counter
            return JsonSerializer.Deserialize(line, HostJsonContext.Default.HostResponse);
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Plugin communication error: {ex.GetType().Name}: {ex.Message}");
            if (!await TryRestartAsync(ct)) return null;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> TryRestartAsync(CancellationToken ct)
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
            $"[scrinia:info] Restarting embeddings plugin (attempt {_failCount}/{MaxRestarts})...");

        try
        {
            try { _process?.Kill(); } catch { /* already dead */ }
            _process?.Dispose();
            await StartProcessAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Plugin restart failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── Disposal ─────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    var json = JsonSerializer.Serialize(
                        new HostRequest { Method = "shutdown" },
                        HostJsonContext.Default.HostRequest);
                    await _stdin!.WriteLineAsync(json);
                    await _stdin.FlushAsync();
                    _process.WaitForExit(3000);

                    if (!_process.HasExited)
                        _process.Kill();
                }
            }
            catch { /* best effort — process may not have started or already exited */ }

            _process.Dispose();
        }

        _lock.Dispose();
    }
}
