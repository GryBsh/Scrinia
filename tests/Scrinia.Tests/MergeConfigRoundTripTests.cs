using FluentAssertions;
using Scrinia.Commands;
using ReaderConfig = Scrinia.Merge.MergeConfig;

namespace Scrinia.Tests;

/// <summary>
/// The merge.config contract is declared twice — Scrinia's internal writer record and
/// Scrinia.Merge's public reader record — in two assemblies, each with its own camelCase policy
/// and nothing pinning them together. That divergence was a real shipped bug. MergeConfigTests
/// in Scrinia.Merge.Tests cannot catch it: it feeds MergeConfig.Load hand-built JSON, so it would
/// have passed under the bug. This test drives the REAL writer into the REAL reader.
///
/// <para>Known blind spot: the reader's <c>MergeConfig</c> has a fourth property,
/// <c>ResolverCommand</c> (<c>string?</c>, default <c>null</c>), that
/// <see cref="ScriniaCommands.SerializeMergeConfig"/> has no parameter for and therefore cannot
/// emit. These tests only round-trip <c>JaccardThreshold</c>, <c>Resolver</c>, and
/// <c>ConflictDir</c>. If writer-side support for <c>ResolverCommand</c> is ever added, it will
/// land untested unless these tests are extended to cover it.</para>
/// </summary>
public class MergeConfigRoundTripTests
{
    [Fact]
    public void RealWriterOutput_LoadsThroughRealReader_WithEveryFieldIntact()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-mergecfg-" + Guid.NewGuid().ToString("N"));
        string scriniaDir = Path.Combine(root, ".scrinia");
        Directory.CreateDirectory(scriniaDir);
        try
        {
            // Values deliberately differ from every default, so a reader that silently falls back
            // to its own defaults fails instead of coincidentally matching.
            string json = ScriniaCommands.SerializeMergeConfig(0.42, "ollama", "merge-conflicts");
            File.WriteAllText(Path.Combine(scriniaDir, "merge.config"), json + "\n");

            ReaderConfig loaded = ReaderConfig.Load(scriniaDir);

            loaded.JaccardThreshold.Should().Be(0.42);
            loaded.Resolver.Should().Be("ollama");
            loaded.ConflictDir.Should().Be("merge-conflicts");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The values `scri setup --multi-user` actually writes are ALSO every one of the reader's
    /// fallback defaults (JaccardThreshold=0.7, Resolver="none", ConflictDir="conflict"), so
    /// asserting the loaded values for that case would pass even if Load threw and returned
    /// Default. This asserts the writer's JSON property names instead — the actual contract, and
    /// the thing the camelCase bug broke. Both CliJsonContext (writer) and MergeConfigJsonContext
    /// (reader) declare JsonSourceGenerationOptions(PropertyNamingPolicy = CamelCase), so the two
    /// sides currently agree on camelCase property names.
    /// </summary>
    [Fact]
    public void RealWriterEmitsThePropertyNamesTheReaderBindsTo()
    {
        string json = ScriniaCommands.SerializeMergeConfig(0.7, "none", "conflict");

        json.Should().Contain("jaccardThreshold");
        json.Should().Contain("resolver");
        json.Should().Contain("conflictDir");
    }
}
