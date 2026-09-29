using Scrinia.Core;
namespace Scrinia.Tests.Wave3;
public class Wave3HygieneTests
{
    [Fact] public void TextPreview_IsPure() { var a = TextPreview.GenerateContentPreview("hello world this is long content for truncation test", 20); var b = TextPreview.GenerateContentPreview("hello world this is long content for truncation test", 20); Assert.Equal(a,b); Assert.EndsWith("...", a); }
    [Fact] public void ContentPreview_Exists() { Assert.NotNull(typeof(ContentPreview)); }
    [Fact] public void ChecksWorkflow_Exists() { var root = FindRepoRoot(); Assert.True(File.Exists(Path.Combine(root, ".github/workflows/checks.yml"))); }
    [Fact] public void MultiUserSetup_UsesOrdinaryGitWorkflow() { var root = FindRepoRoot(); var t = File.ReadAllText(Path.Combine(root, "docs/multi-user-setup.md")); Assert.Contains("Merge like any other file.", t); Assert.DoesNotContain("merge=scrinia-meta", t); Assert.DoesNotContain("merge=scrinia-nmp2", t); }
    [Fact] public void Compact_NoOp_ReturnsMutationNone() { var root = FindRepoRoot(); var s = File.ReadAllText(Path.Combine(root, "src/Scrinia.Mcp/MemoryTools.Lifecycle.cs")); Assert.Contains("mutation: none", s); }
    private static string FindRepoRoot() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !File.Exists(Path.Combine(d.FullName, "Scrinia.sln"))) d = d.Parent; return d!.FullName; }
    [Fact] public void VersionSingleSource() { var root = FindRepoRoot(); Assert.True(File.Exists(Path.Combine(root, "Directory.Build.props"))); }
    [Fact] public void SkillCount_IsSeven() { var root = FindRepoRoot(); Assert.Equal(7, Directory.GetFiles(Path.Combine(root, "src/Scrinia.Mcp/skills"),"*.md").Length); }
}
