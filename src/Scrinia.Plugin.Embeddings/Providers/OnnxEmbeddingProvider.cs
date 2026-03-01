using Microsoft.Extensions.Logging;
using Scrinia.Plugin.Embeddings.Onnx;

namespace Scrinia.Plugin.Embeddings.Providers;

/// <summary>
/// Local ONNX embedding provider using all-MiniLM-L6-v2 (384 dimensions).
/// Auto-downloads model from HuggingFace on first use.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider
{
    private readonly string _modelDir;
    private readonly HardwareAcceleration _hardware;
    private readonly ILogger _logger;
    private OnnxInferenceSession? _session;
    private bool _initFailed;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public bool IsAvailable => _session is not null && !_initFailed;
    public int Dimensions => 384; // all-MiniLM-L6-v2

    public OnnxEmbeddingProvider(string modelDir, HardwareAcceleration hardware, ILogger logger)
    {
        _modelDir = modelDir;
        _hardware = hardware;
        _logger = logger;
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        if (session is null) return null;

        try
        {
            return session.Embed(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ONNX embedding failed for text");
            return null;
        }
    }

    public async Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        if (session is null) return null;

        try
        {
            return session.EmbedBatch(texts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ONNX batch embedding failed");
            return null;
        }
    }

    private async Task<OnnxInferenceSession?> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is not null) return _session;
        if (_initFailed) return null;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_session is not null) return _session;
            if (_initFailed) return null;

            // Download model if needed
            await ModelManager.EnsureModelAsync(_modelDir, _logger, ct);

            _session = OnnxInferenceSession.Create(_modelDir, _hardware, _logger);
            _logger.LogInformation("ONNX embedding session initialized ({Hardware})", _hardware);
            return _session;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize ONNX embedding session");
            _initFailed = true;
            return null;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }
}
