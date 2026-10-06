using System.IO.Compression;

namespace Scrinia.Core.Bundles;

/// <summary>Admits an entire archive before reading, then accounts for each physical member once.</summary>
internal sealed class BundleReadBudget(BundleReadLimits limits)
{
    private readonly Dictionary<ZipArchiveEntry, byte[]?> _members = [];
    private long _actualBytes;

    internal void AdmitArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > limits.ZipEntries)
            throw Invalid("archive has too many members");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        long compressed = 0;
        try
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!names.Add(entry.FullName))
                    throw Invalid($"duplicate archive member '{entry.FullName}'");
                long memberLimit = entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? limits.JsonBytes : limits.BodyBytes;
                if (entry.Length < 0 || entry.CompressedLength < 0)
                    throw Invalid($"member '{entry.FullName}' exceeds its admitted size");
                if (entry.Length > memberLimit)
                    throw Invalid(entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? $"member '{entry.FullName}' exceeds the maximum JSON size"
                        : $"'{entry.FullName}' exceeds the maximum raw NMP/2 body size");
                expanded = checked(expanded + entry.Length);
                compressed = checked(compressed + entry.CompressedLength);
                if (expanded > limits.ExpandedBytes || compressed > limits.CompressedFileBytes)
                    throw Invalid("archive exceeds its aggregate size budget");
            }
        }
        catch (OverflowException exception)
        {
            throw Invalid("archive size arithmetic overflowed", exception);
        }

        _members.Clear();
        _actualBytes = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
            _members.Add(entry, null);
    }

    internal byte[] Read(ZipArchiveEntry entry, long memberLimit)
    {
        if (!_members.TryGetValue(entry, out byte[]? cached))
            throw Invalid($"member '{entry.FullName}' was not admitted");
        if (entry.Length < 0 || entry.Length > memberLimit)
            throw Invalid($"member '{entry.FullName}' exceeds its admitted size");
        if (cached is not null)
            return cached;

        try
        {
            using Stream source = entry.Open();
            using var target = new MemoryStream();
            byte[] buffer = new byte[81920];
            long actual = 0;
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                actual = checked(actual + count);
                if (actual > memberLimit || actual > entry.Length
                    || actual > limits.ExpandedBytes - _actualBytes)
                    throw Invalid($"member '{entry.FullName}' exceeds its admitted size");
                target.Write(buffer, 0, count);
            }
            if (actual != entry.Length)
                throw Invalid($"member '{entry.FullName}' length does not match its declaration");

            _actualBytes = checked(_actualBytes + actual);
            byte[] result = target.ToArray();
            _members[entry] = result;
            return result;
        }
        catch (OverflowException exception)
        {
            throw Invalid("archive size arithmetic overflowed", exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            throw new DurableWriteException("read bundle", entry.FullName, exception);
        }
    }

    private static BundleValidationException Invalid(string reason, Exception? inner = null) =>
        new($"INVALID_BUNDLE: {reason}.", inner);
}
