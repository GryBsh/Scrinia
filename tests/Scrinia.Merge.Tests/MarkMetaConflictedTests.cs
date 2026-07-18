using System.Text.Json;
using Xunit;

namespace Scrinia.Merge.Tests;

public class MarkMetaConflictedTests
{
    [Fact]
    public void NestedTrailingObject_StaysValidJson_WithConflictedTrue()
    {
        string meta = "{\n  \"name\": \"x\",\n  \"nested\": { \"a\": 1 }\n}";
        string result = Nmp2ConflictHandler.InsertConflictedFlag(meta);
        using var doc = JsonDocument.Parse(result); // BUG: current TrimEnd('}') makes this throw
        Assert.True(doc.RootElement.GetProperty("conflicted").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("nested").GetProperty("a").GetInt32());
    }
}
