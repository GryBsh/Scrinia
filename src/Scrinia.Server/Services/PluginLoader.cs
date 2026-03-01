using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Server.Services;

/// <summary>
/// Discovers and loads plugin DLLs from a directory.
/// Each DLL gets an isolated <see cref="AssemblyLoadContext"/> that falls back
/// to the default context for shared assemblies (Core, Abstractions, ASP.NET).
/// </summary>
public sealed class PluginLoader
{
    public IReadOnlyList<IScriniaPlugin> LoadPlugins(string pluginsDir, ILogger logger)
    {
        var plugins = new List<IScriniaPlugin>();

        if (!Directory.Exists(pluginsDir))
        {
            logger.LogDebug("Plugin directory does not exist: {Dir}", pluginsDir);
            return plugins;
        }

        var dlls = Directory.GetFiles(pluginsDir, "*.dll");
        if (dlls.Length == 0)
        {
            logger.LogDebug("No plugin DLLs found in {Dir}", pluginsDir);
            return plugins;
        }

        foreach (string dll in dlls)
        {
            try
            {
                var alc = new PluginAssemblyLoadContext(dll);
                var assembly = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));

                foreach (var type in assembly.GetExportedTypes())
                {
                    if (!typeof(IScriniaPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                        continue;

                    if (Activator.CreateInstance(type) is IScriniaPlugin plugin)
                    {
                        plugins.Add(plugin);
                        logger.LogInformation("Loaded plugin: {Name} v{Version} (order={Order}) from {Dll}",
                            plugin.Name, plugin.Version, plugin.Order, Path.GetFileName(dll));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load plugin from {Dll}", Path.GetFileName(dll));
            }
        }

        plugins.Sort((a, b) => a.Order.CompareTo(b.Order));
        return plugins;
    }

    /// <summary>
    /// Isolated load context that falls back to the default for shared framework assemblies.
    /// </summary>
    private sealed class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _pluginDir;

        public PluginAssemblyLoadContext(string pluginPath)
            : base(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
            _pluginDir = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Let shared assemblies (Core, Abstractions, ASP.NET) resolve from Default
            if (assemblyName.Name is not null &&
                (assemblyName.Name.StartsWith("Scrinia.", StringComparison.Ordinal) ||
                 assemblyName.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                 assemblyName.Name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) ||
                 assemblyName.Name.StartsWith("System.", StringComparison.Ordinal)))
            {
                return null; // falls back to Default ALC
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            // Check plugin root directory first — accelerator-enabled native DLLs
            // (e.g. DirectML onnxruntime.dll) are placed here by dotnet publish.
            string candidate = Path.Combine(_pluginDir, unmanagedDllName);
            if (!candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                candidate += ".dll";
            if (File.Exists(candidate))
                return LoadUnmanagedDllFromPath(candidate);

            // Fallback: deps.json resolver (runtimes/{rid}/native/ paths)
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
        }
    }
}
