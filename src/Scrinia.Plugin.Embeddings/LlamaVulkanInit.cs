using LLama.Native;
using Microsoft.Extensions.Logging;

namespace Scrinia.Plugin.Embeddings;

/// <summary>
/// One-time process-global LLamaSharp initialization for the Vulkan embeddings provider.
/// <see cref="NativeLibraryConfig"/> must be configured BEFORE any native P/Invoke call
/// and once resolved cannot be changed for the lifetime of the process.
/// </summary>
internal static class LlamaVulkanInit
{
    private static int _initialized; // 0 = not yet, 1 = done. Interlocked-gated.

    /// <summary>
    /// Configures LLamaSharp for Vulkan (with CPU auto-fallback), routes native logging
    /// through <paramref name="logger"/>, and finalizes backend resolution by issuing a
    /// trivial native call. Idempotent — only the first invocation has effect; subsequent
    /// calls are no-ops, which is safe because the underlying native state is global.
    /// </summary>
    public static void EnsureConfigured(ILogger logger)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;

        NativeLibraryConfig.LLama
            .WithCuda(false)
            .WithVulkan()
            .WithSearchDirectory(AppContext.BaseDirectory)
            .WithAutoFallback();

        NativeLogConfig.llama_log_set((level, message) =>
        {
            var logLevel = level switch
            {
                LLamaLogLevel.Error => LogLevel.Error,
                LLamaLogLevel.Warning => LogLevel.Warning,
                LLamaLogLevel.Info => LogLevel.Information,
                _ => LogLevel.Debug,
            };
            logger.Log(logLevel, "LLamaNative: {Message}", message);
        });

        // Force backend resolution before the first model-load attempt.
        NativeApi.llama_empty_call();
    }
}
