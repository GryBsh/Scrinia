using System.Security.Cryptography;
using Scrinia.Core;

namespace Scrinia.Commands;

internal enum EmbeddingsInstallPhase { BeforeCopy, AfterCopy, BeforeRename, AfterRename, BeforeDirectorySync }

/// <summary>Publishes one immutable payload under a cooperating, per-home process lock.</summary>
internal static class EmbeddingsBundleInstaller
{
    private static readonly AsyncLocal<Func<EmbeddingsInstallPhase, string, Exception?>?> Injector = new();
    internal static Func<EmbeddingsInstallPhase, string, Exception?>? FailureInjector
    {
        get => Injector.Value;
        set => Injector.Value = value;
    }

    internal static EmbeddingsInstallResult Install(string sourceRoot, string homeRoot, string hostVersion,
        string rid, CancellationToken ct = default) => Install(sourceRoot, homeRoot, hostVersion, rid,
            path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ct);

    // Call-local real-stream seam for deterministic cancellation during validation and verification.
    internal static EmbeddingsInstallResult Install(string sourceRoot, string homeRoot, string hostVersion,
        string rid, Func<string, FileStream> openRead, CancellationToken ct)
    {
        string? destination = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var observed = EmbeddingsBundleValidator.Validate(sourceRoot, hostVersion, rid, File.GetAttributes, openRead, ct);
            ct.ThrowIfCancellationRequested();
            // Resolve caller-supplied ancestor aliases once, then refuse all descendant aliases.
            string home = CanonicalHome(homeRoot);
            string intendedParent = Path.Combine(home, "plugins", observed.Manifest.Plugin, observed.Manifest.PluginVersion);
            if (IsWithin(observed.Root, intendedParent) || IsWithin(Path.Combine(intendedParent, rid), observed.Root))
                throw new IOException("Installation mutation paths cannot overlap the source bundle.");
            EnsureDirectory(home, home);
            string plugins = Path.Combine(home, "plugins");
            EnsureDirectory(home, plugins);
            string lockPath = Path.Combine(plugins, ".scri-plugin-embeddings.install.lock");
            CheckPath(home, lockPath, directory: false, allowMissing: true);
            using var held = FileLock.AcquireExclusive(lockPath, TimeSpan.FromSeconds(30), ct);
            CheckPath(home, lockPath, directory: false);
            // The first validation is an observation, never authority for later filesystem access.
            CheckPath(observed.Root, observed.Root, directory: true);
            var bundle = EmbeddingsBundleValidator.Validate(observed.Root, hostVersion, rid, File.GetAttributes, openRead, ct);
            if (!string.Equals(bundle.Root, observed.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Canonical source root changed during admission.");
            CheckPath(observed.Root, observed.Root, directory: true);
            if (IsWithin(bundle.Root, intendedParent) || IsWithin(Path.Combine(intendedParent, rid), bundle.Root))
                throw new IOException("Accepted source overlaps installation mutation paths.");
            string parent = Path.Combine(plugins, bundle.Manifest.Plugin, bundle.Manifest.PluginVersion);
            destination = Path.Combine(parent, rid);
            EnsureDirectory(home, parent);
            using var sources = OpenSources(bundle, openRead, ct);
            bool exists;
            try { exists = CheckPath(home, destination, directory: true, allowMissing: true); }
            catch (Exception error) when (Expected(error))
            { return new(EmbeddingsInstallStatus.ExistingContentConflict, destination, "Installed destination has an unsafe identity: " + error.Message); }
            if (exists)
            {
                try { VerifyPayload(home, destination, bundle.Manifest, sources.Modes, allowState: true, openRead, ct); }
                catch (Exception error) when (error is not OperationCanceledException && Expected(error))
                { return new(EmbeddingsInstallStatus.ExistingContentConflict, destination, "Installed runtime conflicts with the bundle: " + error.Message); }
                SyncPublication(home, destination, bundle.Manifest, sources.Modes, openRead);
                return new(EmbeddingsInstallStatus.Success, destination, "Identical embeddings payload is installed.", true);
            }

            string staging = Path.Combine(parent, "." + rid + ".staging-" + Guid.NewGuid().ToString("N"));
            bool renameAttempted = false;
            var created = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                ct.ThrowIfCancellationRequested();
                CheckPath(home, parent, directory: true);
                if (CheckPath(home, staging, directory: true, allowMissing: true)) throw new IOException("Staging directory already exists.");
                Directory.CreateDirectory(staging);
                Sync(home, staging);
                byte[] buffer = new byte[128 * 1024];
                foreach (var record in bundle.Manifest.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    string sourcePath = Path.Combine(bundle.PayloadDirectory, record.Name);
                    ThrowInjected(EmbeddingsInstallPhase.BeforeCopy, sourcePath);
                    ct.ThrowIfCancellationRequested();
                    // Recheck the name as well as the stable handle. Replacement cannot change the
                    // bytes read through the handle; changed visible bytes must not be accepted either.
                    CheckPath(bundle.Root, sourcePath, directory: false);
                    VerifyFile(bundle.Root, sourcePath, record, sources.Modes[record.Name], openRead, ct);
                    string targetPath = Path.Combine(staging, record.Name);
                    CheckPath(home, targetPath, directory: false, allowMissing: true);
                    using (var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created.Add(record.Name);
                        FileStream source = sources.Streams[record.Name];
                        source.Position = 0;
                        HashStream(source, record, buffer, ct, target);
                        if (!OperatingSystem.IsWindows())
                            File.SetUnixFileMode(target.SafeFileHandle, sources.Modes[record.Name]!.Value);
                        target.Flush(flushToDisk: true);
                    }
                    ThrowInjected(EmbeddingsInstallPhase.AfterCopy, targetPath);
                }
                VerifyPayload(home, staging, bundle.Manifest, sources.Modes, allowState: false, openRead, ct);
                // Persist entries within staging, then publish its name in the parent directory.
                Sync(home, Path.Combine(staging, bundle.Manifest.Files[0].Name));
                ThrowInjected(EmbeddingsInstallPhase.BeforeRename, staging);
                ct.ThrowIfCancellationRequested();
                VerifyPayload(home, staging, bundle.Manifest, sources.Modes, allowState: false, openRead, ct);
                CheckPath(home, destination, directory: true, allowMissing: true);
                CheckPath(home, staging, directory: true);
                renameAttempted = true;
                Directory.Move(staging, destination);
                ThrowInjected(EmbeddingsInstallPhase.AfterRename, destination);
                // Once rename may have committed, cancellation cannot revoke publication.
                SyncPublication(home, destination, bundle.Manifest, sources.Modes, openRead);
                return new(EmbeddingsInstallStatus.Success, destination, "Embeddings payload installed.");
            }
            catch (Exception error) when (Expected(error))
            {
                if (renameAttempted)
                    return new(EmbeddingsInstallStatus.InstallationFailed, destination,
                        "Installation outcome requires retry; final and staging paths retained at '" + destination + "' and '" + staging + "'. " + error.Message);
                bool cleaned = TryCleanStaging(home, staging, created);
                return new(EmbeddingsInstallStatus.InstallationFailed, null,
                    "Installation failed. " + (cleaned ? "" : "Staging retained at '" + staging + "'; inspect before retry. ") + error.Message);
            }
        }
        catch (EmbeddingsBundleException error) { return new(error.Status, destination, error.Message); }
        catch (Exception error) when (Expected(error))
        {
            return new(EmbeddingsInstallStatus.InstallationFailed, destination,
                "Installation failed. " + (destination is null ? "" : "Any existing payload is retained at '" + destination + "'; retry installation. ") + error.Message);
        }
    }

