using System.Net;
using System.Net.Http.Json;

namespace Scrinia.Server.Tests;

/// <summary>
/// D-2: the ephemeral alias is a SURFACE form. The REST write routes sit below the surface, so
/// they refuse it rather than guessing between an ephemeral and the topic "temp". Before this,
/// POST /memories with an alias name wrote permanent bytes under memories/temp/, making it a
/// third, different memory from the two the MCP surface could produce.
/// </summary>
public class EphemeralAliasRefusalTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public EphemeralAliasRefusalTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task PostMemories_RefusesTheAlias()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "/temp/x", content = new[] { "hello" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ephemeral", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The canonical form is unchanged — only the alias is refused.</summary>
    [Fact]
    public async Task PostMemories_StillAcceptsTheCanonicalEphemeralForm()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "~x", content = new[] { "hello" } });

        Assert.True(response.IsSuccessStatusCode,
            $"expected success, got {(int)response.StatusCode} — a non-success here would make "
            + "the BadRequest assertion above vacuous");
    }

    /// <summary>A near-miss name must still be storable.</summary>
    [Fact]
    public async Task PostMemories_AcceptsANearMissName()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "/tempest/x", content = new[] { "hello" } });

        Assert.True(response.IsSuccessStatusCode);
    }
}
