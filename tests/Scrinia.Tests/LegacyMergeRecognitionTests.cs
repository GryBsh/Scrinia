using System.Reflection;
using System.Text;
using FluentAssertions;
using Scrinia.Core.Migration;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// The migration-only recognition contract owned by Task 1. These tests pin the exact legacy
/// <c>.gitattributes</c> lines, the exact legacy <c>merge.config</c> bytes, the ordered
/// driver-unset argv, and prove the type is recognition-only: no process launch, no Git wrapper,
/// no file write, no merge algorithm, no classifier registration, no reconcile, no cleanup.
/// Nothing here mutates a file or launches a process — Phase 2 consumes these constants.
/// </summary>
public sealed class LegacyMergeRecognitionTests
{
    // Canonical .scrinia/merge.config: the default managed serialization of
    // MergeConfig(0.7, "conflict") plus one trailing LF. Verified byte-exact (62 bytes) against
    // the production source-generated context; the trailing LF is the "plus one LF" the spec names.
    private static readonly byte[] LegacyMergeConfig = Encoding.UTF8.GetBytes(
        "{\r\n  \"jaccardThreshold\": 0.7,\r\n  \"conflictDir\": \"conflict\"\r\n}\n");

    // Source-inspection guardrails. Each pattern is forbidden inside LegacyMergeRecognition.cs.
    // They are deliberately PascalCase / method-call shaped so they never collide with the
    // lowercase JSON keys ("jaccardThreshold"/"conflictDir") or the managed "merge=..." strings
    // that the recognition contract legitimately contains.
    private static readonly string[] ForbiddenInRecognitionType =
    [
        "Process",          // no process launch
        "RunGit",           // no Git wrapper
        "File.WriteAllText", "File.Create", "File.Delete", "File.Move", "File.Append",
        "File.Open", "FileStream", "StreamWriter", "Directory.CreateDirectory", // no file writes
        "new MergeConfig(", "MetaJsonMerger", "Nmp2ConflictHandler", "KeywordJaccard",
        "SidecarAttributeMerge", "SidecarKeywordMerge", "SidecarAdmission", // no merge algorithm/config types
        "JaccardThreshold", "ConflictDir", // no merge config properties
        "%O %A %B %P",      // no merge-driver invocation
        "Reconcile",        // no reconcile
        "ConfigureMultiUser", "ResolveMergeDriverPath", "SerializeMergeConfig", // no setup/driver/config emission
    ];

    // ---- exact managed .gitattributes lines ----

    [Fact]
    public void ManagedAttributeLines_AreExactlyTheTwoConstants()
    {
        LegacyMergeRecognition.ScriniaMetaAttributeLine.Should().Be("*.meta.json merge=scrinia-meta");
        LegacyMergeRecognition.ScriniaNmp2AttributeLine.Should().Be("*.nmp2 merge=scrinia-nmp2");
    }

    [Theory]
    [InlineData("*.meta.json merge=scrinia-meta")]
    [InlineData("*.nmp2 merge=scrinia-nmp2")]
    public void ExactConstants_AreRecognized(string managedLine)
    {
        LegacyMergeRecognition.IsRecognizedGitAttributesLine(managedLine).Should().BeTrue();
    }

    [Theory]
    [InlineData("*.meta.json merge=scrinia-meta ")]   // trailing space
    [InlineData(" *.meta.json merge=scrinia-meta")]    // leading space
    [InlineData("*.meta.json  merge=scrinia-meta")]    // double space
    [InlineData("*.json merge=scrinia-meta")]          // wrong glob
    [InlineData("*.meta.json merge=scrinia-nmp2")]     // wrong driver
    [InlineData("*.meta.json merge=scrinia-meta-x")]   // driver suffix drift
    [InlineData("*.nmp2 merge=scrinia-nmp2-extra")]    // trailing junk
    [InlineData("*.Meta.json merge=scrinia-meta")]     // extension case drift
    [InlineData("*.meta.json MERGE=scrinia-meta")]     // keyword case drift
    [InlineData("*.meta.json merge=Scrinia-Meta")]     // driver case drift
    public void NearMatchGitAttributes_AreRejected(string unmanagedLine)
    {
        LegacyMergeRecognition.IsRecognizedGitAttributesLine(unmanagedLine).Should().BeFalse();
    }

    [Fact]
    public void CrlfTerminatedManagedLine_IsRecognized_PerLineEndingRule()
    {
        // A CRLF file split on \n leaves a trailing \r on each line; the §8.3 line-ending rule
        // normalizes it away before the exact comparison.
        LegacyMergeRecognition.IsRecognizedGitAttributesLine("*.meta.json merge=scrinia-meta\r").Should().BeTrue();
        LegacyMergeRecognition.IsRecognizedGitAttributesLine("*.nmp2 merge=scrinia-nmp2\r").Should().BeTrue();
    }

