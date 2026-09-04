using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Commands;
using System.Text;

namespace Scrinia.Tests;

public sealed class CliErrorPrecedenceTests
{
    [Fact]
    public void EveryDeclaredFailureHasStableCodeAndExitClass()
    {
        foreach (ConfigurationErrorCode error in Enum.GetValues<ConfigurationErrorCode>())
        {
            if (error == ConfigurationErrorCode.None) continue;
            ConfigResults.Code(error).Should().MatchRegex("^[A-Z][A-Z0-9_]+$");
            ConfigResults.ExitClassFor(error).Should().BeOneOf(
                ConfigurationExitClass.InvalidConfiguration,
                ConfigurationExitClass.PolicyFailure,
                ConfigurationExitClass.WriteFailure);
        }
    }

    [Fact]
    public void UnsafePathAndInvalidNameHaveDistinctDeclaredCommandClasses()
    {
        ConfigResults.Code(ConfigurationErrorCode.SharedBaseUnsafePath).Should().Be("SHARED_BASE_UNSAFE_PATH");
        ConfigResults.ExitClassFor(ConfigurationErrorCode.SharedBaseUnsafePath).Should().Be(ConfigurationExitClass.PolicyFailure);
        ConfigResults.Code(ConfigurationErrorCode.CliInvalidArgument).Should().Be("CLI_INVALID_ARGUMENT");
        ConfigResults.ExitClassFor(ConfigurationErrorCode.CliInvalidArgument).Should().Be(ConfigurationExitClass.InvalidConfiguration);
    }

    [Fact]
    public void CommandProducerUsesUsageClassForInvalidCanonicalName()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-task8-" + Guid.NewGuid().ToString("N"));
        try
        {
            SharedBaseResult result = new SharedBaseRegistry(root, root).Add("Not Canonical", root);
            result.ErrorCode.Should().Be(ConfigurationErrorCode.CliInvalidArgument);
            result.ExitClass.Should().Be(ConfigurationExitClass.InvalidConfiguration);
            SharedBaseResult operationResult = new SharedBaseOperations(
                new SharedBaseRegistry(root, root), root, root).Add("Not Canonical", root);
            operationResult.ErrorCode.Should().Be(ConfigurationErrorCode.CliInvalidArgument);
            operationResult.ExitClass.Should().Be(ConfigurationExitClass.InvalidConfiguration);
            ConfigurationJsonResult.Failure("shared base add", result.ErrorCode).ErrorCode
                .Should().Be("CLI_INVALID_ARGUMENT");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnableProducerUsesUsageClassForInvalidCanonicalName()
    {
        string home = Path.Combine(Path.GetTempPath(), "scrinia-task8-enable-" + Guid.NewGuid().ToString("N"));
        try
        {
            SharedBaseResult result = new SharedBaseRegistry(home, home).Enable("Not Canonical");

            result.ErrorCode.Should().Be(ConfigurationErrorCode.CliInvalidArgument);
            result.ExitClass.Should().Be(ConfigurationExitClass.InvalidConfiguration);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void OperationsProducerUsesPolicyClassForMalformedRoot()
    {
        string home = Path.Combine(Path.GetTempPath(), "scrinia-task8-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            SharedBaseResult result = new SharedBaseOperations(
                new SharedBaseRegistry(home, home), home, home).Add("team", "relative-root");

            result.ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseUnsafePath);
            result.ExitClass.Should().Be(ConfigurationExitClass.PolicyFailure);
            result.ExitClass.Should().Be(ConfigResults.ExitClassFor(result.ErrorCode));
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void BothSharedCliAdaptersEmitUnsafePathWithPolicyExitClass()
    {
        var originalOutput = Console.Out;
        try
        {
            using var output = new StringWriter(new StringBuilder());
            Console.SetOut(output);
            var result = new SharedBaseResult(ConfigurationErrorCode.SharedBaseUnsafePath,
                ConfigurationExitClass.PolicyFailure);

            SharedBaseCommands.Render("shared base add", result, json: true).Should().Be(3);
            using (JsonDocument document = JsonDocument.Parse(output.ToString()))
                document.RootElement.GetProperty("errorCode").GetString().Should().Be("SHARED_BASE_UNSAFE_PATH");

            output.GetStringBuilder().Clear();
            SharedEnableCommands.Render("shared enable", result, json: true).Should().Be(3);
            using (JsonDocument document = JsonDocument.Parse(output.ToString()))
                document.RootElement.GetProperty("errorCode").GetString().Should().Be("SHARED_BASE_UNSAFE_PATH");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public void FailureEnvelopeHasOneDeclaredCodeAndNeverLeaksRejectedBase()
    {
        ConfigurationJsonResult result = ConfigurationJsonResult.Failure(
            "shared base add", ConfigurationErrorCode.SharedBaseAlreadyExists);

        result.ErrorCode.Should().Be("SHARED_BASE_ALREADY_EXISTS");
        result.Base.Should().BeNull();
        result.GitIndexStatus.Should().Be("UNVERIFIED");
        result.GitCommands.Should().BeEmpty();
        JsonDocument.Parse(JsonSerializer.Serialize(result, HomeConfigJsonContext.Default.ConfigurationJsonResult))
            .RootElement.GetProperty("errorCode").GetString().Should().Be("SHARED_BASE_ALREADY_EXISTS");
    }
}
