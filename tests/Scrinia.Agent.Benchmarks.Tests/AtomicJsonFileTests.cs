using System.Security.Cryptography;
using System.Text;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class AtomicJsonFileTests
{
    [Fact]
    public async Task Interrupted_write_preserves_previous_destination_and_partial_evidence()
    {
        using var temp = new ScratchDirectory();
        var destination = temp.Write("final.json", "old");
        string? failedTemporaryFile = null;
        var writer = new AtomicJsonFile(path =>
        {
            failedTemporaryFile = path;
            return new FaultingStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write), 1);
        });
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(destination, Encoding.UTF8.GetBytes("new"), CancellationToken.None));
        Assert.Equal("old", File.ReadAllText(destination));
        Assert.True(File.Exists(failedTemporaryFile));
        Assert.Equal("n", File.ReadAllText(failedTemporaryFile!));
    }

    [Fact]
    public async Task Successful_write_replaces_destination_with_complete_bytes()
    {
        using var temp = new ScratchDirectory();
        var path = temp.Write("final.json", "old");
        await new AtomicJsonFile().WriteAsync(path, Encoding.UTF8.GetBytes("new"), CancellationToken.None);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Single(Directory.EnumerateFiles(temp.Root));
    }

    [Fact]
    public void Answer_hash_validates_exact_utf8_bytes()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("original")));
        Assert.Throws<InvalidDataException>(() => AtomicJsonFile.ValidateAnswerHash("modified", hash));
        AtomicJsonFile.ValidateAnswerHash("original", hash);
    }
}
