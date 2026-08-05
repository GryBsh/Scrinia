using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Pins that reconcile() resolves an .nmp2 conflict from the whole conflict/current and
/// conflict/incoming copies even when the working-tree marker body is not a decodable artifact
/// on its own — here, the marker sections hold truncated fragments (header line plus a partial
/// data line, missing ##PAD and NMP/END) rather than complete encoded content.
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

        // Working-tree file: markers wrapping truncated fragments (header + partial data line,
        // missing ##PAD/NMP/END) that do not decode on their own.
        string oursFragment = TruncatedFragment(oursWhole);
        string theirsFragment = TruncatedFragment(theirsWhole);
        await File.WriteAllTextAsync(artifact,
            "<<<<<<< ours:auth-flow.nmp2\n" + oursFragment +
            "\n=======\n" + theirsFragment +
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
                because: "the whole incoming copy is decodable where the marker fragment is not");
    }

    /// <summary>Header line plus half of the first data line — missing ##PAD/NMP/END, so this
    /// alone is not a decodable NMP/2 artifact.</summary>
    private static string TruncatedFragment(string wholeArtifact)
    {
        string[] lines = wholeArtifact.Split('\n');
        string firstDataLine = lines[1];
        return lines[0] + "\n" + firstDataLine[..(firstDataLine.Length / 2)];
    }
}
