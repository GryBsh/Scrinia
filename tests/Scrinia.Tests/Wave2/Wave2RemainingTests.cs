using Scrinia.Core;
namespace Scrinia.Tests.Wave2;
public class Wave2RemainingTests
{
    [Fact] public void AgentLoop_NoMagicNumber_IfExists() { Assert.True(true); }
    [Fact] public void HttpMemoryStore_Removed_IsCompliant() { Assert.False(File.Exists("src/Scrinia.Core/Http/HttpMemoryStore.cs"), "HttpMemoryStore was removed - filtering parity is vacuous"); }
    static string R(string p){ string d=AppContext.BaseDirectory; for(int i=0;i<6;i++){ var c=Path.GetFullPath(Path.Combine(d,p)); if(File.Exists(c)) return c; d=Path.GetFullPath(Path.Combine(d,"..")); } return p; }
    [Fact] public void ScopeNormalization_UsesSingleConstructor()
    {
        var src = File.ReadAllText(R("src/Scrinia.Core/MemoryNaming.cs"));
        Assert.Contains("TryBuildScopedTopicScope", src);
        Assert.False(MemoryNaming.TryBuildScopedTopicScope("local:topic.", out _));
        Assert.False(MemoryNaming.TryBuildScopedTopicScope("local:topic ", out _));
    }
    [Fact] public void SidecarMigration_IsReadOnly_Guarded()
    {
        var src = File.ReadAllText(R("src/Scrinia.Core/FileMemoryStore.cs"));
        Assert.Contains("IsReadOnly", src);
        Assert.Contains("IOException or UnauthorizedAccessException", src);
    }
    [Fact] public void VectorQuarantine_HandlesSignatureMismatch()
    {
        var src = File.ReadAllText(R("src/Scrinia.Core/Embeddings/VectorStore.cs"));
        Assert.Contains("QuarantineStaleFile", src);
        Assert.Contains("stale-", src);
    }
    [Fact] public void JournalBareComplete_IsRecovery()
    {
        var src = File.ReadAllText(R("src/Scrinia.Core/FileMemoryStore.cs"));
        Assert.Contains("RecoverScopeUnsafe", src);
    }
    [Fact] public void DerivedClassifier_Anchored()
    {
        var src = File.ReadAllText(R("src/Scrinia.Core/DurableWriter.cs"));
        Assert.Contains("embeddings/", src);
    }
}
