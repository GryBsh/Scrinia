namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// Identity for a Scrinia plugin. Implement this interface (or extend
/// <see cref="ScriniaPluginBase"/>) in your plugin assembly.
///
    /// <para>Hosting hooks are intentionally not part of this identity contract. Plugins participate
    /// through the contributor, event-sink, embeddings-admin, and operation-hook interfaces below.</para>
///
/// <para><b>What a plugin actually reaches Scrinia through</b> is unchanged and lives elsewhere:
/// <c>ISearchScoreContributor</c>, <c>IMemoryEventSink</c> and <c>IEmbeddingsAdmin</c> in
/// <c>Scrinia.Core</c>, and <see cref="IMemoryOperationHook"/> here. A plugin implements those on
/// its own class; nothing needs a DI container to hand it over.</para>
/// </summary>
public interface IScriniaPlugin
{
    /// <summary>Display name of the plugin.</summary>
    string Name { get; }

    /// <summary>Semantic version string.</summary>
    string Version { get; }

}
