namespace Scrinia.Agent.Benchmarks.Tests;

internal sealed class FaultingStream(Stream inner, int bytesBeforeFailure) : Stream
{
    private int _remaining = bytesBeforeFailure;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override void Write(byte[] buffer, int offset, int count)
    {
        int allowed = Math.Min(count, _remaining);
        inner.Write(buffer, offset, allowed);
        _remaining -= allowed;
        if (allowed < count) throw new IOException("Injected write interruption.");
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Write(buffer.ToArray(), 0, buffer.Length);
        return ValueTask.CompletedTask;
    }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