    [Fact]
    public void NullGitAttributesLine_ReturnsFalse()
    {
        LegacyMergeRecognition.IsRecognizedGitAttributesLine(null).Should().BeFalse();
    }

    [Fact]
    public void EmbeddedNewline_IsNotASingleRecognizedLine()
    {
        string twoLinesJoined = "*.meta.json merge=scrinia-meta" + "\n" + "*.nmp2 merge=scrinia-nmp2";
        LegacyMergeRecognition.IsRecognizedGitAttributesLine(twoLinesJoined).Should().BeFalse();
    }

    // ---- exact legacy merge.config bytes ----

    [Fact]
    public void ExactLegacyMergeConfigBytes_AreRecognized()
    {
        LegacyMergeRecognition.IsExactLegacyMergeConfig(LegacyMergeConfig).Should().BeTrue();
    }

    [Fact]
    public void NonLegacyMergeConfig_AreRejected()
    {
        LegacyMergeRecognition.IsExactLegacyMergeConfig(Array.Empty<byte>()).Should().BeFalse();

        // Missing the single trailing LF.
        LegacyMergeRecognition.IsExactLegacyMergeConfig(LegacyMergeConfig[..^1]).Should().BeFalse();

        // The trailing LF present but with one extra byte after it.
        var withExtraByte = LegacyMergeConfig.Concat(new[] { (byte)' ' }).ToArray();
        LegacyMergeRecognition.IsExactLegacyMergeConfig(withExtraByte).Should().BeFalse();

        // Threshold drift (0.8 instead of 0.7).
        var wrongThreshold = Encoding.UTF8.GetBytes(
            "{\r\n  \"jaccardThreshold\": 0.8,\r\n  \"conflictDir\": \"conflict\"\r\n}\n");
        LegacyMergeRecognition.IsExactLegacyMergeConfig(wrongThreshold).Should().BeFalse();

        // Conflict-directory drift ("conflicts" instead of "conflict").
        var wrongConflictDir = Encoding.UTF8.GetBytes(
            "{\r\n  \"jaccardThreshold\": 0.7,\r\n  \"conflictDir\": \"conflicts\"\r\n}\n");
        LegacyMergeRecognition.IsExactLegacyMergeConfig(wrongConflictDir).Should().BeFalse();

        // Internal newlines use LF instead of the indented CRLF the production serializer emits.
        var lfInternalNewlines = Encoding.UTF8.GetBytes(
            "{\n  \"jaccardThreshold\": 0.7,\n  \"conflictDir\": \"conflict\"\n}\n");
        LegacyMergeRecognition.IsExactLegacyMergeConfig(lfInternalNewlines).Should().BeFalse();
    }

    // ---- ordered driver-unset argv ----

    [Fact]
    public void DriverUnsetArgv_AreTheTwoExactSequencesInClassifierOrder()
    {
        LegacyMergeRecognition.DriverUnsetArgv().Should().Equal(
            "git", "-C", "<workspace-root>", "config", "--local", "--unset-all", "merge.scrinia-meta.driver",
            "git", "-C", "<workspace-root>", "config", "--local", "--unset-all", "merge.scrinia-nmp2.driver");
    }

    [Fact]
    public void DriverUnsetArgv_MetaPrecedesNmp2()
    {
        var argv = LegacyMergeRecognition.DriverUnsetArgv().ToList();
        argv.Should().HaveCount(14);

        int metaIndex = argv.IndexOf("merge.scrinia-meta.driver");
        int nmp2Index = argv.IndexOf("merge.scrinia-nmp2.driver");

        metaIndex.Should().BeGreaterThanOrEqualTo(0);
        nmp2Index.Should().BeGreaterThanOrEqualTo(0);
        metaIndex.Should().BeLessThan(nmp2Index);
    }

    // ---- recognition-only contract ----

    [Fact]
    public void Type_IsPublicStaticAndExposesOnlyTheRecognitionContract()
    {
        var type = typeof(LegacyMergeRecognition);

        type.IsPublic.Should().BeTrue();
        // A C# static class is both sealed and abstract.
        type.IsSealed.Should().BeTrue();
        type.IsAbstract.Should().BeTrue();

        var methodNames = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        methodNames.Should().Equal(
            "DriverUnsetArgv", "IsExactLegacyMergeConfig", "IsRecognizedGitAttributesLine");
    }

    [Fact]
    public void Source_ContainsNoProcessWriteGitOrMergeAlgorithm()
    {
        string source = File.ReadAllText(
            RepoPaths.Combine("src", "Scrinia.Core", "Migration", "LegacyMergeRecognition.cs"));

        foreach (string forbidden in ForbiddenInRecognitionType)
        {
            source.Should().NotContain(
                forbidden,
                $"LegacyMergeRecognition.cs must not contain '{forbidden}' — it is recognition-only.");
        }
    }
}
