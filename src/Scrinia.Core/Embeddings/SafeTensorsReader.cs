using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Reads SafeTensors binary format: [8B header_len LE u64][JSON header][raw tensor data].
/// Only supports float32 (F32) tensors.
/// </summary>
internal static class SafeTensorsReader
{
    private const long MaxHeaderBytes = 100_000_000; // 100 MB
    private const long MaxTensorBytes = 1_000_000_000; // 1 GB

    /// <summary>Reads the JSON header and returns tensor metadata keyed by name.</summary>
    public static Dictionary<string, TensorMeta> ReadHeader(Stream stream)
    {
        Span<byte> lenBuf = stackalloc byte[8];
        stream.ReadExactly(lenBuf);
        long headerLen = BitConverter.ToInt64(lenBuf);

        if (headerLen < 0 || headerLen > MaxHeaderBytes)
            throw new FormatException($"SafeTensors header length out of bounds: {headerLen}");

        byte[] headerBytes = new byte[headerLen];
        stream.ReadExactly(headerBytes);

        var raw = JsonSerializer.Deserialize(headerBytes, SafeTensorsJsonContext.Default.DictionaryStringJsonElement)
            ?? throw new FormatException("Invalid SafeTensors header.");

        var result = new Dictionary<string, TensorMeta>(StringComparer.Ordinal);
        foreach (var (name, element) in raw)
        {
            if (name == "__metadata__") continue;

            result[name] = ReadTensorMeta(name, element);
        }

        return result;
    }

    /// <summary>
    /// Parses and validates one tensor's dtype, shape and data_offsets from an untrusted
    /// SafeTensors header. Fails closed with <see cref="FormatException"/> on any structural
    /// problem — a non-object element, a non-string/empty dtype, a non-numeric shape element, or a
    /// data_offsets array that is not exactly two non-negative numbers in order — so a corrupt or
    /// malicious manifest is rejected here as a catchable FormatException rather than surfacing an
    /// NRE or InvalidCastException that the model-availability gate does not treat as unavailable.
    /// </summary>
    private static TensorMeta ReadTensorMeta(string name, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"SafeTensors tensor '{name}' is not a JSON object.");

        JsonElement dtypeElement = RequireObjectProperty(element, "dtype", name);
        if (dtypeElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dtypeElement.GetString()))
            throw new FormatException($"SafeTensors tensor '{name}' has a non-string or empty dtype.");
        string dtype = dtypeElement.GetString()!;

        JsonElement shapeElement = RequireObjectProperty(element, "shape", name);
        if (shapeElement.ValueKind != JsonValueKind.Array)
            throw new FormatException($"SafeTensors tensor '{name}' shape is not a JSON array.");
        long[] shape = shapeElement.EnumerateArray()
            .Select(e => ReadNumber(e, "shape", name)).ToArray();

        JsonElement offsetsElement = RequireObjectProperty(element, "data_offsets", name);
        if (offsetsElement.ValueKind != JsonValueKind.Array)
            throw new FormatException($"SafeTensors tensor '{name}' data_offsets is not a JSON array.");
        List<long> offsets = new(2);
        foreach (JsonElement offset in offsetsElement.EnumerateArray())
            offsets.Add(ReadNumber(offset, "data_offsets", name));
        if (offsets.Count != 2)
            throw new FormatException($"SafeTensors tensor '{name}' data_offsets must contain exactly two numbers, found {offsets.Count}.");

        long dataStart = offsets[0];
        long dataEnd = offsets[1];
        if (dataStart < 0 || dataEnd < 0 || dataStart > dataEnd)
            throw new FormatException($"SafeTensors tensor '{name}' has invalid data_offsets [{dataStart}, {dataEnd}].");

        return new TensorMeta(dtype, shape, dataStart, dataEnd);
    }

    private static JsonElement RequireObjectProperty(JsonElement element, string property, string tensorName) =>
        element.TryGetProperty(property, out JsonElement value)
            ? value
            : throw new FormatException($"SafeTensors tensor '{tensorName}' is missing the '{property}' property.");

    private static long ReadNumber(JsonElement element, string property, string tensorName) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long value)
            ? value
            : throw new FormatException($"SafeTensors tensor '{tensorName}' property '{property}' is not an integer.");

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
        if (byteLen < 0 || byteLen > MaxTensorBytes)
            throw new FormatException($"Tensor byte length out of bounds: {byteLen}");
        int floatCount = checked((int)(byteLen / 4));
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
        if (byteLen < 0 || byteLen > MaxTensorBytes)
            throw new FormatException($"Tensor byte length out of bounds: {byteLen}");
        int halfCount = checked((int)(byteLen / 2));
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

    /// <summary>
    /// Reads the SafeTensors header and returns the total on-disk length the manifest declares: the
    /// 8-byte length prefix plus the header plus the largest tensor <c>data_offsets</c> end. Throws
    /// <see cref="FormatException"/> when the header is not a well-formed manifest, so a corrupt
    /// manifest is caught here by reading only the header — never the tensor body.
    /// </summary>
    public static long VerifyManifestLength(Stream stream)
    {
        stream.Position = 0;
        Dictionary<string, TensorMeta> header = ReadHeader(stream);

        long maxDataEnd = 0;
        foreach (TensorMeta meta in header.Values)
            maxDataEnd = Math.Max(maxDataEnd, meta.DataEnd);

        // ReadHeader consumed the 8-byte prefix and the header, so the stream now sits at the data start.
        return stream.Position + maxDataEnd;
    }

    /// <summary>Computes the start offset of the data section (8 + headerLen).</summary>
    public static long GetDataStart(Stream stream)
    {
        stream.Position = 0;
        Span<byte> lenBuf = stackalloc byte[8];
        stream.ReadExactly(lenBuf);
        long headerLen = BitConverter.ToInt64(lenBuf);
        if (headerLen < 0 || headerLen > MaxHeaderBytes)
            throw new FormatException($"SafeTensors header length out of bounds: {headerLen}");
        return 8 + headerLen;
    }

    internal sealed record TensorMeta(string Dtype, long[] Shape, long DataStart, long DataEnd);
}

[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal partial class SafeTensorsJsonContext : JsonSerializerContext;
