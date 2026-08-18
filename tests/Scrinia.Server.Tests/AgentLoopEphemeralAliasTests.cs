using System.Text.Json;
using Scrinia.Core;
using Scrinia.Server.Chat;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// The fifth address surface, and one of the two with no shared helper to protect it: the chat
/// agent's <c>recall_memory</c> tool reaches <see cref="MemoryOrchestrator.ShowAsync"/> directly,
/// bypassing the MCP tool methods entirely. Its policy is the facade's — canonicalize and PROCEED,
/// because the server process persists and an ephemeral memory written in it is still readable.
///
/// <para>Companion to <c>Scrinia.Tests.Naming.AddressSurfaceCoverageTests</c>, which pins the MCP
/// facade and the MCP resource, and to <see cref="EphemeralAliasRefusalTests"/>, which pins REST's
/// deliberately opposite answer.</para>
/// </summary>
public class AgentLoopEphemeralAliasTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "scrinia-agentloop-" + Guid.NewGuid().ToString("N"));

    public AgentLoopEphemeralAliasTests() =>
        Directory.CreateDirectory(Path.Combine(_dir, ".scrinia", "memories"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RecallMemory_TreatsTempPathAsTheEphemeralMemory()
    {
        var store = new FileMemoryStore(_dir);
        await MemoryOrchestrator.StoreAsync(store, new StoreRequest(["the secret note"], "~probe"));

        // The alias spelling the LLM may well emit, against the memory stored under the canonical
        // one. Without canonicalization this resolves to the persistent scope local:temp and comes
        // back "not found".
        string result = await AgentLoop.ExecuteRecallAsync(
            JsonDocument.Parse("""{"name":"/temp/probe"}""").RootElement, store, default);

        Assert.Contains("the secret note", result, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", result, StringComparison.OrdinalIgnoreCase);
    }
}
