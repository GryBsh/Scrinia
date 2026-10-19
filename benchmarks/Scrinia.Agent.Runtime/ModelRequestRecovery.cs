using System.ClientModel;
using System.Net.Sockets;

namespace Scrinia.Agent.Benchmarks;

internal static class ModelRequestRecovery
{
    public static TimeSpan Delay(int failedAttempt) => TimeSpan.FromSeconds(Math.Min(40, 5 * Math.Pow(2, failedAttempt - 1)));

    public static bool IsTransient(Exception error, CancellationToken ct)
    {
        return error switch
        {
            ClientResultException { Status: 408 or 429 or 500 or 502 or 503 or 504 } => true,
            ClientResultException { Status: 0, InnerException: { } inner } => IsTransient(inner, ct),
            HttpRequestException { StatusCode: { } status } => (int)status is 408 or 429 or 500 or 502 or 503 or 504,
            HttpRequestException http => http.HttpRequestError is HttpRequestError.Unknown or
                HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded,
            HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => true,
            SocketException => true,
            IOException { InnerException: SocketException } => true,
            TimeoutException => true,
            OperationCanceledException => !ct.IsCancellationRequested, // SDK network timeout, not the caller's deadline.
            _ => false
        };
    }
}

// Keep cancellation semantics at the client boundary while allowing the run to stop on a
// deadline during an unresolved outage. Explicit user cancellation still wins in CaseRunner.
internal sealed class ProviderRecoveryInterruptedException(OperationCanceledException inner)
    : OperationCanceledException("Model endpoint recovery was interrupted.", inner, inner.CancellationToken);
