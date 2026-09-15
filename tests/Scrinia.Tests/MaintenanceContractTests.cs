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
    public void Request_rejects_null_scope_collections_and_elements_with_argument_exceptions()
    {
        var nullScopes = () => new MaintenanceRequest(null!, null, null, null);
        var nullScopeElement = () => new MaintenanceRequest(new string[] { null! }, null, null, null);

        nullScopes.Should().NotThrow();
        nullScopeElement.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Request_rejects_null_protection_path_elements_and_more_than_4096_paths()
    {
        var nullPath = () => new MaintenanceRequest(null, null, new HashSet<string> { null! }, null);
        var tooManyPaths = () => new MaintenanceRequest(null, null,
            Enumerable.Range(0, 4097).Select(index => $"versions/{index}.nmp2").ToHashSet(StringComparer.Ordinal), null);

        nullPath.Should().Throw<ArgumentException>();
        tooManyPaths.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("token with whitespace")]
    [InlineData("token\nwith-control")]
    public void Request_rejects_invalid_continuation_tokens(string token)
    {
        var action = () => new MaintenanceRequest(null, null, null, token);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Request_rejects_continuation_tokens_over_32_kib()
    {
        var action = () => new MaintenanceRequest(null, null, null, new string('x', 32 * 1024 + 1));

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("../body.nmp2", null)]
    [InlineData("body.nmp2", "../sidecar.json")]
    [InlineData("https://example.test/body", null)]
    public void Candidate_rejects_unsafe_evidence_paths(string bodyPath, string? sidecarPath)
    {
        var action = () => new MaintenanceCandidate(MaintenanceScopes.DefaultLocalScope, "/memory/item",
            MaintenanceCandidateKind.ArchivePair, bodyPath, sidecarPath, null, null, null, "body.nmp2");

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Result_rejects_null_collections_and_warning_elements_with_argument_exceptions()
    {
        var nullScopes = () => new MaintenanceResult(0, 0, 0, true, false, false, false, false, false,
            false, null, null!, Array.Empty<string>(), null);
        var nullWarnings = () => new MaintenanceResult(0, 0, 0, true, false, false, false, false, false,
            false, null, Array.Empty<MaintenanceScopeResult>(), new string[] { null! }, null);

        nullScopes.Should().Throw<ArgumentException>();
        nullWarnings.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Continuation_payload_defensively_copies_snapshot_candidates()
    {
        var snapshots = new List<MaintenanceSnapshotCandidate>
        {
            new("/memory/item", "memory/versions/item.nmp2", DateTimeOffset.UtcNow, "pair", true, null)
        };
        var payload = new MaintenanceContinuationPayload(1, "workspace", "local", "local", "archive",
            "/memory/item", "memory/versions/item.nmp2", snapshots, 12, DateTimeOffset.UtcNow);
        snapshots.Clear();

        payload.SnapshotCandidates.Should().ContainSingle();
    }

    [Fact]
    public void Contracts_reject_negative_counts_and_snapshot_bytes()
    {
        var scope = () => new MaintenanceScopeResult("local", -1, 0, 0, 0, false, false, null,
            Array.Empty<MaintenanceCandidateResult>(), Array.Empty<string>());
        var result = () => new MaintenanceResult(-1, 0, 0, true, false, false, false, false, false,
            false, null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(), null);
        var payload = () => new MaintenanceContinuationPayload(1, "workspace", "local", "local", "archive",
            "/memory/item", "memory/versions/item.nmp2", Array.Empty<MaintenanceSnapshotCandidate>(), -1,
            DateTimeOffset.UtcNow);

        scope.Should().Throw<ArgumentException>();
        result.Should().Throw<ArgumentException>();
        payload.Should().Throw<ArgumentException>();
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

    [Fact]
    public void Json_context_registers_exact_approved_roots_and_excludes_requests()
    {
        var approvedTypes = new[]
        {
            typeof(MaintenanceCandidateKind),
            typeof(MaintenanceProtectionReason),
            typeof(MaintenanceCandidate),
            typeof(MaintenanceCandidateResult),
            typeof(MaintenanceScopeResult),
            typeof(MaintenanceResult),
            typeof(MaintenanceContinuationPayload),
            typeof(MaintenanceSnapshotCandidate)
        };

        approvedTypes.Select(type => MaintenanceJsonContext.Default.GetTypeInfo(type))
            .Should().NotContainNulls();
        MaintenanceJsonContext.Default.GetTypeInfo(typeof(MaintenanceRequest)).Should().BeNull();
    }
}
