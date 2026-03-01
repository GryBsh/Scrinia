using Microsoft.Extensions.Logging;

namespace Scrinia.Plugin.Embeddings.Onnx;

/// <summary>Hardware acceleration type for ONNX inference.</summary>
public enum HardwareAcceleration
{
    Cpu,
    DirectMl,
    Cuda
}

/// <summary>
/// Resolves the preferred hardware acceleration for ONNX inference.
/// Does NOT probe OnnxRuntime APIs (those calls are deferred to session creation)
/// to avoid JIT failures in trimmed single-file hosts.
/// </summary>
public static class HardwareDetector
{
    public static HardwareAcceleration Detect(string preference, ILogger? logger)
    {
        var pref = preference?.Trim().ToLowerInvariant() ?? "auto";

        var result = pref switch
        {
            "cpu" => HardwareAcceleration.Cpu,
            "cuda" => HardwareAcceleration.Cuda,
            "directml" => HardwareAcceleration.DirectMl,
            _ => OperatingSystem.IsWindows()
                ? HardwareAcceleration.DirectMl
                : HardwareAcceleration.Cpu,
        };

        logger?.LogInformation("Hardware acceleration: {Accel}", result);
        return result;
    }
}
