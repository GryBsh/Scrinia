using System.Text;

namespace Scrinia.Core.Configuration;

public interface ISharedBaseRegistry
{
    SharedBaseResult Add(string name, string root);
    SharedBaseResult Remove(string name);
    IReadOnlyList<SharedBaseEntry> List(bool includeDisabled);
    SharedBaseResult Enable(string name);
    SharedBaseResult Disable(string name);
    SharedBaseEntry? Lookup(string name);
    IReadOnlyList<SharedBaseCollision> Collisions();
}

public sealed class SharedBaseRegistry : ISharedBaseRegistry
{
    private const string EnabledKey = WorkspaceConfig.EnabledKey;
    private readonly string _homeDirectory;
    private readonly string _workspaceRoot;
    private readonly IHomeConfigStore _homeConfigStore;
    private readonly IWorkspaceConfig _workspaceConfig;

    public SharedBaseRegistry(string homeDirectory, string workspaceRoot, IHomeConfigStore? homeConfigStore = null, IWorkspaceConfig? workspaceConfig = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        _homeDirectory = Path.GetFullPath(homeDirectory);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _homeConfigStore = homeConfigStore ?? new HomeConfigStore();
        _workspaceConfig = workspaceConfig ?? WorkspaceConfig.Load(_workspaceRoot);
    }

    public static bool IsCanonicalName(string name) =>
        name.Length is >= 1 and <= 32 && name[0] is >= 'a' and <= 'z' &&
        name[1..].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public SharedBaseResult ValidateRoot(string root)
    {
        if (!TryValidateRoot(root, out string canonicalRoot, out ConfigurationErrorCode errorCode))
            return Failure(errorCode, "The shared base root is invalid.");
        return new(Base: new SharedBaseEntry { Root = canonicalRoot, Tracking = TrackingPolicy.ProjectGit });
    }

    public SharedBaseResult Add(string name, string root)
    {
        if (!IsCanonicalName(name)) return Failure(ConfigurationErrorCode.CliInvalidArgument, "The shared base name is not canonical.", ConfigurationExitClass.InvalidConfiguration);
        if (!TryValidateRoot(root, out string canonicalRoot, out ConfigurationErrorCode errorCode))
            return Failure(errorCode, "The shared base root is invalid.");

        return WithLockedConfig(config =>
        {
            if (config.SharedBases.TryGetValue(name, out SharedBaseEntry? existing))
            {
                if (!SamePhysical(existing.Root, canonicalRoot))
                    return Failure(ConfigurationErrorCode.SharedBaseAlreadyExists, "The shared base name is already registered.");
                return ValidateCollisions(config, name, canonicalRoot) ?? Success(existing.WithIdentity(name));
            }

            SharedBaseCollision? collision = FindCollision(config, name, canonicalRoot);
            if (collision is not null) return Failure(ConfigurationErrorCode.SharedBaseRootCollision, "The shared base root collides with another root.");
            config.SharedBases[name] = new SharedBaseEntry { Root = canonicalRoot };
            SaveLocked(config);
            return Success(config.SharedBases[name].WithIdentity(name));
        });
    }

    public SharedBaseResult Remove(string name)
    {
        if (!IsCanonicalName(name)) return Failure(ConfigurationErrorCode.CliInvalidArgument, "The shared base name is not canonical.", ConfigurationExitClass.InvalidConfiguration);
        return WithLockedConfig(config =>
        {
            if (!config.SharedBases.Remove(name)) return new SharedBaseResult();
            SaveLocked(config);
            return new SharedBaseResult();
        });
    }

