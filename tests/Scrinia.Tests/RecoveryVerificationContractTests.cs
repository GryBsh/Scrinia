using FluentAssertions;
using Scrinia.Core;
using System.Security.Cryptography;
using Xunit;

namespace Scrinia.Tests;

public sealed class RecoveryVerificationContractTests
{
    [Fact]
    public void Recovery_source_uses_only_the_typed_parser_boundary()
    {
        string root = Scrinia.TestDoubles.RepoPaths.Root();
        string[] files =
        [
            "src/Scrinia.Core/FileMemoryStore.cs",
            "src/Scrinia.Core/RecoveryDiscovery.cs",
            "src/Scrinia.Core/RecoveryEnvelope.cs",
            "src/Scrinia.Core/FileEntityTransactionCoordinator.cs",
            "src/Scrinia.Core/MultiMemoryTransactionRecord.cs",
            "src/Scrinia.Core/RecoveryContracts.cs",
            "src/Scrinia.Core/RecoveryVerification.cs",
            "src/Scrinia.Core/AppendJournal.cs",
            "src/Scrinia.Core/RestoreJournal.cs"
        ];
        string source = string.Join('\n', files.Select(file => File.ReadAllText(Path.Combine(root, file))));

        source.Should().NotContain("ParseAndNormalize(");
        source.Should().NotContain("ParseAndValidate(");
        source.Should().NotContain("TryReadOperationId(");
        source.Should().NotContain("RecoveryEnvelopeCodec.Parse(");
        source.Should().NotContain("ParseVerifiedJournal(");
        source.Should().NotContain("ParseVerifiedJson(");
        source.Should().NotContain("ValidateForOperation(");
        source.Should().NotContain("ParseTypedBody(");
        source.Should().NotContain("VerifyTerminalBounded(");
        source.Should().NotContain("NormalizeLegacy(");
        source.Should().NotContain("ParseLegacyJournal(bytes");
        source.Should().NotContain("ConsumeLegacy(bytes");
        source.Should().Contain("AppendJournalCodec.ConsumeParsed(");
        source.Should().Contain("RestoreJournalCodec.ConsumeParsed(");
        string nonParserSource = string.Join('\n', files.Where(file => file is
            "src/Scrinia.Core/RecoveryDiscovery.cs" or
            "src/Scrinia.Core/FileEntityTransactionCoordinator.cs" or
            "src/Scrinia.Core/MultiMemoryTransactionRecord.cs" or
            "src/Scrinia.Core/RecoveryVerification.cs")
            .Select(file => File.ReadAllText(Path.Combine(root, file))));
        nonParserSource.Should().NotContain("JsonDocument.Parse(");
        source.Should().Contain("ConsumeParsed(in RecoveryVerifiedEnvelope envelope)");
        source.Should().NotContain("ReadBounded(");
        source.Should().Contain("HashWorkspaceFile");
        source = source.Replace("System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))", "", StringComparison.Ordinal);
        source.Should().NotContain("ReadAllBytes(");
        source.Should().NotContain("ReadAllBytesAsync(");
        source.Should().NotContain("SHA256.HashData(File.");
        source.Should().Contain("ParseVerifiedEnvelope(");
    }

    [Fact]
    public void Recovery_envelope_exposes_only_the_exact_verified_parser_boundary()
    {
        string root = Scrinia.TestDoubles.RepoPaths.Root();
        string source = File.ReadAllText(Path.Combine(root, "src/Scrinia.Core/RecoveryEnvelope.cs"));
        string parser = File.ReadAllText(Path.Combine(root, "src/Scrinia.Core/RecoveryVerification.cs"));

        parser.Should().Contain("ParseVerifiedEnvelope(");
        source.Should().NotContain("ParseVerifiedEnvelope(");
        source.Should().NotContain("ParseVerifiedJson");
        source.Should().NotContain("ValidateForOperation");
        source.Should().NotContain("ParseTypedBody(");
    }

    [Fact]
    public void Recovery_source_does_not_drop_the_active_cancellation_token()
    {
        string root = Scrinia.TestDoubles.RepoPaths.Root();
        string[] files =
        [
            "src/Scrinia.Core/RecoveryDiscovery.cs",
            "src/Scrinia.Core/MultiMemoryTransactionRecord.cs",
            "src/Scrinia.Core/FileEntityTransactionCoordinator.cs"
        ];
        string source = string.Join('\n', files.Select(file => File.ReadAllText(Path.Combine(root, file))));

        source.Should().NotContain("CancellationToken.None");

        string envelope = File.ReadAllText(Path.Combine(root, "src/Scrinia.Core/RecoveryEnvelope.cs"));
        envelope.Should().NotContain("CancellationToken.None");
    }

