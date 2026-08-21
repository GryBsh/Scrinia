using System.Text.Json;

namespace Scrinia.Commands;

internal static class WorkspaceConfig
{
    private const string ConfigFileName = "config.json";

    private static string GetConfigPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".scrinia", ConfigFileName);

    /// <summary>
    /// Cache-on-first-read, invalidated by a write through this class. Keyed by workspace root,
    /// because one process resolves a root per invocation and the test suite drives many.
    /// </summary>
    /// <remarks>
    /// Every <see cref="GetValue"/> used to re-open, re-read and re-deserialize the file:
    /// 10 times per <c>scri hint</c>, 20+ per <c>scri search</c>, on a file that is 91 bytes in
    /// this workspace — and <c>hint</c> fires on every single user prompt.
    /// <para>The window this opens, stated plainly: a config change made by ANOTHER process is
    /// not seen until this one restarts. That is free for the CLI, where every invocation is a
    /// fresh process, and it is why the hot path pays nothing for it. It is real for a
    /// long-lived <c>scri serve</c>, where the remedy is a restart — the same remedy that
    /// already applies to every other startup-resolved setting.</para>
    /// <para>An mtime check would close that window at the cost of a stat per call. Not taken:
    /// the point is to stop hammering the file, and a stat per key is the same shape of
    /// repeated small read, just cheaper.</para>
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns a COPY. Callers mutate the result (<see cref="SetValue"/> does), and
    /// handing out the cached instance would let a caller edit the cache without writing the
    /// file — the aliasing footgun this codebase has already paid for once, on the index.</summary>
    internal static Dictionary<string, string> Load(string workspaceRoot) =>
        new(Cached(workspaceRoot), StringComparer.OrdinalIgnoreCase);

    /// <summary>The cached instance itself. Read-only callers use this rather than
    /// <see cref="Load"/>: a lookup does not need a copy of the whole map, and this is the hot
    /// path — <c>hint</c> asks for ten keys on every user prompt.</summary>
    private static Dictionary<string, string> Cached(string workspaceRoot) =>
        Cache.GetOrAdd(workspaceRoot, static root => ReadFromDisk(root));

    private static Dictionary<string, string> ReadFromDisk(string workspaceRoot)
    {
        string path = GetConfigPath(workspaceRoot);
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.DictionaryStringString);
        if (raw is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
    }

    internal static void Save(string workspaceRoot, Dictionary<string, string> config)
    {
        string path = GetConfigPath(workspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.DictionaryStringString);
        File.WriteAllText(path, json);

        // The write IS the invalidation. Repopulating from the value just persisted keeps the
        // next read off disk without letting the cache and the file disagree.
        Cache[workspaceRoot] = new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase);
    }

    internal static string? GetValue(string workspaceRoot, string key) =>
        Cached(workspaceRoot).TryGetValue(key, out var value) ? value : null;

    internal static void SetValue(string workspaceRoot, string key, string value)
    {
        var config = Load(workspaceRoot);
        config[key] = value;
        Save(workspaceRoot, config);
    }

    internal static bool UnsetValue(string workspaceRoot, string key)
    {
        var config = Load(workspaceRoot);
        if (!config.Remove(key))
            return false;
        Save(workspaceRoot, config);
        return true;
    }
}
