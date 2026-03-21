namespace Scrinia.Core.Embeddings;

/// <summary>Shared vector math utilities for embedding providers.</summary>
public static class VectorMath
{
    /// <summary>L2-normalizes a vector in place.</summary>
    public static void L2Normalize(float[] v)
    {
        float norm = 0;
        foreach (float f in v) norm += f * f;
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }
}
