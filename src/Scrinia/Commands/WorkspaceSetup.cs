using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Services;

namespace Scrinia.Commands;

internal static class WorkspaceSetup
{
    private static readonly AsyncLocal<string?> _rootOverride = new();
    private static string? _root;

    /// <summary>
    /// The resolved workspace root for this process, cached by <see cref="Configure"/>.
    /// Respects test overrides set via <see cref="OverrideRoot"/> (checked first, via
    /// <see cref="AsyncLocal{T}"/> so parallel test classes don't clobber each other),
    /// falling back to the process-wide value <see cref="Configure"/> cached.
    /// Replaces the former static workspace-root accessor.
    /// </summary>
    internal static string Root =>
        _rootOverride.Value ?? _root ?? throw new InvalidOperationException(
            "WorkspaceSetup.Configure must be called before reading Root.");

    /// <summary>Sets an async-local override for <see cref="Root"/>, scoped to the current
    /// async context. For tests that need root isolation under parallel execution; not
    /// wired into any production path.</summary>
    internal static void OverrideRoot(string? path) => _rootOverride.Value = path;

    /// <summary>
    /// The configured store for this process. Throws if <see cref="Configure"/> has not run.
    /// Resolves via <see cref="MemoryStoreContext.Current"/> ?? <see cref="MemoryStoreContext.Default"/>
    /// so the generic-host thread boundary does not lose the store.
    /// </summary>
    internal static IMemoryStore Store =>
        MemoryStoreContext.Current ?? MemoryStoreContext.Default ?? throw new InvalidOperationException(
            "WorkspaceSetup.Configure must be called before reading Store.");

    internal static void Configure(string? workspaceRoot)
        => Configure(workspaceRoot, null);

    internal static void Configure(string? workspaceRoot, IMultiMemoryParticipantAdapterFactory? participantAdapterFactory)
    {
        string root = WorkspaceRoot.Resolve(workspaceRoot, Directory.GetCurrentDirectory());
        _root = root;

        // Build ranker weights from config so a workspace can tune α_recency without
        // recompiling. Defaults (RankerOptions.Default) match the historical composition —
        // see RankerOptions docstring for the formula and calibration notes.
        var rankerOptions = RankerOptions.FromConfig(GetConfigValue);
        SearchCandidateOptions.Default = SearchCandidateOptions.Parse(
            GetConfigValue(SearchCandidateOptions.ConfigurationKey));
        var store = StoreFactory.Create(root, rankerOptions, participantAdapterFactory);
        MemoryStoreContext.Current = store;
        MemoryStoreContext.Default = store;
    }

    /// <summary>
    /// Resolves a plugin executable name, falling back to <paramref name="defaultName"/>.
    ///
    /// <para>The resolution order — both environment forms, then the workspace config file —
    /// lives in <see cref="GetConfigValue"/> and is not repeated here. This method used to carry
    /// its own copy, and when the double-underscore lookup was repaired the fix had to be applied
    /// to both; a rule with two homes is the shape that let the broken form ship documented in the
    /// first place.</para>
    /// </summary>
    internal static string GetPluginName(string key, string defaultName) =>
        GetConfigValue(key) ?? defaultName;

    /// <summary>
    /// Reads config from environment variables, then falls back to the workspace config file.
    /// A colon-separated key (e.g. "Scrinia:Embeddings:Provider") is accepted in two environment
    /// forms: single-underscore (SCRINIA_EMBEDDINGS_PROVIDER), which is the historically documented
    /// one and keeps priority, and the .NET-standard double-underscore form
    /// (SCRINIA__EMBEDDINGS__PROVIDER), which is what a .NET configuration host would read
    /// natively and is accepted here so the two spellings never disagree.
    /// </summary>
    internal static string? GetConfigValue(string key) => GetConfigValue(key, null);

    internal static string? GetConfigValue(string key, string? defaultValue)
    {
        if (key == "Scrinia:Shared:Enabled")
            return WorkspaceConfig.GetValue(Root, key) ?? defaultValue;

        return GetEnvironmentValue(key) ?? WorkspaceConfig.GetValue(Root, key) ?? defaultValue;
    }

    internal static string? GetEnvironmentValue(string key)
    {
        // 1. Environment variable, single-underscore form (highest priority)
        string envKey = key.Replace(':', '_').Replace("__", "_").ToUpperInvariant();
        string? value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        // 2. Environment variable, .NET-standard double-underscore form. This branch used to
        //    compute a string byte-identical to the one above for every key in the repository, so
        //    the documented form never resolved.
        envKey = key.Replace(":", "__").ToUpperInvariant();
        value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        return null;
    }
}
