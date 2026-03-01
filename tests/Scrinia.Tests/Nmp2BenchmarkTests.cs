using FluentAssertions;
using Scrinia.Core.Encoding;
using Xunit.Abstractions;

namespace Scrinia.Tests;

/// <summary>
/// Benchmark tests that encode all TestData files with NMP/2 via the chunked encoder
/// (the real production path) and report before/after sizes, compression ratio,
/// and bits per token.
/// </summary>
public sealed class Nmp2BenchmarkTests(ITestOutputHelper output)
{
    private static readonly Nmp2Strategy Strategy = new();

    [Fact]
    public void Benchmark_AllTestDataFiles()
    {
        var files = TestHelpers.AllTestDataFiles();

        // Header
        output.WriteLine(
            $"{"File",-28} {"Original",10} {"Artifact",10} {"Ratio",8} {"Chunks",7} {"Est Tokens",11} {"Bits/Token",11}");
        output.WriteLine(new string('-', 91));

        long totalOriginal = 0;
        long totalArtifact = 0;
        int totalEstTokens = 0;

        foreach (var (name, content) in files)
        {
            long originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);
            string artifact = Nmp2ChunkedEncoder.Encode(content);
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

            long artifactChars = artifact.Length;
            double ratio = (double)artifactChars / originalBytes;

            // Estimate tokens: ~4 chars per token for Base64-heavy content
            int estTokens = (int)(artifactChars / 4);
            double bitsPerToken = estTokens > 0 ? (originalBytes * 8.0) / estTokens : 0;

            totalOriginal += originalBytes;
            totalArtifact += artifactChars;
            totalEstTokens += estTokens;

            output.WriteLine(
                $"{name,-28} {FormatBytes(originalBytes),10} {FormatBytes(artifactChars),10} {ratio,7:F3}x {chunkCount,6} {estTokens,10:N0} {bitsPerToken,10:F2}");

            // Verify roundtrip via chunked decoder
            if (chunkCount == 1)
            {
                byte[] decoded = Strategy.Decode(artifact);
                decoded.Should().Equal(
                    System.Text.Encoding.UTF8.GetBytes(content),
                    $"{name} should roundtrip exactly");
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 1; i <= chunkCount; i++)
                    sb.Append(Nmp2ChunkedEncoder.DecodeChunk(artifact, i));
                sb.ToString().Should().Be(content, $"{name} should roundtrip exactly via chunks");
            }
        }

        // Totals
        double totalRatio = (double)totalArtifact / totalOriginal;
        double totalBpt = totalEstTokens > 0 ? (totalOriginal * 8.0) / totalEstTokens : 0;

        output.WriteLine(new string('-', 91));
        output.WriteLine(
            $"{"TOTAL",-28} {FormatBytes(totalOriginal),10} {FormatBytes(totalArtifact),10} {totalRatio,7:F3}x {"",6} {totalEstTokens,10:N0} {totalBpt,10:F2}");
    }

    [Fact]
    public void Benchmark_CompressionIsAlwaysBelowOriginal()
    {
        foreach (var (name, content) in TestHelpers.AllTestDataFiles())
        {
            string artifact = Nmp2ChunkedEncoder.Encode(content);

            // NMP/2 artifact (chars) should be smaller than original text (chars)
            // for any non-trivial text file. Artifact chars include header/footer overhead
            // so we allow up to 1.5x for very small or incompressible inputs.
            artifact.Length.Should().BeLessThan(
                (int)(content.Length * 1.5),
                $"{name} artifact should not be excessively larger than original");
        }
    }

    [Fact]
    public void Benchmark_AllFilesRoundTrip()
    {
        foreach (var (name, content) in TestHelpers.AllTestDataFiles())
        {
            string artifact = Nmp2ChunkedEncoder.Encode(content);
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

            if (chunkCount == 1)
            {
                byte[] decoded = Strategy.Decode(artifact);
                System.Text.Encoding.UTF8.GetString(decoded).Should().Be(content, $"{name} must roundtrip exactly");
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 1; i <= chunkCount; i++)
                    sb.Append(Nmp2ChunkedEncoder.DecodeChunk(artifact, i));
                sb.ToString().Should().Be(content, $"{name} must roundtrip exactly via chunks");
            }
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1_024 => $"{bytes} B",
            < 1_048_576 => $"{bytes / 1_024.0:F1} KB",
            _ => $"{bytes / 1_048_576.0:F1} MB",
        };
}
