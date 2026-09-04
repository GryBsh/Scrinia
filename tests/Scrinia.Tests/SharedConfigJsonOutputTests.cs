using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class SharedConfigJsonOutputTests
{
    [Fact]
    public void SuccessEnvelopeEmitsStableFieldsAndStaticArgv()
    {
        ConfigurationJsonResult result = ConfigurationJsonResult.Success(
            "tracking status", changed: false, tracking: "local", state: "local",
            gitCommands: [["git", "-C", "C:\\repo", "status", "--short"]]);

        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(result, HomeConfigJsonContext.Default.ConfigurationJsonResult));
        JsonElement root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("gitIndexStatus").GetString().Should().Be("UNVERIFIED");
        root.GetProperty("gitCommands")[0][0].GetString().Should().Be("git");
        root.GetProperty("gitCommands")[0][2].GetString().Should().Be("C:\\repo");
        root.TryGetProperty("errorCode", out _).Should().BeFalse();
        root.TryGetProperty("base", out _).Should().BeFalse();
    }

    [Fact]
    public void ListEnvelopeEmitsBasesButNotMutationManifest()
    {
        ConfigurationJsonResult result = ConfigurationJsonResult.Success(
            "shared base list", changed: false,
            bases: [new SharedBaseJsonItem("alpha", "C:\\alpha", "valid")]);

        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(result, HomeConfigJsonContext.Default.ConfigurationJsonResult));
        document.RootElement.GetProperty("bases")[0].GetProperty("name").GetString().Should().Be("alpha");
        document.RootElement.TryGetProperty("manifest", out _).Should().BeFalse();
    }
}
