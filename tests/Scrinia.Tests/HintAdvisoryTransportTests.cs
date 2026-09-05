using System.Text;

using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the bounded, fail-silent stdin transport that the <c>scri hint</c> advisory owns
/// (Task 2). The <see cref="ConsoleStdinReader"/> reads stdin repeatedly until EOF or the 65,537
/// byte ceiling, rejects anything over the 65,536 accepted maximum, decodes with strict UTF-8
/// (no BOM, invalid bytes rejected), applies a linked two-second cancellation deadline, and
/// returns <c>null</c> for oversize, invalid UTF-8, timeout, or read failure. Positional input
/// always wins and never invokes the reader.
/// </summary>
public sealed class HintAdvisoryTransportTests
{
    // ── SelectPrompt routing through the bounded reader ───────────────────

    [Fact]
    public void SelectPrompt_PositionalWins_NeverCallsReader()
    {
        // A positional prompt must win and the bounded reader must never be invoked.
        var fake = new RecordingReader();

        string? selected = PromptHintTransport.SelectPrompt("commit", fake);

        selected.Should().Be("commit");
        fake.ReadCount.Should().Be(0, "the reader must never be called when a positional prompt wins");
    }

    [Fact]
    public void SelectPrompt_NoPositional_RoutesThroughReader()
    {
        // With no positional prompt the reader supplies stdin verbatim.
        var fake = new RecordingReader("push");

        string? selected = PromptHintTransport.SelectPrompt(null, fake);

        selected.Should().Be("push");
        fake.ReadCount.Should().Be(1, "the reader must be called exactly once when no positional prompt is present");
    }

    // ── Reader: accepted maximum (exactly 65,536 bytes) ───────────────────

    [Fact]
    public void ConsoleStdinReader_AcceptsExactly65536Bytes_PassesThrough()
    {
        byte[] bytes = RepeatingBytes(65_536);
        using var stream = new MemoryStream(bytes);
        var reader = new ConsoleStdinReader(stream);

        string? result = reader.ReadBoundedStdin();

        result.Should().HaveLength(65_536);
    }

    // ── Reader: one byte over the ceiling is rejected ─────────────────────

    [Fact]
    public void ConsoleStdinReader_65537Bytes_ReturnsNull()
    {
        byte[] bytes = RepeatingBytes(65_537);
        using var stream = new MemoryStream(bytes);
        var reader = new ConsoleStdinReader(stream);

        reader.ReadBoundedStdin().Should().BeNull("input over the accepted maximum must be rejected");
    }

    // ── Reader: strict UTF-8 rejects invalid bytes ────────────────────────

    [Fact]
    public void ConsoleStdinReader_InvalidUtf8_ReturnsNull()
    {
        // 0xFF 0xFE are never valid UTF-8 lead bytes; the strict decoder must refuse them.
        byte[] bytes = { 0xFF, 0xFE, 0x00, 0x41 };
        using var stream = new MemoryStream(bytes);
        var reader = new ConsoleStdinReader(stream);

        reader.ReadBoundedStdin().Should().BeNull("invalid UTF-8 must be rejected");
    }

    // ── Reader: two-second deadline ───────────────────────────────────────

    [Fact]
    public void ConsoleStdinReader_TwoSecondTimeout_ReturnsNull()
    {
        var reader = new ConsoleStdinReader(new BlockingStream());

        string? result = reader.ReadBoundedStdin();

        result.Should().BeNull("stdin that never reaches EOF within two seconds must time out to null");
    }

    // ── Reader: read failure is fail-silent ───────────────────────────────

    [Fact]
    public void ConsoleStdinReader_IOError_ReturnsNull()
    {
        var reader = new ConsoleStdinReader(new ThrowingStream());

        reader.ReadBoundedStdin().Should().BeNull("a read failure must be fail-silent (null)");
    }

    // ── Reader: repeated partial reads concatenate correctly at EOF ───────

    [Fact]
    public void ConsoleStdinReader_RepeatedPartialReadsUntilEof_Concatenates()
    {
        string original = "The quick brown fox jumps over the lazy dog 0123456789.";
        byte[] bytes = Encoding.UTF8.GetBytes(original);
        using var stream = new ChunkedStream(bytes, chunkSize: 4);
        var reader = new ConsoleStdinReader(stream);

        string? result = reader.ReadBoundedStdin();

        result.Should().Be(original, "each small chunk must concatenate into the exact original text at EOF");
    }

    private static byte[] RepeatingBytes(int count)
    {
        var bytes = new byte[count];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)('a' + (i % 26));
        return bytes;
    }

    /// <summary>Records how many times the bounded read is invoked; used to prove positional wins.</summary>
    private sealed class RecordingReader : IBoundedStdinReader
    {
        private readonly string? _value;

        public RecordingReader(string? value = null) => _value = value;

        public int ReadCount { get; private set; }

        public string? ReadBoundedStdin()
        {
            ReadCount++;
            return _value;
        }
    }

    /// <summary>Redirected stdin that returns <paramref name="chunkSize"/> bytes per read until EOF.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _position;

        public ChunkedStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = Math.Max(1, chunkSize);
        }

        public override bool CanRead => true;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }

        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.Run(() => ReadCore(buffer, offset, count));

        private int ReadCore(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length)
                return 0; // EOF

            int available = _data.Length - _position;
            int n = Math.Min(count, Math.Min(_chunkSize, available));
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
    }

    /// <summary>Redirected stdin that never produces data; the two-second deadline must fire.</summary>
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.Run(async () =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            });

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
    }

    /// <summary>Redirected stdin that fails every read with an IOException.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException("simulated stdin read failure");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException<int>(new IOException("simulated stdin read failure"));

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
    }
}
