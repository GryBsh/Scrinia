using Scrinia.Core.Resilience;

namespace Scrinia.Server.Chat.Providers;

/// <summary>
/// Sends a chat request under the shared circuit-breaker and retry policy and reports the
/// outcome as a value.
/// <para>Deliberately NOT a base class. The three callers are
/// <c>async IAsyncEnumerable&lt;ChatEvent&gt;</c> iterators and must do their own
/// <c>yield return</c>; C# also forbids <c>yield return</c> inside a <c>try</c> with a
/// <c>catch</c>, which is why the original code captured an error into a local and yielded
/// after the block. A base class cannot yield on the caller's behalf.</para>
/// </summary>
internal static class ChatProviderHttp
{
    /// <summary>
    /// Returns <c>(response, null)</c> on success — the caller owns and must dispose the
    /// response — or <c>(null, message)</c> when the circuit is open or the provider returned
    /// a non-success status. A failed response is disposed here, never handed back.
    /// Exactly one of the two tuple members is non-null on every return.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    public static async Task<(HttpResponseMessage? Response, string? Error)> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        CircuitBreaker circuitBreaker,
        RetryOptions retryOptions,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            circuitBreaker.EnsureClosed();
            response = await RetryPolicy.ExecuteAsync(
                async () => await http.SendAsync(requestFactory(), HttpCompletionOption.ResponseHeadersRead, ct),
                resp => TransientDetector.IsTransient(resp),
                retryOptions,
                logger: null,
                ct);
        }
        catch (CircuitBreakerOpenException ex)
        {
            return (null, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // client disconnect / caller cancel — not a provider health signal
        }
        catch (Exception)
        {
            circuitBreaker.RecordFailure();
            throw; // Let it propagate to ChatEndpoints catch-all
        }

        if (response.IsSuccessStatusCode)
            return (response, null);

        using (response)
        {
            if (TransientDetector.IsTransient(response))
                circuitBreaker.RecordFailure();
            string body = "";
            try { body = await response.Content.ReadAsStringAsync(ct); } catch { }
            return (null, $"Provider returned {(int)response.StatusCode}: {HttpErrorBody.Truncate(body)}");
        }
    }
}
