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
    [InlineData(0)]
    [InlineData(129)]
    public void Request_rejects_scope_counts_outside_the_contract(int scopeCount)
    {
        var scopes = Enumerable.Range(0, scopeCount).Select(index => $"scope-{index}").ToArray();

        var action = () => new MaintenanceRequest(scopes, null, null, null);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Request_rejects_duplicate_scopes_using_ordinal_comparison()
    {
        var action = () => new MaintenanceRequest(new[] { "local", "local" }, null, null, null);

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

    [Fact]
    public void Request_accepts_exactly_4096_protection_paths_and_rejects_the_next_path()
    {
        var paths = Enumerable.Range(0, 4096).Select(index => $"versions/{index}.nmp2").ToHashSet(StringComparer.Ordinal);
        var accepted = new MaintenanceRequest(null, null, paths, null);
        var tooMany = new HashSet<string>(paths, StringComparer.Ordinal) { "versions/4096.nmp2" };

        accepted.ExplicitlyProtectedRelativePaths.Should().HaveCount(4096);
        var action = () => new MaintenanceRequest(null, null, tooMany, null);
        action.Should().Throw<ArgumentException>();
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
    public void Scope_and_result_helpers_reject_null_collection_elements_with_argument_exceptions()
    {
        var nullCandidate = () => new MaintenanceScopeResult("local", 0, 0, 0, 0, false, false, null,
            new MaintenanceCandidateResult[] { null! }, Array.Empty<string>());
        var nullScope = () => new MaintenanceResult(0, 0, 0, true, false, false, false, false, false,
            false, null, new MaintenanceScopeResult[] { null! }, Array.Empty<string>(), null);
        var nullWarning = () => new MaintenanceScopeResult("local", 0, 0, 0, 0, false, false, null,
            Array.Empty<MaintenanceCandidateResult>(), new string[] { null! });

        nullCandidate.Should().Throw<ArgumentException>();
        nullScope.Should().Throw<ArgumentException>();
        nullWarning.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Continuation_payload_rejects_null_snapshot_collections_and_elements()
    {
        var nullCollection = () => new MaintenanceContinuationPayload(1, "workspace", "local", "local", "archive",
            "/memory/item", "memory/versions/item.nmp2", null!, 0, DateTimeOffset.UtcNow);
        var nullElement = () => new MaintenanceContinuationPayload(1, "workspace", "local", "local", "archive",
            "/memory/item", "memory/versions/item.nmp2", new MaintenanceSnapshotCandidate[] { null! }, 0,
            DateTimeOffset.UtcNow);

        nullCollection.Should().Throw<ArgumentException>();
        nullElement.Should().Throw<ArgumentException>();
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
    public void Results_defensively_copy_every_exposed_collection()
    {
        var candidate = CreateCandidate();
        var candidateResults = new List<MaintenanceCandidateResult> { new(candidate, false, null, null) };
        var scopeWarnings = new List<string> { "scope warning" };
        var scope = new MaintenanceScopeResult("local", 1, 0, 0, 1, false, false, null,
            candidateResults, scopeWarnings);
        var scopes = new List<MaintenanceScopeResult> { scope };
        var resultWarnings = new List<string> { "result warning" };
        var result = new MaintenanceResult(1, 0, 0, true, false, false, false, false, false, false,
            null, scopes, resultWarnings, null);

        candidateResults.Clear();
        scopeWarnings.Clear();
        scopes.Clear();
        resultWarnings.Clear();

        scope.Candidates.Should().ContainSingle();
        scope.Warnings.Should().ContainSingle();
        result.Scopes.Should().ContainSingle();
        result.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void Contracts_reject_negative_scope_counts()
    {
        var actions = new[]
        {
            () => new MaintenanceScopeResult("local", -1, 0, 0, 0, false, false, null,
                Array.Empty<MaintenanceCandidateResult>(), Array.Empty<string>()),
            () => new MaintenanceScopeResult("local", 0, -1, 0, 0, false, false, null,
                Array.Empty<MaintenanceCandidateResult>(), Array.Empty<string>()),
            () => new MaintenanceScopeResult("local", 0, 0, -1, 0, false, false, null,
                Array.Empty<MaintenanceCandidateResult>(), Array.Empty<string>()),
            () => new MaintenanceScopeResult("local", 0, 0, 0, -1, false, false, null,
                Array.Empty<MaintenanceCandidateResult>(), Array.Empty<string>())
        };

        actions.Should().AllSatisfy(action => action.Should().Throw<ArgumentException>());
    }

    [Fact]
    public void Contracts_reject_negative_result_counts_and_snapshot_bytes()
    {
        var actions = new[]
        {
            () => new MaintenanceResult(-1, 0, 0, true, false, false, false, false, false,
                false, null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(), null),
            () => new MaintenanceResult(0, -1, 0, true, false, false, false, false, false,
                false, null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(), null),
            () => new MaintenanceResult(0, 0, -1, true, false, false, false, false, false,
                false, null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(), null)
        };
        var payload = () => new MaintenanceContinuationPayload(1, "workspace", "local", "local", "archive",
            "/memory/item", "memory/versions/item.nmp2", Array.Empty<MaintenanceSnapshotCandidate>(), -1,
            DateTimeOffset.UtcNow);

        actions.Should().AllSatisfy(action => action.Should().Throw<ArgumentException>());
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
    public void Result_reports_empty_warnings_as_false_even_when_input_flag_is_true()
    {
        var result = new MaintenanceResult(0, 0, 0, true, false, false, false, false, false, true,
            null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(), null);

        result.HasWarnings.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, true, true, true, true)]
    public void Result_preserves_valid_orthogonal_flag_combinations(bool completed, bool moreWork,
        bool recoveryRetained, bool lockContended, bool partialFailure)
    {
        var result = new MaintenanceResult(0, 0, 0, completed, moreWork, recoveryRetained,
            lockContended, false, partialFailure, false, null, Array.Empty<MaintenanceScopeResult>(),
            Array.Empty<string>(), null);

        result.Completed.Should().Be(completed);
        result.MoreWork.Should().Be(moreWork);
        result.RecoveryRetained.Should().Be(recoveryRetained);
        result.LockContended.Should().Be(lockContended);
        result.PartialFailure.Should().Be(partialFailure);
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

    private static MaintenanceCandidate CreateCandidate() => new(
        MaintenanceScopes.DefaultLocalScope, "/memory/item", MaintenanceCandidateKind.ArchivePair,
        "memory/versions/item.nmp2", null, null, null, null, "memory/versions/item.nmp2");
}
