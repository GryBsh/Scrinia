using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public class SharedAvailabilityConsolidationTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData(SharedBaseAvailability.Available, "available")]
    [InlineData(SharedBaseAvailability.Unavailable, "unavailable")]
    [InlineData(SharedBaseAvailability.Stale, "stale")]
    [InlineData(SharedBaseAvailability.Conflicted, "conflicted")]
    public void Serializes_To_Published_Wire_Spellings(SharedBaseAvailability value, string expected)
    {
        var json = JsonSerializer.Serialize(value, _options);
        Assert.Equal(expected, json.Trim('"'));
    }
}
