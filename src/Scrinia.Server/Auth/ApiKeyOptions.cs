using Microsoft.AspNetCore.Authentication;

namespace Scrinia.Server.Auth;

public sealed class ApiKeyOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
}