    [Fact]
    public void Recovery_production_surface_has_no_raw_legacy_parser_entry_points()
    {
        string root = Scrinia.TestDoubles.RepoPaths.Root();
        // Scan the complete production Core tree. These are the only non-recovery parser families
        // currently permitted to use direct JSON parsing; generated obj files are excluded because
        // they are compiler output rather than production sources.
        string[] allowedNonRecoveryParsers =
        ["BundleFormatService.cs", "HomeConfig.cs", "SharedBaseRuntimeContracts.cs",
            "SharedBaseRegistry.cs", "WorkspaceConfig.cs"];
        string[] files = Directory.GetFiles(Path.Combine(root, "src/Scrinia.Core"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(file).EndsWith(".g.cs", StringComparison.Ordinal) &&
                !allowedNonRecoveryParsers.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            .ToArray();
        string source = string.Join('\n', files.Select(File.ReadAllText));

        source.Should().NotContain("ParseLegacyJournal(");
        source.Should().NotContain("ConsumeLegacy(");
        source.Should().NotContain("ParseLegacyEvidence(");
        source.Should().Contain("ParseVerifiedEnvelope(");
    }

    [Fact]
    public void Primary_discovery_orders_bounded_entity_traversal_before_admission()
    {
        string root = Scrinia.TestDoubles.RepoPaths.Root();
        string source = File.ReadAllText(Path.Combine(root, "src/Scrinia.Core/RecoveryDiscovery.cs"));

        source.Should().Contain("OrderMaintenanceDirectories(");
        source.Should().Contain("OrderMaintenanceEvidencePaths(");
    }
    [Fact]
    public void MaintenanceLimits_exposes_the_authoritative_hard_contract()
    {
        MaintenanceLimits limits = MaintenanceLimits.Hard;

        limits.MaximumBytes.Should().Be(16 * 1024 * 1024);
        limits.MaximumDuration.Should().Be(TimeSpan.FromSeconds(2));
        limits.MaximumOpenOperations.Should().Be(2);
        limits.MaximumStatOperations.Should().Be(8);
        limits.MaximumReadOperations.Should().Be(257);
        limits.ReadBufferBytes.Should().Be(64 * 1024);
    }

    [Fact]
    public void MaintenanceLimits_rejects_a_limit_above_the_hard_ceiling()
    {
        Action create = () => MaintenanceLimits.CreateLowerOverride(
            MaintenanceLimits.Hard.MaximumBytes + 1,
            MaintenanceLimits.Hard.MaximumDuration,
            MaintenanceLimits.Hard.MaximumOpenOperations,
            MaintenanceLimits.Hard.MaximumStatOperations,
            MaintenanceLimits.Hard.MaximumReadOperations,
            MaintenanceLimits.Hard.ReadBufferBytes);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Verifier_reads_source_once_and_accounts_for_the_eof_read()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(root, "evidence.json");
            byte[] bytes = "bounded evidence"u8.ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding("local", root));
            var request = new RecoveryVerificationRequest(
                new RecoveryScopeBinding("local", root), RecoveryEvidenceKind.AppendJournal,
                "evidence.json", null, new RecoveryExpectedHash(hash), null, MaintenanceLimits.Hard);

            RecoveryVerificationResult result = await verifier.VerifyAsync(request, CancellationToken.None);

            result.Outcome.Should().Be(RecoveryVerificationOutcome.Verified);
            result.BytesRead.Should().Be(bytes.Length);
            result.OpenOperations.Should().Be(1);
            result.StatOperations.Should().Be(2);
            result.ReadOperations.Should().Be(2);
            result.FinalZeroByteRead.Should().BeTrue();
            result.SourceContent.Should().NotBeNull();
            result.SourceContent!.Bytes.ToArray().Should().Equal(bytes);
            result.SourceContent.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Verifier_can_return_verified_content_before_parser_supplies_an_embedded_hash()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(root, "evidence.json");
            byte[] bytes = "parser-owned hash"u8.ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding("local", root));
            var request = new RecoveryVerificationRequest(
                new RecoveryScopeBinding("local", root), RecoveryEvidenceKind.AppendJournal,
                "evidence.json", null, default, null, MaintenanceLimits.Hard);

            RecoveryVerificationResult result = await verifier.VerifyAsync(request, CancellationToken.None);

            result.Outcome.Should().Be(RecoveryVerificationOutcome.Verified);
            result.SourceContent.Should().NotBeNull();
            result.SourceContent!.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Verifier_returns_invalid_limits_without_filesystem_operations()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding("local", root));
            var request = new RecoveryVerificationRequest(
                new RecoveryScopeBinding("local", root), RecoveryEvidenceKind.AppendJournal,
                "missing.json", null, new RecoveryExpectedHash("00"), null,
                new MaintenanceLimits(0, TimeSpan.FromSeconds(2), 2, 8, 257, 64 * 1024));

