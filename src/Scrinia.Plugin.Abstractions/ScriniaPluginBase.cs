namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// Convenience base class for plugins. It carried empty virtual bodies for the three
/// service-registration and endpoint-mapping members that <see cref="IScriniaPlugin"/> used to
/// declare; those went with the HTTP host that called them.
/// </summary>
public abstract class ScriniaPluginBase : IScriniaPlugin
{
    public abstract string Name { get; }
    public abstract string Version { get; }
    public virtual int Order => 0;
}
