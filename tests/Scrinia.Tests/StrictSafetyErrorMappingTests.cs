using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public sealed class StrictSafetyErrorMappingTests
{
    public static IEnumerable<object[]> StrictFailures()
    {
        yield return [new MemoryAddressException(MemoryAddressError.Create("bad address")), ErrorCodes.MalformedAddress];
        yield return [new ScopeContainmentException("local:../escape"), ErrorCodes.ScopeContainmentForbidden];
        yield return [new ReservedPathOperationException("unsupported operation"), ErrorCodes.ReservedPathOperationForbidden];
        yield return [new BundleValidationException("invalid bundle"), ErrorCodes.InvalidBundle];
        yield return [new BundleImportException(BundleImportFailure.RolledBack, new IOException("write")), ErrorCodes.BundleImportFailed];
        yield return [new BundleImportException(BundleImportFailure.RecoveryRequired, new IOException("rollback"), "journal"), ErrorCodes.BundleRecoveryRequired];
        yield return [new EmbeddingsProtocolUnsupportedException("version"), ErrorCodes.EmbeddingsProtocolUnsupported];
        yield return [new EmbeddingsProtocolInvalidException("status"), ErrorCodes.EmbeddingsProtocolInvalid];
    }

    [Theory]
    [MemberData(nameof(StrictFailures))]
    public void Every_strict_failure_maps_to_the_declared_code(Exception failure, string expectedCode)
    {
        Assert.Equal(expectedCode, ErrorCodes.ToStableCode(failure));
        Assert.Equal(expectedCode, ResponseBuilder.Error(failure).ErrorCode);
    }
}
