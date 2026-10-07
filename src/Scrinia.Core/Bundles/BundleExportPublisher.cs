using System.IO.Compression;
using System.Security.Cryptography;

namespace Scrinia.Core.Bundles;

/// <summary>Publishes a prepared bundle while retaining evidence until compensation is proven.</summary>
internal static class BundleExportPublisher
{
    internal static void Publish(string preparedPath, string destinationPath)
    {
        string? priorPath = null;
        byte[]? priorHash = null;
        bool replaced = false;
        bool retainEvidence = false;
        try
        {
            // The archive must be closed and readable before the destination is touched.
            using (var archive = ZipFile.OpenRead(preparedPath))
            {
                if (archive.GetEntry("manifest.json") is null)
                    throw new InvalidDataException("The prepared bundle has no manifest.");
                foreach (var entry in archive.Entries)
                {
                    using var input = entry.Open();
                    input.CopyTo(Stream.Null);
                }
            }
            byte[] outputHash = Hash(preparedPath);
            if (File.Exists(destinationPath))
            {
                string candidate = TemporaryPath(destinationPath, "prior");
                using (var reservation = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    priorPath = candidate;
                DurableFile.AtomicCopy(destinationPath, priorPath);
                priorHash = Hash(priorPath);
            }
            try
            {
                DurableFile.AtomicCopy(preparedPath, destinationPath, () => replaced = true);
            }
            catch (Exception publicationFailure)
            {
                if (!replaced) throw;
                try
                {
                    VerifyOutputOwnership();
                    if (priorPath is not null)
                    {
                        // Copy rather than move: the prior remains available if its restore cannot be synchronized.
                        DurableFile.AtomicCopy(priorPath, destinationPath, beforeReplace: VerifyOutputOwnership);
                        if (!Matches(destinationPath, priorHash!))
                            throw new IOException("The restored destination does not match its prior backup.");
                    }
                    else
                    {
                        DurableFile.DeleteAndSync(destinationPath, beforeDelete: VerifyOutputOwnership);
                        if (File.Exists(destinationPath))
                            throw new IOException("The attempted export could not be removed.");
                    }
                }
                catch (Exception compensationFailure)
                {
                    retainEvidence = true;
                    string evidence = priorPath is null
                        ? $"prepared output retained at '{preparedPath}'"
                        : $"prior backup retained at '{priorPath}'; prepared output retained at '{preparedPath}'";
                    throw new DurableWriteException($"bundle publication; compensation unproven, {evidence}",
                        destinationPath, new AggregateException(publicationFailure, compensationFailure));
                }
                throw;
            }

            void VerifyOutputOwnership()
            {
                // Recheck at each actual compensation attempt, after its injected/retried boundary.
                // Filesystem check + replacement/deletion is not an atomic compare-and-swap.
                if (!Matches(destinationPath, outputHash))
                    throw new IOException("The destination no longer matches this export; compensation refused.");
            }
        }
        catch (DurableWriteException) { throw; }
        catch (Exception exception)
        {
            throw new DurableWriteException("bundle publication", destinationPath, exception);
        }
        finally
        {
            if (!retainEvidence)
            {
                TryDelete(preparedPath);
                if (priorPath is not null) TryDelete(priorPath);
            }
        }
    }

    internal static string TemporaryPath(string destination, string kind = "tmp") =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,
            "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + "." + kind);

    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    private static bool Matches(string path, byte[] expected) =>
        File.Exists(path) && Hash(path).AsSpan().SequenceEqual(expected);

    internal static void TryDelete(string path)
    {
        // Only attempt-owned files reach cleanup; a cleanup error must not report a rollback.
        try { DurableFile.DeleteAndSync(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}