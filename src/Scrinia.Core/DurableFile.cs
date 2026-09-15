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

    /// <summary>Raises an append-specific injected failure at a deterministic durable boundary.</summary>
    internal static void ThrowAppendFault(AppendFaultPoint point, string operationId, string path)
    {
        Exception? failure = FileMemoryStore.InjectAppendFault(point, operationId, path);
        if (failure is not null)
            throw failure;
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

    internal static void AtomicWriteAllTextForAppend(
        string path, string content, string operationId,
        AppendFaultPoint writePoint, AppendFaultPoint flushPoint) =>
        AtomicWrite(path, Utf8NoBom.GetBytes(content), "write text", operationId, writePoint, flushPoint);

    internal static void AtomicWriteAllBytesForAppend(
        string path, ReadOnlySpan<byte> content, string operationId,
        AppendFaultPoint writePoint, AppendFaultPoint flushPoint) =>
        AtomicWrite(path, content, "write bytes", operationId, writePoint, flushPoint);

    private static void AtomicWrite(string path, ReadOnlySpan<byte> content, string operation)
        => AtomicWrite(path, content, operation, null, null, null);

    private static void AtomicWrite(
        string path, ReadOnlySpan<byte> content, string operation, string? operationId,
        AppendFaultPoint? writePoint, AppendFaultPoint? flushPoint)
    {
        byte[] bytes = content.ToArray();
        string? temporaryPath = null;

        try
        {
            FileRetry.Run(() =>
            {
                string candidatePath = CreateTemporaryPath(path);
                using var stream = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                temporaryPath = candidatePath;
                WriteTemporaryFile(stream, bytes, path, operationId, writePoint, flushPoint);
            });
            FileRetry.Run(() =>
            {
                ThrowInjected(DurableWritePhase.BeforeMove, path);
                File.Move(temporaryPath!, path, overwrite: true);
            });
        }
        catch (Exception exception)
        {
            if (temporaryPath is not null)
                TryDeleteTemporaryFile(temporaryPath);
            throw new DurableWriteException(operation, path, exception);
        }
    }

    private static string CreateTemporaryPath(string destination)
    {
        string directory = Path.GetDirectoryName(destination) ?? ".";
        string shortRandomName = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
        return Path.Combine(directory, shortRandomName + ".tmp");
    }

    private static void WriteTemporaryFile(
        FileStream stream, byte[] bytes, string destination, string? operationId = null,
        AppendFaultPoint? writePoint = null, AppendFaultPoint? flushPoint = null)
    {
        if (operationId is not null && writePoint is not null)
            ThrowAppendFault(writePoint.Value, operationId, destination);
        ThrowInjected(DurableWritePhase.BeforeWrite, destination);

        stream.Write(bytes);

        if (operationId is not null && flushPoint is not null)
            ThrowAppendFault(flushPoint.Value, operationId, destination);
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
