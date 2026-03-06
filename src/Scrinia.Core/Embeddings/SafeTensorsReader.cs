using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Reads SafeTensors binary format: [8B header_len LE u64][JSON header][raw tensor data].
/// Only supports float32 (F32) tensors.
/// </summary>
internal static class SafeTensorsReader
{
    /// <summary>Reads the JSON header and returns tensor metadata keyed by name.</summary>
    public static Dictionary<string, TensorMeta> ReadHeader(Stream stream)
    {
        Span<byte> lenBuf = stackalloc byte[8];
        stream.ReadExactly(lenBuf);
        long headerLen = BitConverter.ToInt64(lenBuf);

        byte[] headerBytes = new byte[headerLen];
        stream.ReadExactly(headerBytes);

        var raw = JsonSerializer.Deserialize(headerBytes, SafeTensorsJsonContext.Default.DictionaryStringJsonElement)
            ?? throw new FormatException("Invalid SafeTensors header.");

        var result = new Dictionary<string, TensorMeta>(StringComparer.Ordinal);
        foreach (var (name, element) in raw)
        {
            if (name == "__metadata__") continue;

            var dtype = element.GetProperty("dtype").GetString()!;
            var shape = element.GetProperty("shape").EnumerateArray()
                .Select(e => e.GetInt64()).ToArray();
            var offsets = element.GetProperty("data_offsets").EnumerateArray()
                .Select(e => e.GetInt64()).ToArray();

            result[name] = new TensorMeta(dtype, shape, offsets[0], offsets[1]);
        }

        return result;
    }

    /// <summary>
    /// Reads a tensor as float32 from the data section of a SafeTensors file.
    /// Supports F32 (native) and F16 (converted to F32). Stream position is set internally.
    /// </summary>
    public static float[] ReadFloatTensor(Stream stream, long dataStart, TensorMeta meta)
    {
        if (meta.Dtype.Equals("F16", StringComparison.OrdinalIgnoreCase))
            return ReadF16Tensor(stream, dataStart, meta);

        if (!meta.Dtype.Equals("F32", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Only F32 and F16 tensors are supported, got {meta.Dtype}.");

        long byteLen = meta.DataEnd - meta.DataStart;
        int floatCount = (int)(byteLen / 4);
        var result = new float[floatCount];

        stream.Position = dataStart + meta.DataStart;
        byte[] buffer = new byte[byteLen];
        stream.ReadExactly(buffer);
        Buffer.BlockCopy(buffer, 0, result, 0, buffer.Length);

        return result;
    }

    private static float[] ReadF16Tensor(Stream stream, long dataStart, TensorMeta meta)
    {
        long byteLen = meta.DataEnd - meta.DataStart;
        int halfCount = (int)(byteLen / 2);
        var result = new float[halfCount];

        stream.Position = dataStart + meta.DataStart;
        byte[] buffer = new byte[byteLen];
        stream.ReadExactly(buffer);

        for (int i = 0; i < halfCount; i++)
        {
            ushort bits = BitConverter.ToUInt16(buffer, i * 2);
            result[i] = HalfToFloat(bits);
        }

        return result;
    }

    private static float HalfToFloat(ushort half)
    {
        return (float)BitConverter.UInt16BitsToHalf(half);
    }

    /// <summary>Computes the start offset of the data section (8 + headerLen).</summary>
    public static long GetDataStart(Stream stream)
    {
        stream.Position = 0;
        Span<byte> lenBuf = stackalloc byte[8];
        stream.ReadExactly(lenBuf);
        long headerLen = BitConverter.ToInt64(lenBuf);
        return 8 + headerLen;
    }

    internal sealed record TensorMeta(string Dtype, long[] Shape, long DataStart, long DataEnd);
}

[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal partial class SafeTensorsJsonContext : JsonSerializerContext;
