using System.Text;

namespace Scrinia.Commands;

/// <summary>
/// Bounded, fail-silent stdin transport for the <c>scri hint</c> advisory. A reader returns the
/// decoded stdin as a string, or <c>null</c> when the input is oversize (over the accepted
/// 65,536 byte maximum), invalid UTF-8, past the two-second deadline, or unreadable. The advisory
/// treats a <c>null</c> result as silence: it emits nothing and still exits 0.
/// </summary>
public interface IBoundedStdinReader
{
    /// <summary>
    /// Read stdin until EOF or the byte ceiling, decode it with strict UTF-8 under a two-second
    /// deadline, and return the text — or <c>null</c> when the input is oversize, invalid UTF-8,
    /// the deadline elapses, or the read fails.
    /// </summary>
    /// <returns>The decoded stdin, or <c>null</c> for any oversize, malformed, timed-out, or
    /// failed read. A <c>null</c> return is a normal, fail-silent outcome, not an error.</returns>
    string? ReadBoundedStdin();
}

/// <summary>
/// Bounded, fail-silent stdin reader for the <c>scri hint</c> advisory. It reads stdin repeatedly
/// until EOF or the 65,537 byte ceiling, decodes the accumulated bytes with strict UTF-8 under a
/// linked two-second deadline, and returns <c>null</c> for any oversize, malformed, timed-out, or
/// failed read — never throwing, so the advisory stays silent and exits 0.
/// </summary>
public sealed class ConsoleStdinReader : IBoundedStdinReader
{
    /// <summary>Accepted maximum: 65,536 bytes. 65,537 or more is rejected.</summary>
    private const int MaxAcceptedBytes = 65_536;

    /// <summary>Bytes consumed per read iteration.</summary>
    private const int ReadChunkSize = 4_096;

    /// <summary>The linked deadline applied to every bounded read.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    /// <summary>Strict UTF-8: no BOM and invalid byte sequences throw (see <see cref="DecoderFallbackException"/>).</summary>
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly Stream _stdin;

    /// <summary>Reads from the process's redirected standard input (or <paramref name="stdin"/> when supplied).</summary>
    /// <param name="stdin">The stream to read from. Defaults to <see cref="Console.OpenStandardInput"/>.</param>
    public ConsoleStdinReader(Stream? stdin = null)
        => _stdin = stdin ?? Console.OpenStandardInput();

    /// <inheritdoc />
    public string? ReadBoundedStdin()
    {
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(Deadline);

        try
        {
            byte[]? bytes = ReadToCeilingOrEof(deadline.Token);
            return bytes is null ? null : StrictUtf8.GetString(bytes);
        }
        catch (OperationCanceledException)
        {
            // The two-second deadline elapsed before EOF: a timeout is a fail-silent null.
            return null;
        }
        catch (Exception)
        {
            // Invalid UTF-8 (DecoderFallbackException) or any read failure: fail silent.
            return null;
        }
    }

    /// <summary>
    /// Read stdin in chunks until EOF or the byte ceiling. Returns the accumulated bytes at EOF,
    /// or <c>null</c> when the accumulated length exceeds the accepted maximum.
    /// </summary>
    private byte[]? ReadToCeilingOrEof(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkSize];
        int totalRead = 0;

        while (true)
        {
            int bytesRead = _stdin.ReadAsync(chunk, 0, chunk.Length, cancellationToken).GetAwaiter().GetResult();
            if (bytesRead == 0)
                return buffer.ToArray(); // EOF: decode everything read so far.

            totalRead += bytesRead;
            if (totalRead > MaxAcceptedBytes)
                return null; // oversize: reject before decoding, never buffering past the ceiling.
            buffer.Write(chunk, 0, bytesRead);
        }
    }
}
