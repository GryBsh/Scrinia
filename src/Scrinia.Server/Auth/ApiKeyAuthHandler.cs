using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Scrinia.Server.Auth;

public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyOptions>
{
    private readonly ApiKeyStore _keyStore;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyStore keyStore)
        : base(options, logger, encoder)
    {
        _keyStore = keyStore;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        string rawKey = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(rawKey))
            return Task.FromResult(AuthenticateResult.Fail("Empty bearer token."));

        var result = _keyStore.ValidateKey(rawKey);
        if (result is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or revoked API key."));

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, result.UserId) };
        foreach (var store in result.Stores)
            claims.Add(new Claim("store", store));
        foreach (var perm in result.Permissions)
            claims.Add(new Claim("permission", perm));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
