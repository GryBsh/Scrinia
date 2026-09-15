using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class MaintenanceContractTests
{
    [Fact]
    public void Request_copies_and_normalizes_collections()
    {
        var scopes = new List<string> { MaintenanceScopes.DefaultLocalScope };
        var protectedPaths = new HashSet<string>(StringComparer.Ordinal) { "versions/a.nmp2" };

        var request = new MaintenanceRequest(scopes, 25, protectedPaths, "continuation");
        scopes[0] = "changed";
        protectedPaths.Add("versions/b.nmp2");

        request.Scopes.Should().Equal(MaintenanceScopes.DefaultLocalScope);
        request.ExplicitlyProtectedRelativePaths.Should().Equal("versions/a.nmp2");
        request.BatchSizeOverride.Should().Be(25);
        request.ContinuationToken.Should().Be("continuation");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public void Request_rejects_batch_sizes_outside_the_contract(int batchSize)
    {
        var action = () => new MaintenanceRequest(null, batchSize, null, null);

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/absolute")]
    [InlineData("https://example.test/file")]
    [InlineData("")]
    public void Request_rejects_unsafe_protected_relative_paths(string path)
    {
        var action = () => new MaintenanceRequest(null, null, new HashSet<string> { path }, null);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Request_uses_the_empty_protection_set_for_null_protected_paths()
    {
        var request = new MaintenanceRequest(null, null, null, null);

        request.ExplicitlyProtectedRelativePaths.Should().BeSameAs(MaintenanceScopes.EmptyProtectionSet);
    }

    [Fact]
    public void Result_reports_warnings_as_an_orthogonal_flag()
    {
        var candidate = new MaintenanceCandidate(
            MaintenanceScopes.DefaultLocalScope,
            "/memory/item",
            MaintenanceCandidateKind.ArchivePair,
            "memory/versions/item.nmp2",
            null,
            null,
            null,
            MaintenanceProtectionReason.Malformed,
            "memory/versions/item.nmp2");
        var candidateResult = new MaintenanceCandidateResult(candidate, false,
            MaintenanceProtectionReason.Malformed, "retained");
        var scopeResult = new MaintenanceScopeResult(MaintenanceScopes.DefaultLocalScope, 1, 0, 0, 1,
            false, false, null, new[] { candidateResult }, new[] { "retained" });
        var result = new MaintenanceResult(1, 0, 0, true, false, false, false, false, false, true,
            null, new[] { scopeResult }, new[] { "retained" }, null);

        result.Completed.Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
        result.PartialFailure.Should().BeFalse();
        result.Scopes.Should().ContainSingle();
    }

    [Fact]
    public void Json_context_serializes_the_maintenance_contract_with_stable_names()
    {
        var result = new MaintenanceResult(0, 0, 0, true, false, true, false, false, false, true,
            "next", Array.Empty<MaintenanceScopeResult>(), new[] { "recovery retained" }, null);

        var json = JsonSerializer.Serialize(result, MaintenanceJsonContext.Default.MaintenanceResult);

        json.Should().Contain("\"deletedArchives\":0");
        json.Should().Contain("\"recoveryRetained\":true");
        json.Should().Contain("\"continuationToken\":\"next\"");
    }
}
