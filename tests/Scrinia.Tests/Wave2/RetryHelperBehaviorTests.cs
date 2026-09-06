using Scrinia.Core.Resilience;
namespace Scrinia.Tests.Wave2;
public class RetryHelperBehaviorTests
{
    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(1000)]
    public void Truncate_At500(int len)
    {
        string body = new string('x', len);
        string truncated = HttpErrorBody.Truncate(body);
        if (len > 500) { Assert.EndsWith("...", truncated); Assert.Equal(503, truncated.Length); }
        else Assert.DoesNotContain("...", truncated);
    }
    [Fact] public void RetryPolicy_IsDedupedViaSingleHelper()
    {
        string R2(string p){ string d=AppContext.BaseDirectory; for(int i=0;i<6;i++){ var c=Path.GetFullPath(Path.Combine(d,p)); if(File.Exists(c)) return c; d=Path.GetFullPath(Path.Combine(d,"..")); } return p; }
        var src = File.ReadAllText(R2("src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs"));
        Assert.Contains("RetryPolicy.ExecuteAsync", src);
        Assert.Contains("HttpErrorBody.Truncate", src);
    }
}
