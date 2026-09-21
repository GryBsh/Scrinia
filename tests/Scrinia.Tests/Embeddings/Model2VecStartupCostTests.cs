using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Guards the eager-vs-cheap startup contract for the Model2Vec content gate.
///
/// <para><b>Why.</b> The startup path probes availability with
/// <c>Model2VecModelManager.IsModelAvailable</c> and then loads with <c>Model2VecProvider.Load</c>.
/// Both once routed through the same full-file SHA-256 over the ~22MB <c>model.safetensors</c>, so a
/// plain start hashed the body twice. The availability probe is only a "is there a well-formed model
/// here?" pre-check and must read the SafeTensors <i>header</i> — never the tensor body — while the
/// eager <c>Load</c> stays the full-file content gate that proves the on-disk asset matches its pinned
/// digest before anything embeds.</para>
///
/// <para><b>How this asserts it without the 22MB real model.</b> The full-file hash validates against
/// the model manager's pinned digest, so a stand-in model cannot pass <c>Load</c>. Instead this test
/// seeds a <i>size-valid</i> manifest (correct header + matching pinned digest for the resolver under
/// test) and observes the full-file-hash counter: <see cref="Model2VecModelManager.IsModelAvailable"/>
/// must record none, and the <c>Resolve</c> that <c>Load</c> delegates must record every asset once.</para>
/// </summary>
public sealed class Model2VecStartupCostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"m2v-cost-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IsModelAvailable_RecordsNoFullFileHash_WhileLoad_ValidatesEveryAsset()
    {
        (string modelSha, string vocabSha) = SeedSyntheticModel();
        IReadOnlyList<DownloadableAsset> assets = SyntheticAssets(modelSha, vocabSha);

        try
        {
            // Cheap pre-check (what IsModelAvailable runs): a size-valid manifest is available and the
            // ~22MB body is never hashed here.
            VerifiedAssetResolver.ResetFullContentValidationSpy();
            Model2VecModelManager.IsModelAvailable(_dir).Should().BeTrue(
                "the seeded manifest is well-formed and complete, so availability needs no full read");
            VerifiedAssetResolver.FullContentValidatedAssets.Should().BeEmpty(
                "the availability pre-check must read the header only — hashing the model here double-hashes it at startup");

            // Security gate (what Load runs): the full-file hash proves every asset matches its pinned
            // digest exactly once before the model is used.
            VerifiedAssetResolver.ResetFullContentValidationSpy();
            Model2VecAssetResolver.Resolve(_dir, assets).Should().NotBeNull();
            VerifiedAssetResolver.FullContentValidatedAssets
                .Should().Contain("model.safetensors").And.Contain("vocab.txt")
                .And.HaveCount(2, "the eager Load path must content-hash each asset once — the gate stays intact");
        }
        finally
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// A truncated manifest (declared size larger than the file) is rejected by the cheap probe, not
    /// silently accepted — availability fails fast on a corrupt body without a full read.
    /// </summary>
    [Fact]
    public void IsModelAvailable_RejectsTruncatedModel()
    {
        (string modelSha, string vocabSha) = SeedSyntheticModel();
        WriteTruncatedModel(modelSha);

        try
        {
            VerifiedAssetResolver.ResetFullContentValidationSpy();
            Model2VecModelManager.IsModelAvailable(_dir).Should().BeFalse(
                "a manifest whose declared size exceeds the file is not an available model");
            VerifiedAssetResolver.FullContentValidatedAssets.Should().BeEmpty(
                "the reject must come from the header/manifest check, not a full-file hash");
        }
        finally
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// A header-truncated manifest (declared header length exceeds the bytes actually present) makes
    /// <c>ReadHeader</c>'s <see cref="System.IO.Stream.ReadExactly(ReadOnlySpan{byte})"/> run past EOF and
    /// throw <see cref="EndOfStreamException"/> — a case the cheap probe's catch list used to miss, which
    /// would have escaped the startup probe instead of failing soft to BM25.
    /// </summary>
    [Fact]
    public void IsModelAvailable_DegradesToFalse_OnHeaderTruncatedModel()
    {
        SeedSyntheticModel();
        WriteHeaderTruncatedModel();

        try
        {
            VerifiedAssetResolver.ResetFullContentValidationSpy();
            Model2VecModelManager.IsModelAvailable(_dir).Should().BeFalse(
                "a header-truncated manifest must fail soft, not throw out of the startup probe");
            VerifiedAssetResolver.FullContentValidatedAssets.Should().BeEmpty(
                "the fail-soft must come from the header read, never a full-file hash");
        }
        finally
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private (string modelSha, string vocabSha) SeedSyntheticModel()
    {
        Directory.CreateDirectory(_dir);

        // 4 tokens x 2 dims x 4 bytes (F32) = 32 bytes of tensor body behind a valid manifest.
        const string headerJson =
            "{\"__metadata__\":{\"framework\":\"pt\"},\"embeddings\":{\"dtype\":\"F32\",\"shape\":[4,2],\"data_offsets\":[0,32]}}";
        byte[] header = Encoding.UTF8.GetBytes(headerJson);
        byte[] tensorData = new byte[32];
        byte[] modelBytes = BuildSafeTensors(header, tensorData);
        string modelSha = Digest(modelBytes);
        File.WriteAllBytes(Path.Combine(_dir, "model.safetensors"), modelBytes);

        string vocab = string.Join("\n", new[] { "pad", "unk", "cls", "sep" }) + "\n";
        byte[] vocabBytes = Encoding.UTF8.GetBytes(vocab);
        string vocabSha = Digest(vocabBytes);
        File.WriteAllText(Path.Combine(_dir, "vocab.txt"), vocab);

        return (modelSha, vocabSha);
    }

    private void WriteTruncatedModel(string modelSha)
    {
        // Same valid header, but drop 8 bytes of the tensor body so the manifest's declared size no
        // longer matches the on-disk length — the cheap probe must catch this without a full read.
        const string headerJson =
            "{\"__metadata__\":{\"framework\":\"pt\"},\"embeddings\":{\"dtype\":\"F32\",\"shape\":[4,2],\"data_offsets\":[0,32]}}";
        byte[] header = Encoding.UTF8.GetBytes(headerJson);
        byte[] tensorData = new byte[24]; // 8 bytes short of the 32 the manifest declares
        byte[] modelBytes = BuildSafeTensors(header, tensorData);
        File.WriteAllBytes(Path.Combine(_dir, "model.safetensors"), modelBytes);
        _ = modelSha;
    }

    private void WriteHeaderTruncatedModel()
    {
        // Same well-formed header, but the on-disk file only carries half of it: the 8-byte length
        // prefix still claims the full header length, so ReadHeader's ReadExactly runs past EOF and
        // throws EndOfStreamException. vocab.txt stays intact so the resolver resolves both paths and
        // the failure originates from the header read, not a missing asset.
        const string headerJson =
            "{\"__metadata__\":{\"framework\":\"pt\"},\"embeddings\":{\"dtype\":\"F32\",\"shape\":[4,2],\"data_offsets\":[0,32]}}";
        byte[] header = Encoding.UTF8.GetBytes(headerJson);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write((ulong)header.Length); // declares the full header follows
        writer.Write(header.AsSpan(0, header.Length / 2)); // but only half is written — header truncated
        writer.Flush();
        File.WriteAllBytes(Path.Combine(_dir, "model.safetensors"), ms.ToArray());
    }

    private static IReadOnlyList<DownloadableAsset> SyntheticAssets(string modelSha, string vocabSha) =>
    [
        new("test", "https://example.test/model", "model.safetensors", modelSha),
        new("test", "https://example.test/vocab", "vocab.txt", vocabSha),
    ];

    private static byte[] BuildSafeTensors(byte[] header, byte[] tensorData)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write((ulong)header.Length); // SafeTensors length-prefixed header
        writer.Write(header);
        writer.Write(tensorData);
        writer.Flush();
        return ms.ToArray();
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
