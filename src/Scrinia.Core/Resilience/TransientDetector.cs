using System.Net;
using System.Net.Sockets;

namespace Scrinia.Core.Resilience;

/// <summary>Classifies failures as transient (retryable) or permanent.</summary>
public static class TransientDetector
{
    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.TooManyRequests,        // 429
        HttpStatusCode.InternalServerError,    // 500
        HttpStatusCode.BadGateway,             // 502
        HttpStatusCode.ServiceUnavailable,     // 503
        HttpStatusCode.GatewayTimeout,         // 504
    ];

    /// <summary>Returns true if the HTTP response indicates a transient failure.</summary>
    public static bool IsTransient(HttpResponseMessage? response) =>
        response is not null && TransientStatusCodes.Contains(response.StatusCode);

    /// <summary>Returns true if the exception indicates a transient failure.</summary>
    public static bool IsTransient(Exception? ex) => ex switch
    {
        null => false,
        TimeoutException => true,
        IOException => true,
        SocketException => true,
        HttpRequestException hre when hre.InnerException is SocketException => true,
        HttpRequestException hre when hre.InnerException is IOException => true,
        _ => false,
    };
}
