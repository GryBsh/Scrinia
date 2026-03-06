using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

public class SafeTensorsReaderTests
{
    /// <summary>Creates a synthetic SafeTensors file with a single F32 tensor.</summary>
    private static MemoryStream CreateSyntheticSafeTensors(string name, int rows, int cols, float fillValue)
    {
        // Build header JSON
        int totalFloats = rows * cols;
        long dataBytes = totalFloats * 4L;
        string headerJson = $"{{\"{name}\":{{\"dtype\":\"F32\",\"shape\":[{rows},{cols}],\"data_offsets\":[0,{dataBytes}]}}}}";
        byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(headerJson);
        long headerLen = headerBytes.Length;

        var ms = new MemoryStream();
        // 8-byte LE header length
        ms.Write(BitConverter.GetBytes(headerLen));
        // JSON header
        ms.Write(headerBytes);
        // Float data
        for (int i = 0; i < totalFloats; i++)
        {
            ms.Write(BitConverter.GetBytes(fillValue));
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void ReadHeader_ParsesTensorMetadata()
    {
        using var ms = CreateSyntheticSafeTensors("embeddings", 10, 4, 1.0f);

        var header = SafeTensorsReader.ReadHeader(ms);

        header.Should().ContainKey("embeddings");
        var meta = header["embeddings"];
        meta.Dtype.Should().Be("F32");
        meta.Shape.Should().BeEquivalentTo(new long[] { 10, 4 });
    }

    [Fact]
    public void ReadFloatTensor_ExtractsCorrectValues()
    {
        int rows = 3, cols = 2;
        float fillValue = 0.5f;
        using var ms = CreateSyntheticSafeTensors("test", rows, cols, fillValue);

        long dataStart = SafeTensorsReader.GetDataStart(ms);
        ms.Position = 0;
        var header = SafeTensorsReader.ReadHeader(ms);
        var meta = header["test"];

        float[] data = SafeTensorsReader.ReadFloatTensor(ms, dataStart, meta);

        data.Should().HaveCount(rows * cols);
        data.Should().AllSatisfy(f => f.Should().Be(fillValue));
    }

    [Fact]
    public void GetDataStart_CalculatesCorrectOffset()
    {
        using var ms = CreateSyntheticSafeTensors("x", 1, 1, 0f);

        long dataStart = SafeTensorsReader.GetDataStart(ms);

        // Should be 8 (header len field) + actual header length
        ms.Position = 0;
        Span<byte> buf = stackalloc byte[8];
        ms.ReadExactly(buf);
        long headerLen = BitConverter.ToInt64(buf);

        dataStart.Should().Be(8 + headerLen);
    }
}