    public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled)
    {
        HomeConfig config = _homeConfigStore.Load(_homeDirectory);
        var enabled = _workspaceConfig.GetEnabledBases().ToHashSet(StringComparer.Ordinal);
        return config.SharedBases.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(pair => includeDisabled || enabled.Contains(pair.Key))
            .Select(pair => pair.Value.WithIdentity(pair.Key)).ToArray();
    }

    public SharedBaseResult Enable(string name) => SetEnabled(name, enabled => enabled.Contains(name) ? enabled : enabled.Append(name).ToArray());

    public SharedBaseResult Disable(string name)
    {
        if (!IsCanonicalName(name)) return Failure(ConfigurationErrorCode.CliInvalidArgument, "The shared base name is not canonical.", ConfigurationExitClass.InvalidConfiguration);
        return SetEnabled(name, enabled => enabled.Where(item => item != name).ToArray(), requireRegistration: false);
    }

    public SharedBaseEntry? Lookup(string name)
    {
        if (!IsCanonicalName(name) || !_workspaceConfig.GetEnabledBases().Contains(name, StringComparer.Ordinal)) return null;
        HomeConfig config = _homeConfigStore.Load(_homeDirectory);
        return config.SharedBases.TryGetValue(name, out SharedBaseEntry? entry) ? entry.WithIdentity(name) : null;
    }

    public IReadOnlyList<SharedBaseCollision> Collisions()
    {
        HomeConfig config = _homeConfigStore.Load(_homeDirectory);
        var collisions = new List<SharedBaseCollision>();
        SharedBaseEntry[] entries = config.SharedBases.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value.WithIdentity(pair.Key)).ToArray();
        for (int i = 0; i < entries.Length; i++)
            for (int j = i + 1; j < entries.Length; j++)
                if (RootsOverlap(entries[i].Root, entries[j].Root))
                    collisions.Add(new(entries[i].Name, entries[i].Root, entries[j].Name, entries[j].Root));
        return collisions;
    }

    private SharedBaseResult SetEnabled(string name, Func<IReadOnlyList<string>, IReadOnlyList<string>> mutate, bool requireRegistration = true)
    {
        if (!IsCanonicalName(name)) return Failure(ConfigurationErrorCode.SharedBaseUnsafePath, "The shared base name is not canonical.", ConfigurationExitClass.InvalidConfiguration);
        if (requireRegistration && LookupRegistered(name) is null) return Failure(ConfigurationErrorCode.SharedBaseNotFound, "The shared base is not registered.");
        _workspaceConfig.UpdateEnabledBases(mutate);
        SharedBaseEntry? entry = LookupRegistered(name);
        return entry is null ? new SharedBaseResult() : Success(entry.WithIdentity(name));
    }

    private SharedBaseEntry? LookupRegistered(string name)
    {
        HomeConfig config = _homeConfigStore.Load(_homeDirectory);
        return config.SharedBases.TryGetValue(name, out SharedBaseEntry? entry) ? entry : null;
    }

    private T WithLockedConfig<T>(Func<HomeConfig, T> action)
    {
        Directory.CreateDirectory(_homeDirectory);
        using FileLock _ = FileLock.AcquireExclusive(ConfigurationLock.GetPath(ConfigPath));
        return action(_homeConfigStore.Load(_homeDirectory));
    }

    private string ConfigPath => Path.Combine(_homeDirectory, "config.json");

    private void SaveLocked(HomeConfig config)
    {
        if (_homeConfigStore is HomeConfigStore store)
        {
            store.SaveLocked(ConfigPath, config);
            return;
        }

        _homeConfigStore.Save(_homeDirectory, config);
    }

    private bool TryValidateRoot(string root, out string canonicalRoot, out ConfigurationErrorCode errorCode)
    {
        canonicalRoot = "";
        errorCode = ConfigurationErrorCode.SharedBaseUnsafePath;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return false;
        canonicalRoot = NormalizeRoot(root);
        if (string.Equals(Path.GetFileName(canonicalRoot), ".scrinia", StringComparison.OrdinalIgnoreCase) ||
            PathContainment.IsWithin(_workspaceRoot, canonicalRoot))
        {
            errorCode = ConfigurationErrorCode.SharedBaseRootCollision;
            return false;
        }
        if (!PathContainment.IsWithin(canonicalRoot, Path.Combine(canonicalRoot, ".scrinia"))) return false;
        if (!HasGitEvidence(canonicalRoot))
        {
            errorCode = ConfigurationErrorCode.SharedBaseNotGitRoot;
            return false;
        }
        return true;
    }

    private static bool HasGitEvidence(string root)
    {
        string gitPath = Path.Combine(root, ".git");
        if (Directory.Exists(gitPath)) return true;
        if (!File.Exists(gitPath)) return false;
        byte[] bytes = File.ReadAllBytes(gitPath);
        if (bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })) return false;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { return false; }
        text = text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;
        if (!text.StartsWith("gitdir: ", StringComparison.Ordinal) || text.Contains('\n') || text.Contains('\r')) return false;
        string target = text[8..];
        if (string.IsNullOrWhiteSpace(target)) return false;
        return
            Directory.Exists(Path.IsPathFullyQualified(target) ? target : Path.Combine(root, target));
    }

    private SharedBaseResult? ValidateCollisions(HomeConfig config, string name, string root) =>
        FindCollision(config, name, root) is not null ? Failure(ConfigurationErrorCode.SharedBaseRootCollision, "The shared base root collides with another root.") : null;

    private static SharedBaseCollision? FindCollision(HomeConfig config, string name, string root) => config.SharedBases
        .Where(pair => pair.Key != name)
        .Select(pair => new { pair.Key, pair.Value.Root })
        .Where(pair => RootsOverlap(pair.Root, root))
        .Select(pair => new SharedBaseCollision(name, root, pair.Key, pair.Root)).FirstOrDefault();

    private static bool SamePhysical(string left, string right) => RootsOverlap(left, right) && PathContainment.IsWithin(left, right) && PathContainment.IsWithin(right, left);
    private static bool RootsOverlap(string left, string right) => PathContainment.IsWithin(left, right) || PathContainment.IsWithin(right, left);
    private static SharedBaseResult Success(SharedBaseEntry entry) => new(Base: entry);
    private static SharedBaseResult Failure(ConfigurationErrorCode code, string message, ConfigurationExitClass exit = ConfigurationExitClass.PolicyFailure) =>
        new(code, exit, Exception: new ScriniaConfigurationException(message));
}