    private static SourceFiles OpenSources(ValidatedEmbeddingsBundle bundle, Func<string, FileStream> openRead, CancellationToken ct)
    {
        var result = new SourceFiles();
        try
        {
            foreach (var record in bundle.Manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Combine(bundle.PayloadDirectory, record.Name);
                CheckPath(bundle.Root, path, directory: false);
                var stream = openRead(path);
                result.Streams.Add(record.Name, stream);
                result.Modes.Add(record.Name, OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(stream.SafeFileHandle));
                HashStream(stream, record, new byte[128 * 1024], ct);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void HashStream(FileStream input, EmbeddingsBundleFile record, byte[] buffer,
        CancellationToken ct, FileStream? target = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = input.Read(buffer);
            ct.ThrowIfCancellationRequested();
            if (count == 0) break;
            length = checked(length + count);
            if (length > record.Length) throw new InvalidDataException("Runtime exceeds its declared length.");
            hash.AppendData(buffer, 0, count);
            target?.Write(buffer, 0, count);
        }
        if (length != record.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime length or SHA-256 differs from the captured manifest.");
    }

    private static void VerifyFile(string root, string path, EmbeddingsBundleFile record, UnixFileMode? mode,
        Func<string, FileStream> openRead, CancellationToken ct)
    {
        CheckPath(root, path, directory: false);
        using var stream = openRead(path);
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(stream.SafeFileHandle) != mode)
            throw new InvalidDataException("Runtime Unix permissions differ from the source.");
        HashStream(stream, record, new byte[128 * 1024], ct);
    }

    private static void VerifyPayload(string home, string payload, EmbeddingsBundleManifest manifest,
        Dictionary<string, UnixFileMode?> modes, bool allowState, Func<string, FileStream> openRead, CancellationToken ct)
    {
        CheckPath(home, payload, directory: true);
        var remaining = manifest.Files.ToDictionary(file => file.Name, StringComparer.Ordinal);
        foreach (string entry in Directory.EnumerateFileSystemEntries(payload))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            if (allowState && name == "state") { VerifyState(home, entry, ct); continue; }
            if (!remaining.Remove(name, out var record)) throw new InvalidDataException("Unexpected runtime member.");
            VerifyFile(home, entry, record, modes[name], openRead, ct);
        }
        if (remaining.Count != 0) throw new InvalidDataException("Missing runtime member.");
    }

    private static void VerifyState(string home, string state, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(state);
        while (pending.TryPop(out string? directory))
        {
            ct.ThrowIfCancellationRequested();
            CheckPath(home, directory, directory: true);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                bool isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                CheckPath(home, entry, isDirectory);
                if (isDirectory) pending.Push(entry);
            }
        }
    }

