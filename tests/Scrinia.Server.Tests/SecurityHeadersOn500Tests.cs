using System.Net;
using FluentAssertions;

namespace Scrinia.Server.Tests;

/// <summary>
/// The defect <see cref="ApiVersionHeaderOn500Tests"/> pins for the API-version header, one
/// middleware higher: the security-headers middleware set its four headers by direct assignment
/// before <c>await next()</c>, and <c>UseExceptionHandler</c> — registered at
/// <c>Program.cs:352</c>, so it wraps this middleware — calls <c>Response.Clear()</c> on an
/// unhandled exception, emptying the header collection before writing the 500 body. Measured
/// before the fix: a genuine 500 arrived with all four headers ABSENT. The response most likely to
/// carry a stack trace or an unexpected content type was the one response with no protection
/// against sniffing or framing.
///
/// <para>Driven over a real Kestrel host via <see cref="ThrowingStoreServerFactory"/>, whose
/// backend throws from inside <c>Program.cs</c>'s store-resolution middleware — a genuine
/// unhandled exception on the real request path, not a simulated response.</para>
/// </summary>
public sealed class SecurityHeadersOn500Tests : IAsyncLifetime
{
    private readonly ThrowingStoreServerFactory _factory = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateAuthenticatedClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "strict-origin-when-cross-origin")]
    [InlineData("X-XSS-Protection", "0")]
    public async Task GenuineUnhandledException_StillCarriesTheSecurityHeaders(string header, string expected)
    {
        var resp = await _client.GetAsync($"/api/v1/stores/{_factory.ThrowingStore}/memories");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            because: "the configured backend throws when the store is resolved, so this must reach " +
                     "the global exception handler");

        resp.Headers.TryGetValues(header, out var values).Should().BeTrue(
            because: $"{header} must survive the exception handler's Response.Clear() — a 500 is " +
                      "the response that most needs it, not the one that can afford to lose it");
        values!.Should().ContainSingle().Which.Should().Be(expected);
    }

    /// <summary>
    /// Negative control for the theory above: on a non-exception response the headers were never in
    /// question. Without this, a middleware that emitted them ONLY on the error path would still
    /// satisfy the theory. <c>/health/live</c> is used deliberately — plain <c>/health</c> returns
    /// 503 under this factory, because its check enumerates stores and the throwing backend
    /// degrades it (measured), whereas <c>/health/live</c> returns 200 unconditionally
    /// (<c>HealthEndpoints.cs:16</c>).
    /// </summary>
    [Fact]
    public async Task NonExceptionResponse_AlsoCarriesTheSecurityHeaders()
    {
        var resp = await _client.GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.TryGetValues("X-Content-Type-Options", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("nosniff");
    }
}
