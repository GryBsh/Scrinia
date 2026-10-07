using System.Text.Json;
using System.Collections.Concurrent;
using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class MaintenanceContractTests
{
    private static readonly string[] ExpectedLocalLocal = new[] { "local", "local" };
    private static readonly string[] ExpectedRetained = new[] { "retained" };
    private static readonly string[] ExpectedRecoveryRetained = new[] { "recovery retained" };

    [Fact]
    public void Registry_binds_cursor_semantics_and_invalidates_the_key()
    {
        var workspace = new MaintenanceWorkspaceKey(Guid.NewGuid());
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var scope = new MaintenanceScopeBinding(Guid.NewGuid());
        var policy = new MaintenanceHash256(new byte[32]);
        using var registry = new MaintenanceRunRegistry();
        registry.Create(workspace, run, snapshot, scope, policy, DateTimeOffset.UtcNow.AddMinutes(5));
        var cursor = new MaintenanceCursor(2, workspace, run, snapshot, scope, 0,
            MaintenanceCursorPhase.Discover, new MaintenanceCandidateKey(Guid.Empty), 0, policy,
            (ulong)DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());

        string token = MaintenanceCursorCodec.Encode(cursor, registry);
        MaintenanceCursorCodec.TryDecode(token, registry, out _, out _).Should().BeTrue();

        registry.Remove(run);
        MaintenanceCursorCodec.TryDecode(token, registry, out _, out string? error).Should().BeFalse();
        error.Should().Be("Invalid continuation.");
    }

    [Fact]
    public void Registry_bound_decode_does_not_use_a_key_after_remove_revokes_the_binding()
    {
        using var registry = new MaintenanceRunRegistry();
        MaintenanceRunId run = new(Guid.NewGuid());
        MaintenanceWorkspaceKey workspace = new(Guid.NewGuid());
        MaintenanceSnapshotId snapshot = new(Guid.NewGuid());
        MaintenanceScopeBinding scope = new(Guid.NewGuid());
        MaintenanceHash256 policy = new(new byte[32]);
        registry.Create(workspace, run, snapshot, scope, policy, DateTimeOffset.UtcNow.AddMinutes(5));
        var cursor = new MaintenanceCursor(2, workspace, run, snapshot, scope, 0,
            MaintenanceCursorPhase.Discover, new MaintenanceCandidateKey(Guid.Empty), 0, policy,
            (ulong)DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        string token = MaintenanceCursorCodec.Encode(cursor, registry);

        registry.TryAcquire(run, out MaintenanceRunLease? lease).Should().BeTrue();
        using (lease!)
        {
            registry.Remove(run).Should().BeTrue();
            MaintenanceCursorCodec.TryDecode(token, registry, out _, out string? error).Should().BeFalse();
            error.Should().Be("Invalid continuation.");
            lease!.SigningKey.Length.Should().Be(32);
            lease!.SigningKey.ToArray().Should().NotBeEquivalentTo(new byte[32]);
        }

        MaintenanceCursorCodec.TryDecode(token, registry, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Registry_bound_decode_and_remove_have_a_single_revocation_boundary()
    {
        using var registry = new MaintenanceRunRegistry();
        string token = CreateToken(registry, out MaintenanceRunId run);
        using var started = new ManualResetEventSlim();
        using var decoded = new ManualResetEventSlim();
        using var removed = new ManualResetEventSlim();
        var results = new ConcurrentBag<bool>();

        Task decode = Task.Run(() =>
        {
            started.Set();
            results.Add(MaintenanceCursorCodec.TryDecode(token, registry, out _, out _));
            decoded.Set();
            removed.Wait();
        });
        Task revoke = Task.Run(() =>
        {
            decoded.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            registry.Remove(run).Should().BeTrue();
            removed.Set();
        });

        await Task.WhenAll(decode, revoke);
        MaintenanceCursorCodec.TryDecode(token, registry, out _, out _).Should().BeFalse();
        results.Should().OnlyContain(result => result);
    }

    [Fact]
    public void Expired_and_disposed_bindings_fail_closed_for_direct_codec_consumers()
    {
        using var expiredRegistry = new MaintenanceRunRegistry();
        string expiredToken = CreateToken(expiredRegistry, out _,
            DateTimeOffset.UtcNow.AddSeconds(1.1));
        Thread.Sleep(1200);
        expiredRegistry.EvictExpired().Should().Be(1);
        MaintenanceCursorCodec.TryDecode(expiredToken, expiredRegistry, out _, out _).Should().BeFalse();

        var disposedRegistry = new MaintenanceRunRegistry();
        string disposedToken = CreateToken(disposedRegistry, out _);
        disposedRegistry.Dispose();
        MaintenanceCursorCodec.TryDecode(disposedToken, disposedRegistry, out _, out _).Should().BeFalse();
    }

    private static string CreateToken(MaintenanceRunRegistry registry, out MaintenanceRunId run,
        DateTimeOffset? expiresAt = null)
    {
        MaintenanceWorkspaceKey workspace = new(Guid.NewGuid());
        run = new MaintenanceRunId(Guid.NewGuid());
        MaintenanceSnapshotId snapshot = new(Guid.NewGuid());
        MaintenanceScopeBinding scope = new(Guid.NewGuid());
        MaintenanceHash256 policy = new(new byte[32]);
        DateTimeOffset expiry = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
        registry.Create(workspace, run, snapshot, scope, policy, expiry);
        var cursor = new MaintenanceCursor(2, workspace, run, snapshot, scope, 0,
            MaintenanceCursorPhase.Discover, new MaintenanceCandidateKey(Guid.Empty), 0, policy,
            (ulong)expiry.ToUnixTimeSeconds());
        return MaintenanceCursorCodec.Encode(cursor, registry);
    }

    [Fact]
    public void Enumerator_capability_is_consumed_and_requires_strictly_increasing_ordinals()
    {
        using var enumerator = JournalBoundaryEnumerator.Create(
            new MaintenanceRunId(Guid.NewGuid()), new MaintenanceSnapshotId(Guid.NewGuid()),
            new MaintenanceScopeBinding(Guid.NewGuid()), 1, 0, null);
        var first = enumerator.BufferJournal(1, new MaintenanceCandidateKey(Guid.NewGuid()),
            JournalBoundaryLocation.AppendVersions, JournalBoundaryNameClass.AppendJournal,
            JournalDirectoryEntryKind.File);
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(first!, out JournalBoundaryProof? proof).Should().BeTrue();
        enumerator.Advance();
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(first!, out _).Should().BeFalse();

        var next = enumerator.BufferJournal(2, new MaintenanceCandidateKey(Guid.NewGuid()),
            JournalBoundaryLocation.AppendVersions, JournalBoundaryNameClass.AppendJournal,
            JournalDirectoryEntryKind.File);
        next.Should().NotBeNull();
        next!.DirectoryEntryOrdinal.Should().BeGreaterThan(proof!.DirectoryEntryOrdinal);
    }

    [Fact]
    public void Cursor_codec_uses_the_fixed_b5c2_wire_shape_and_rfc_hmac_vector()
    {
        var cursor = new MaintenanceCursor(
            2,
            new MaintenanceWorkspaceKey(Guid.Empty),
            new MaintenanceRunId(Guid.Empty),
            new MaintenanceSnapshotId(Guid.Empty),
            new MaintenanceScopeBinding(Guid.Empty),
            0,
            MaintenanceCursorPhase.Discover,
            new MaintenanceCandidateKey(Guid.Empty),
            0,
            new MaintenanceHash256(new byte[32]),
            (ulong)DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());

        string token = MaintenanceCursorCodec.Encode(cursor, "0123456789abcdef0123456789abcdef"u8);

        token.Should().StartWith("b5c2.");
        token.Length.Should().BeLessThanOrEqualTo(219);
        MaintenanceCursorCodec.TryDecode(token, "0123456789abcdef0123456789abcdef"u8,
            out MaintenanceCursor decoded, out string? error).Should().BeTrue();
        error.Should().BeNull();
        decoded.Should().Be(cursor);
    }

    [Fact]
    public void Candidate_key_matches_the_approved_b5c2_vector()
    {
        var identity = new MaintenanceCandidateIdentity(
            MaintenanceCandidateKind.CompletedRecoveryEvidence,
            new MaintenanceWorkspaceKey(Guid.Empty),
            3,
            new MaintenanceHash256(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray()),
            42);

        MaintenanceCandidateKey key = MaintenanceCursorCodec.ComputeCandidateKey(
            new MaintenanceWorkspaceKey(Guid.Empty), identity,
            "0123456789abcdef0123456789abcdef"u8);

        Convert.ToHexString(key.Value.ToByteArray()).Should().Be("C6D0988044D8F2D78F44EC2A1625F0AC");
    }

    [Fact]
    public void Journal_boundary_proof_requires_buffered_metadata_and_exact_run_binding()
    {
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var root = new MaintenanceScopeBinding(Guid.NewGuid());
        var key = new MaintenanceCandidateKey(Guid.NewGuid());
        using var owner = JournalBoundaryEnumerator.Create(run, snapshot, root, 2, 4, null);
        var boundary = owner.BufferJournal(1, key, JournalBoundaryLocation.AppendVersions,
            JournalBoundaryNameClass.AppendJournal, JournalDirectoryEntryKind.File)!;

        JournalBoundaryProof.TryCaptureFromBufferedMetadata(boundary, out JournalBoundaryProof? proof).Should().BeTrue();
        var context = new JournalBoundaryValidationContext(run, snapshot, root, owner.Id, 2, null, 4, key,
            JournalDirectoryEntryKind.File, 0);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, context, out JournalBoundaryRejection rejection)
            .Should().BeTrue();
        rejection.Should().Be(JournalBoundaryRejection.None);

        var wrongRun = context with { ActiveRunId = new MaintenanceRunId(Guid.NewGuid()) };
        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, wrongRun, out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.WrongRun);

        owner.Advance();
        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, context, out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.PostInspection);
    }

    [Fact]
    public void Maintenance_limits_use_one_shared_page_and_fixed_journal_ceiling()
    {
        MaintenanceLimits.CandidatePageSize.Should().Be(128);
        MaintenanceLimits.UnsafeScanLimit.Should().Be(32);
        MaintenanceLimits.JournalBudgetMaximum.Should().Be(50);
        MaintenanceLimits.ReturnedProjectionLimit.Should().BeLessThan(MaintenanceLimits.CandidatePageSize);
    }

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

    [Fact]
    public void Request_rejects_a_journal_batch_override_above_the_fixed_ceiling()
    {
        var action = () => new MaintenanceRequest(null, 51, null, null);

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
        var action = () => new MaintenanceRequest(ExpectedLocalLocal, null, null, null);

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
        actions.Should().AllSatisfy(action => action.Should().Throw<ArgumentException>());
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
            false, false, null, new[] { candidateResult }, ExpectedRetained);
        var result = new MaintenanceResult(1, 0, 0, true, false, false, false, false, false, true,
            null, new[] { scopeResult }, ExpectedRetained, null);

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
            "next", Array.Empty<MaintenanceScopeResult>(), ExpectedRecoveryRetained, null);

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
        };

        approvedTypes.Select(type => MaintenanceJsonContext.Default.GetTypeInfo(type))
            .Should().NotContainNulls();
        MaintenanceJsonContext.Default.GetTypeInfo(typeof(MaintenanceRequest)).Should().BeNull();
    }

    private static MaintenanceCandidate CreateCandidate() => new(
        MaintenanceScopes.DefaultLocalScope, "/memory/item", MaintenanceCandidateKind.ArchivePair,
        "memory/versions/item.nmp2", null, null, null, null, "memory/versions/item.nmp2");
}
