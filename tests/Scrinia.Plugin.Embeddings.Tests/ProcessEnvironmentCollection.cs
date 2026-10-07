using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>Serializes process environment writers within this test assembly.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection definition; preserve the shared ProcessEnvironment fixture contract.")]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}