            RecoveryVerificationResult result = await verifier.VerifyAsync(request, CancellationToken.None);

            result.Outcome.Should().Be(RecoveryVerificationOutcome.ProtectedInvalidLimits);
            result.BytesRead.Should().Be(0);
            result.OpenOperations.Should().Be(0);
            result.StatOperations.Should().Be(0);
            result.ReadOperations.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Verifier_rejects_a_reparse_point_before_retaining_external_bytes()
    {
        string root = CreateTemporaryDirectory();
        string outside = CreateTemporaryDirectory();
        try
        {
            string externalPath = Path.Combine(outside, "external.json");
            byte[] externalBytes = "must not be read"u8.ToArray();
            await File.WriteAllBytesAsync(externalPath, externalBytes);
            string linkPath = Path.Combine(root, "evidence.json");

            try
            {
                File.CreateSymbolicLink(linkPath, externalPath);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding("local", root));
            var request = new RecoveryVerificationRequest(
                new RecoveryScopeBinding("local", root), RecoveryEvidenceKind.AppendJournal,
                "evidence.json", null, default, null, MaintenanceLimits.Hard);

            RecoveryVerificationResult result = await verifier.VerifyAsync(request, CancellationToken.None);

            result.Outcome.Should().Be(RecoveryVerificationOutcome.ProtectedUnsafe);
            result.BytesRead.Should().Be(0);
            result.SourceContent.Should().BeNull();
            result.OpenOperations.Should().Be(0);
            result.ReadOperations.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

internal static class RecoveryAdapterTestExtensions
{
    internal static RecoveryEnvelopeDocument ConsumeParsed(this IRecoveryAdapter adapter,
        ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath, string expectedEvidenceDirectory)
    {
        if (!RecoveryEnvelopeCodec.IsCommonEnvelope(bytes))
            return adapter.Operation switch
            {
                RecoveryOperation.Append => RecoveryEnvelopeCodec.FromAppend(
                    AppendJournalCodec.ParseLegacyJournal(bytes, expectedScope, evidencePath, expectedEvidenceDirectory)) with { IsLegacy = true },
                RecoveryOperation.Restore => RecoveryEnvelopeCodec.FromRestore(
                    RestoreJournalCodec.ParseLegacyJournal(bytes, expectedScope, evidencePath)) with { IsLegacy = true },
                _ => ((FileEntityRecoveryAdapter)adapter).ConsumeLegacy(bytes, expectedScope, evidencePath, expectedEvidenceDirectory)
            };

        byte[] ownedBytes = bytes.ToArray();
        using var content = new RecoveryVerificationContent(ownedBytes,
            new RecoveryVerifiedFileMetadata(ownedBytes.Length,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ownedBytes)), true, true, true));
        RecoveryScopeBinding scope = new(expectedScope, expectedEvidenceDirectory);
        RecoveryVerifiedFileMetadata metadata = content.Metadata;
        RecoveryVerifiedEnvelope verified = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(
            in content, in scope, evidencePath, expectedEvidenceDirectory,
            adapter.Operation switch
            {
                RecoveryOperation.Append => RecoveryEvidenceKind.AppendJournal,
                RecoveryOperation.Restore => RecoveryEvidenceKind.RestoreJournal,
                _ => RecoveryEvidenceKind.FileEntityJournal
            }, in metadata);
        return adapter.ConsumeParsed(in verified);
    }
}
