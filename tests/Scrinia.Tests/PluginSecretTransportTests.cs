using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public class PluginSecretTransportTests
{
    private const string SecretKey = "Scrinia:Embeddings:OpenAiApiKey";
    private const string SecretVal = "sk-super-secret-value";

    private static Func<string, string?> Config() => key => key switch
    {
        SecretKey => SecretVal,
        "Scrinia:Embeddings:Provider" => "openai",
        _ => null,
    };

    [Fact]
    public void IsSecret_TrueForApiKeys_FalseForOthers()
    {
        PluginConfigProtocol.IsSecret(SecretKey).Should().BeTrue();
        PluginConfigProtocol.IsSecret("Scrinia:Embeddings:VoyageAiApiKey").Should().BeTrue();
        PluginConfigProtocol.IsSecret("Scrinia:Embeddings:Provider").Should().BeFalse();
        PluginConfigProtocol.IsSecret("Scrinia:Embeddings:OllamaBaseUrl").Should().BeFalse();
    }

    [Fact]
    public void EnvVarName_IsDeterministicAndSanitized()
    {
        PluginConfigProtocol.EnvVarName(SecretKey)
            .Should().Be("SCRINIA_PLUGIN_CFG__SCRINIA_EMBEDDINGS_OPENAIAPIKEY");
    }

    [Fact]
    public void BuildArguments_OmitsSecretsButKeepsNonSecrets()
    {
        var args = McpPluginHost.BuildArguments("/data", "/models", Config());

        // Non-secret config is still present on the command line.
        args.Should().ContainInConsecutiveOrder("--config", "Scrinia:Embeddings:Provider=openai");
        // The secret value and its config pair never appear in argv.
        args.Should().NotContain(a => a.Contains(SecretVal));
        args.Should().NotContain(a => a.Contains(SecretKey));
    }

    [Fact]
    public void BuildSecretEnvironment_CarriesSecretUnderMappedName()
    {
        var env = McpPluginHost.BuildSecretEnvironment(Config());

        env.Should().ContainKey(PluginConfigProtocol.EnvVarName(SecretKey))
            .WhoseValue.Should().Be(SecretVal);
        // Non-secret keys are not placed in the environment.
        env.Should().NotContainKey(PluginConfigProtocol.EnvVarName("Scrinia:Embeddings:Provider"));
    }

    [Fact]
    public void ApplySecretEnvironment_RoundTripsFromBuiltEnvironment()
    {
        var env = McpPluginHost.BuildSecretEnvironment(Config());
        var configValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        PluginConfigProtocol.ApplySecretEnvironment(
            configValues, name => env.TryGetValue(name, out var v) ? v : null);

        configValues.Should().ContainKey(SecretKey).WhoseValue.Should().Be(SecretVal);
    }
}
