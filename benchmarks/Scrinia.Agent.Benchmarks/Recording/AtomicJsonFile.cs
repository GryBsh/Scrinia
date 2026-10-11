using System.Security.Cryptography;
using System.Text;

namespace Scrinia.Agent.Benchmarks;

public sealed class AtomicJsonFile(Func<string, Stream>? createStream = null)
{
    public static void ValidateAnswerHash(string text, string expectedSha256)
    {
        if (text is null || expectedSha256 != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))))
            throw new InvalidDataException("Saved answer digest does not match its text.");
    }

    public async Task WriteAsync(string destination, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PathSafety.RequireNoLinks(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = createStream?.Invoke(temporary) ?? new FileStream(temporary,
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            if (stream is FileStream file) file.Flush(flushToDisk: true);
        }
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, destination, overwrite: true);
    }
}
