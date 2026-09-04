using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class CliErrorPrecedenceTests
{
    [Theory]
    [InlineData(ConfigurationErrorCode.WorkspaceConfigMalformed, "WORKSPACE_CONFIG_MALFORMED", 2)]
    [InlineData(ConfigurationErrorCode.UserConfigMalformed, "USER_CONFIG_MALFORMED", 2)]
    [InlineData(ConfigurationErrorCode.UserConfigSchemaUnsupported, "USER_CONFIG_SCHEMA_UNSUPPORTED", 2)]
    [InlineData(ConfigurationErrorCode.SharedBaseAlreadyExists, "SHARED_BASE_ALREADY_EXISTS", 3)]
    [InlineData(ConfigurationErrorCode.SharedBaseNotFound, "SHARED_BASE_NOT_FOUND", 3)]
    [InlineData(ConfigurationErrorCode.SharedBaseRootCollision, "SHARED_BASE_ROOT_COLLISION", 3)]
    [InlineData(ConfigurationErrorCode.SharedBaseUnsafePath, "SHARED_BASE_UNSAFE_PATH", 3)]
    [InlineData(ConfigurationErrorCode.SharedBaseNotGitRoot, "SHARED_BASE_NOT_GIT_ROOT", 3)]
    [InlineData(ConfigurationErrorCode.SharedTrackingPolicyDrift, "SHARED_TRACKING_POLICY_DRIFT", 3)]
    [InlineData(ConfigurationErrorCode.LegacyMigrationRequired, "LEGACY_MIGRATION_REQUIRED", 3)]
    [InlineData(ConfigurationErrorCode.ManagedFileUnsafe, "MANAGED_FILE_UNSAFE", 3)]
    [InlineData(ConfigurationErrorCode.TrackingPolicyRequired, "TRACKING_POLICY_REQUIRED", 3)]
    [InlineData(ConfigurationErrorCode.TrackingPolicyInvalid, "TRACKING_POLICY_INVALID", 3)]
    [InlineData(ConfigurationErrorCode.MigrationManualCleanupRequired, "MIGRATION_MANUAL_CLEANUP_REQUIRED", 3)]
    [InlineData(ConfigurationErrorCode.ConfigLockTimeout, "CONFIG_LOCK_TIMEOUT", 4)]
    [InlineData(ConfigurationErrorCode.ConfigWriteFailed, "CONFIG_WRITE_FAILED", 4)]
    [InlineData(ConfigurationErrorCode.IoFailure, "IO_FAILURE", 4)]
    public void EveryDeclaredFailureHasStableCodeAndExitClass(ConfigurationErrorCode error, string code, int exitClass)
    {
        ConfigResults.Code(error).Should().Be(code);
        ConfigResults.ExitClassFor(error).Should().Be((ConfigurationExitClass)exitClass);
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
