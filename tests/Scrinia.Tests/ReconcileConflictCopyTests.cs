using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// A conflicted .nmp2 in the working tree holds two interleaved base64 halves, which do not
/// reliably decode. The merge driver also writes whole valid copies to conflict/current and
/// conflict/incoming; reconcile must resolve from those.
/// </summary>
public sealed class ReconcileConflictCopyTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task Reconcile_Nmp2WithConflictCopies_ResolvesFromTheWholeCopy()
    {
        using var scope = new TestHelpers.StoreScope();

        string dir = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "local");
        Directory.CreateDirectory(dir);
        string artifact = Path.Combine(dir, "auth-flow.nmp2");

        string oursWhole = Nmp2ChunkedEncoder.Encode("the ours version");
        string theirsWhole = Nmp2ChunkedEncoder.Encode("the theirs version");

        // Working-tree file: markers wrapping halves that will not decode on their own.
        await File.WriteAllTextAsync(artifact,
            "<<<<<<< ours:auth-flow.nmp2\n" + oursWhole +
            "\n=======\n" + theirsWhole +
            "\n>>>>>>> theirs:auth-flow.nmp2\n");

        Directory.CreateDirectory(Path.Combine(dir, "conflict", "current"));
        Directory.CreateDirectory(Path.Combine(dir, "conflict", "incoming"));
        await File.WriteAllTextAsync(Path.Combine(dir, "conflict", "current", "auth-flow.nmp2"), oursWhole);
        await File.WriteAllTextAsync(Path.Combine(dir, "conflict", "incoming", "auth-flow.nmp2"), theirsWhole);

        string result = await Tools().MemoryReconcile(
            conflictId: "memories/local/auth-flow.nmp2", choice: "theirs");

        ResponseParser.Parse(result).Status.Should().Be("success");

        string resolved = await File.ReadAllTextAsync(artifact);
        resolved.Should().NotContain("<<<<<<<");
        System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(resolved))
            .Should().Be("the theirs version",
                because: "the whole incoming copy is decodable where the marker half is not");
    }
}
