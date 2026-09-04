namespace Scrinia.Core.Configuration;

/// <summary>Coordinates shared-base filesystem initialization with registry mutations.</summary>
public sealed class SharedBaseOperations
{
    private readonly ISharedBaseRegistry _registry;
    private readonly string _homeDirectory;
    private readonly string _workspaceRoot;

    public SharedBaseOperations(ISharedBaseRegistry registry, string homeDirectory, string workspaceRoot)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _homeDirectory = Path.GetFullPath(homeDirectory ?? throw new ArgumentNullException(nameof(homeDirectory)));
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    }

    public SharedBaseResult Add(string name, string root)
    {
        if (!SharedBaseRegistry.IsCanonicalName(name))
            return Failure(ConfigurationErrorCode.CliInvalidArgument, "The shared base name is not canonical.", ConfigurationExitClass.InvalidConfiguration);

        SharedBaseEntry? existing = _registry.List(includeDisabled: true).FirstOrDefault(item => item.Name == name);
        if (existing is not null && !SamePhysical(existing.Root, root))
            return Failure(ConfigurationErrorCode.SharedBaseAlreadyExists, "The shared base name is already registered.");

        SharedBaseResult validationResult = _registry is SharedBaseRegistry concreteRegistry
            ? concreteRegistry.ValidateRoot(root)
            : ValidateArgumentRoot(root);
        if (!validationResult.Succeeded)
            return validationResult;
        string canonicalRoot = validationResult.Base!.Root;

        using FileLock _ = FileLock.AcquireExclusive(ConfigurationLock.GetPath(
            Path.Combine(canonicalRoot, ".scrinia", ".gitignore")));

        string scriniaDirectory = Path.Combine(canonicalRoot, ".scrinia");
        string ignorePath = Path.Combine(scriniaDirectory, ".gitignore");
        bool directoryExisted = Directory.Exists(scriniaDirectory);
        byte[]? originalIgnore = File.Exists(ignorePath) ? File.ReadAllBytes(ignorePath) : null;
        bool changed = false;
        Exception? rollbackFailure;

        try
        {
            Directory.CreateDirectory(scriniaDirectory);
            string? current = originalIgnore is null ? null : System.Text.Encoding.UTF8.GetString(originalIgnore);
            ManagedIgnoreValidation validation = ManagedIgnoreBlock.Validate(current, TrackingPolicy.ProjectGit);
            if (existing is not null && validation != ManagedIgnoreValidation.Valid)
                return Failure(ConfigurationErrorCode.SharedTrackingPolicyDrift, "The shared base managed ignore block is missing or drifted.");

            string updated = existing is null && current is null && !File.Exists(ignorePath)
                ? ManagedIgnoreBlock.ProjectGit
                : ManagedIgnoreBlock.Apply(current, TrackingPolicy.ProjectGit);
            changed = !string.Equals(current, updated, StringComparison.Ordinal);
            if (changed)
                DurableFile.AtomicWriteAllText(ignorePath, updated);

            SharedBaseResult result;
            try
            {
                result = _registry.Add(name, canonicalRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ScriniaConfigurationException)
            {
                bool restored = RestoreAfterFailure(ignorePath, originalIgnore, scriniaDirectory, directoryExisted, out rollbackFailure);
                return restored
                    ? Failure(ConfigurationErrorCode.ConfigWriteFailed, "The shared base registry could not be committed.", ConfigurationExitClass.WriteFailure, exception)
                    : RecoveryRequired(exception, rollbackFailure);
            }
            if (result.Succeeded)
                return result;

            if (!RestoreAfterFailure(ignorePath, originalIgnore, scriniaDirectory, directoryExisted, out rollbackFailure))
                return RecoveryRequired(result.Exception ?? new ScriniaConfigurationException("The shared base registry rejected the operation."), rollbackFailure);
            return result;
        }
        catch (ScriniaConfigurationException exception) when (exception.InnerException is DurableWriteException)
        {
            return RestoreAfterFailure(ignorePath, originalIgnore, scriniaDirectory, directoryExisted, out rollbackFailure)
                ? Failure(ConfigurationErrorCode.ConfigWriteFailed, exception.Message, ConfigurationExitClass.WriteFailure, exception)
                : RecoveryRequired(exception, rollbackFailure);
        }
        catch (DurableWriteException exception)
        {
            return RestoreAfterFailure(ignorePath, originalIgnore, scriniaDirectory, directoryExisted, out rollbackFailure)
                ? Failure(ConfigurationErrorCode.ConfigWriteFailed, exception.Message, ConfigurationExitClass.WriteFailure, exception)
                : RecoveryRequired(exception, rollbackFailure);
        }
        catch (ScriniaConfigurationException exception)
        {
            return RestoreAfterFailure(ignorePath, originalIgnore, scriniaDirectory, directoryExisted, out rollbackFailure)
                ? Failure(ConfigurationErrorCode.SharedTrackingPolicyDrift, exception.Message, ConfigurationExitClass.PolicyFailure, exception)
                : RecoveryRequired(exception, rollbackFailure);
        }
        catch (IOException exception)
        {
            return RestoreAfterFailure(ignorePath, originalIgnore, scriniaDirectory, directoryExisted, out rollbackFailure)
                ? Failure(ConfigurationErrorCode.IoFailure, exception.Message, ConfigurationExitClass.WriteFailure, exception)
                : RecoveryRequired(exception, rollbackFailure);
        }
    }

    public SharedBaseResult Remove(string name) => _registry.Remove(name);
    public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) => _registry.List(includeDisabled);
    public SharedBaseResult Enable(string name) => _registry.Enable(name);
    public SharedBaseResult Disable(string name) => _registry.Disable(name);

    private static bool SamePhysical(string left, string right)
    {
        string normalized = SharedBaseRegistry.NormalizeRoot(right);
        return PathContainment.IsWithin(left, normalized) && PathContainment.IsWithin(normalized, left);
    }

    private static SharedBaseResult ValidateArgumentRoot(string root)
    {
        try
        {
            string normalized = SharedBaseRegistry.NormalizeRoot(root);
            return string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(normalized)
                ? Failure(ConfigurationErrorCode.SharedBaseUnsafePath, "The shared base root is invalid.", ConfigResults.ExitClassFor(ConfigurationErrorCode.SharedBaseUnsafePath))
                : new(Base: new SharedBaseEntry { Root = normalized, Tracking = TrackingPolicy.ProjectGit });
        }
        catch (ArgumentException exception)
        {
            return Failure(ConfigurationErrorCode.SharedBaseUnsafePath, exception.Message, ConfigResults.ExitClassFor(ConfigurationErrorCode.SharedBaseUnsafePath), exception);
        }
    }

    private static bool RestoreAfterFailure(string ignorePath, byte[]? original, string directory, bool existed, out Exception? rollbackFailure)
    {
        try
        {
            RestoreIgnore(ignorePath, original);
            RemoveNewDirectory(directory, existed);
            rollbackFailure = null;
            return true;
        }
        catch (Exception exception)
        {
            rollbackFailure = exception;
            return false;
        }
    }

    private static void RestoreIgnore(string path, byte[]? original)
    {
        if (original is null) { if (File.Exists(path)) File.Delete(path); return; }
        DurableFile.AtomicWriteAllBytes(path, original);
    }

    private static void RemoveNewDirectory(string directory, bool existed)
    {
        if (!existed && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static SharedBaseResult Failure(ConfigurationErrorCode code, string message, ConfigurationExitClass exit,
        Exception? exception = null) => new(code, exit, Exception: exception ?? new ScriniaConfigurationException(message));
    private static SharedBaseResult Failure(ConfigurationErrorCode code, string message) => Failure(code, message, ConfigurationExitClass.PolicyFailure);
    private static SharedBaseResult RecoveryRequired(Exception cause, Exception? rollbackFailure = null) => Failure(
        ConfigurationErrorCode.ConfigRecoveryRequired,
        "The shared base transaction could not restore its previous filesystem state; recovery is required.",
        ConfigurationExitClass.WriteFailure,
        rollbackFailure is null ? cause : new AggregateException(cause, rollbackFailure));
}