    private static void SyncPublication(string home, string destination, EmbeddingsBundleManifest manifest,
        Dictionary<string, UnixFileMode?> modes, Func<string, FileStream> openRead)
    {
        try { Sync(home, destination); }
        catch (Exception error) when (Expected(error))
        {
            VerifyPayload(home, destination, manifest, modes, allowState: true, openRead, CancellationToken.None);
            Sync(home, destination);
        }
    }

    private static void Sync(string home, string path)
    {
        ThrowInjected(EmbeddingsInstallPhase.BeforeDirectorySync, path);
        CheckPath(home, Path.GetDirectoryName(path)!, directory: true);
        DurableFile.SyncParentDirectory(path);
        CheckPath(home, Path.GetDirectoryName(path)!, directory: true);
    }

    private static void EnsureDirectory(string home, string directory)
    {
        bool exists = CheckPath(home, directory, directory: true, allowMissing: true);
        string parent = Path.GetDirectoryName(directory)!;
        if (directory != home) EnsureDirectory(home, parent);
        else if (!exists) CreateHomeAncestors(parent);
        if (!exists) Directory.CreateDirectory(directory);
        CheckPath(home, directory, directory: true);
        // Home's parent lies outside the descendant boundary, but CanonicalHome resolved it.
        if (directory == home)
        {
            ThrowInjected(EmbeddingsInstallPhase.BeforeDirectorySync, directory);
            DurableFile.SyncParentDirectory(directory);
        }
        else Sync(home, directory);
    }

    private static void CreateHomeAncestors(string path)
    {
        string volume = Path.GetPathRoot(path)!;
        if (!CheckPath(volume, path, directory: true, allowMissing: true))
        {
            CreateHomeAncestors(Path.GetDirectoryName(path) ?? throw new IOException("Home has no existing ancestor."));
            Directory.CreateDirectory(path);
        }
        CheckPath(volume, path, directory: true);
        // The nearest existing ancestor may be a prior attempt's unsynchronized creation.
        if (Path.GetDirectoryName(path) is not null) DurableFile.SyncParentDirectory(path);
    }

    private static string CanonicalHome(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new IOException("Home must be an absolute directory path.");
        string full = Path.GetFullPath(root);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(next); }
            catch (FileNotFoundException) { current = next; continue; }
            catch (DirectoryNotFoundException) { current = next; continue; }
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Home ancestor is not a directory.");
            current = (attributes & FileAttributes.ReparsePoint) == 0 ? next :
                new DirectoryInfo(next).ResolveLinkTarget(true)?.FullName ?? throw new IOException("Home alias cannot be resolved.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool CheckPath(string root, string path, bool directory, bool allowMissing = false)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Installation path escaped its root.");
        // Caller aliases were resolved before admission. An ancestor replaced afterward must
        // not silently redirect the canonical root while its leaf still looks like a directory.
        string ancestor = Path.GetPathRoot(root)!;
        foreach (string part in root[ancestor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            ancestor = Path.Combine(ancestor, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(ancestor); }
            catch (FileNotFoundException) when (allowMissing) { return false; }
            catch (DirectoryNotFoundException) when (allowMissing) { return false; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException("Canonical root ancestor changed identity.");
        }
        string current = root;
        string[] parts = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar);
        for (int index = -1; index < parts.Length; index++)
        {
            if (index >= 0) current = Path.Combine(current, parts[index]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) when (allowMissing) { return false; }
            catch (DirectoryNotFoundException) when (allowMissing) { return false; }
            bool expectDirectory = index < parts.Length - 1 || directory;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != expectDirectory)
                throw new IOException("Installation path has an unsafe file identity.");
            if (!expectDirectory) RecoveryFileType.RequireRegularFile(current);
        }
        return true;
    }

    private static bool IsWithin(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool TryCleanStaging(string home, string staging, HashSet<string> created)
    {
        try
        {
            if (!CheckPath(home, staging, directory: true, allowMissing: true)) return true;
            string[] files = Directory.GetFileSystemEntries(staging);
            foreach (string file in files)
            {
                if (!created.Contains(Path.GetFileName(file))) return false;
                CheckPath(home, file, directory: false);
            }
            foreach (string file in files) { CheckPath(home, file, directory: false); File.Delete(file); }
            CheckPath(home, staging, directory: true);
            Directory.Delete(staging, recursive: false);
            Sync(home, staging);
            return true;
        }
        catch (Exception error) when (Expected(error)) { return false; }
    }

    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or OperationCanceledException or TimeoutException or OverflowException;

    private static void ThrowInjected(EmbeddingsInstallPhase phase, string path)
    {
        if (FailureInjector?.Invoke(phase, path) is { } error) throw error;
    }

    private sealed class SourceFiles : IDisposable
    {
        internal Dictionary<string, FileStream> Streams { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, UnixFileMode?> Modes { get; } = new(StringComparer.Ordinal);
        public void Dispose() { foreach (var stream in Streams.Values) stream.Dispose(); }
    }
}
