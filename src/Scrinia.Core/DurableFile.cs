using System.Text;
using Scrinia.Core.Resilience;

namespace Scrinia.Core;

/// <summary>
/// Replaces one file through a flushed sibling temporary file. The file contents are flushed, but
/// the containing directory is not: a power loss can retain the old file if the rename is lost.
/// </summary>
internal static class DurableFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly AsyncLocal<Func<DurableWritePhase, string, Exception?>?> FailureInjectorContext = new();

    /// <summary>Test-only failure injection seam scoped to the current asynchronous flow.</summary>
    internal static Func<DurableWritePhase, string, Exception?>? FailureInjector
    {
        get => FailureInjectorContext.Value;
        set => FailureInjectorContext.Value = value;
    }

    internal static void AtomicWriteAllText(string path, string content)
    {
        byte[] bytes;

        try
        {
            bytes = Utf8NoBom.GetBytes(content);
        }
        catch (Exception exception)
        {
            throw new DurableWriteException("write text", path, exception);
        }

        AtomicWrite(path, bytes, "write text");
    }

    internal static void AtomicWriteAllBytes(string path, ReadOnlySpan<byte> content) =>
        AtomicWrite(path, content, "write bytes");

    private static void AtomicWrite(string path, ReadOnlySpan<byte> content, string operation)
    {
        byte[] bytes = content.ToArray();
        string temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            FileRetry.Run(() => WriteTemporaryFile(temporaryPath, bytes, path));
            FileRetry.Run(() =>
            {
                ThrowInjected(DurableWritePhase.BeforeMove, path);
                File.Move(temporaryPath, path, overwrite: true);
            });
        }
        catch (Exception exception)
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw new DurableWriteException(operation, path, exception);
        }
    }

    private static void WriteTemporaryFile(string temporaryPath, byte[] bytes, string destination)
    {
        ThrowInjected(DurableWritePhase.BeforeWrite, destination);

        using var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);

        ThrowInjected(DurableWritePhase.BeforeFlush, destination);
        stream.Flush(flushToDisk: true);
    }

    private static void ThrowInjected(DurableWritePhase phase, string destination)
    {
        Exception? failure = FailureInjector?.Invoke(phase, destination);
        if (failure is not null)
            throw failure;
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch
        {
            // Cleanup must not hide the original write failure.
        }
    }
}

internal enum DurableWritePhase
{
    BeforeWrite,
    BeforeFlush,
    BeforeMove
}
