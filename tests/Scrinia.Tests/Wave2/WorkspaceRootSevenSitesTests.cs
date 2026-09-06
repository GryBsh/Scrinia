namespace Scrinia.Tests.Wave2;
public class WorkspaceRootSevenSitesTests
{
    static string R(string p){ string d=AppContext.BaseDirectory; for(int i=0;i<6;i++){ var c=Path.GetFullPath(Path.Combine(d,p)); if(File.Exists(c)) return c; d=Path.GetFullPath(Path.Combine(d,"..")); } return p; }
    [Fact] public void WorkspaceRoot_IsSingleAuthority()
    {
        var src = File.ReadAllText(R("src/Scrinia.Core/FileMemoryStore.cs"));
        Assert.Contains("WorkspaceRoot", src);
        var mcpCore = File.ReadAllText(R("src/Scrinia.Mcp/MemoryTools.Core.cs"));
        Assert.Contains("WorkspaceRoot", mcpCore);
        Assert.DoesNotContain("GetWorkspaceRoot(", mcpCore);
    }
    [Theory]
    [InlineData("src/Scrinia.Mcp/MemoryTools.Core.cs")]
    [InlineData("src/Scrinia.Mcp/MemoryTools.Bundling.cs")]
    [InlineData("src/Scrinia.Mcp/MemoryTools.Lifecycle.cs")]
    public void Sites_UseWorkspaceRoot(string file){ Assert.Contains("WorkspaceRoot", File.ReadAllText(R(file))); }
}
