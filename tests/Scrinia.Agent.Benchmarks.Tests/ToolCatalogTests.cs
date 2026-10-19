using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class ToolCatalogTests
{
    private static Tool Make(string name, string schema) => new()
    {
        Name = name, Description = "description", InputSchema = JsonDocument.Parse(schema).RootElement.Clone()
    };

    [Fact]
    public void Tool_and_object_property_order_do_not_change_catalog_identity()
    {
        Tool[] first = [Make("a", """{"type":"object","properties":{"x":{"type":"string"}}}"""), Make("b", """{"type":"object"}""")];
        Tool[] second = [Make("b", """{"type":"object"}"""), Make("a", """{"properties":{"x":{"type":"string"}},"type":"object"}""")];
        Assert.Equal(ToolCatalog.ComputeProtocolHash(first), ToolCatalog.ComputeProtocolHash(second));
        second[1].Description = "changed";
        Assert.NotEqual(ToolCatalog.ComputeProtocolHash(first), ToolCatalog.ComputeProtocolHash(second));
    }

    [Fact]
    public void Output_schema_and_behavior_annotations_are_part_of_identity()
    {
        var tool = Make("a", """{"type":"object"}""");
        var original = ToolCatalog.ComputeProtocolHash([tool]);
        tool.OutputSchema = JsonDocument.Parse("""{"type":"object","properties":{"result":{"type":"string"}}}""").RootElement.Clone();
        var withOutput = ToolCatalog.ComputeProtocolHash([tool]);
        Assert.NotEqual(original, withOutput);
        tool.Annotations = new ToolAnnotations { ReadOnlyHint = true };
        Assert.NotEqual(withOutput, ToolCatalog.ComputeProtocolHash([tool]));
    }
}
