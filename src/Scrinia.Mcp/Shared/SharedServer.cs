using Scrinia.Core.Configuration;
using Scrinia.Core.Models;
using Scrinia.Core;

namespace Scrinia.Mcp.SharedContracts;

public sealed record SharedBaseSelection(string Base, SharedBaseEntry? Entry);

public sealed class SharedAddressException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

/// <summary>Resolves the enabled shared namespace before any base-scoped work begins.</summary>
public sealed class SharedServer
{
    private readonly IWorkspaceConfig _workspaceConfig;
    private readonly ISharedBaseRegistry _registry;
    private readonly ISharedBaseRuntimeFactory _runtimeFactory;

    public SharedServer(IWorkspaceConfig workspaceConfig, ISharedBaseRegistry registry, ISharedBaseRuntimeFactory runtimeFactory)
    {
        _workspaceConfig = workspaceConfig ?? throw new ArgumentNullException(nameof(workspaceConfig));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
    }

    public SharedBaseSelection? SelectBase(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            return null;
        if (!SharedBaseRegistry.IsCanonicalName(baseName))
            throw new SharedAddressException("INVALID_PARAMETER", "The shared base name is not canonical.");
        if (!_workspaceConfig.GetEnabledBases().Contains(baseName, StringComparer.Ordinal))
            return null;

        SharedBaseEntry? entry = _registry.Lookup(baseName);
        return entry is null ? null : new SharedBaseSelection(baseName, entry);
    }

    public IReadOnlyList<SharedBaseSelection> SelectEnabledBases()
    {
        var selected = new List<SharedBaseSelection>();
        foreach (string baseName in _workspaceConfig.GetEnabledBases())
        {
            SharedBaseEntry? entry = _registry.Lookup(baseName);
            selected.Add(new SharedBaseSelection(baseName, entry));
        }
        return selected;
    }

    public IReadOnlyList<SharedBaseSummary> GetEnabledBaseSummaries() =>
        SelectEnabledBases()
            .Select(selection => CreateSummary(selection.Base, selection.Entry))
            .ToArray();

    public SharedBaseRuntime OpenSelectedBase(string baseName)
    {
        SharedBaseSelection selection = SelectBase(baseName)
            ?? throw new SharedAddressException("UNKNOWN_SHARED_BASE", "The selected shared base is unavailable.");
        if (selection.Entry is null)
            throw new SharedAddressException("UNKNOWN_SHARED_BASE", "The selected shared base is unavailable.");
        return _runtimeFactory.Open(selection.Entry);
    }

    public static MemoryAddress ParsePersistentAddress(string rawAddress)
    {
        MemoryAddress address;
        try
        {
            address = MemoryAddress.Parse(rawAddress);
        }
        catch (MemoryAddressException exception)
        {
            throw new SharedAddressException("MALFORMED_ADDRESS", exception.Message);
        }

        if (address.Kind == MemoryAddressKind.Ephemeral)
            throw new SharedAddressException("SHARED_EPHEMERAL_NOT_SUPPORTED", "Shared bases accept persistent addresses only.");

        return address;
    }

    public ParsedPath ParsePersistentPath(string rawPath)
    {
        MemoryAddress address = ParsePersistentAddress(rawPath);
        return PathParser.Parse(address.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private SharedBaseSummary CreateSummary(string baseName, SharedBaseEntry? entry)
    {
        if (entry is null)
            return new SharedBaseSummary(baseName, null, SharedAvailability.Unavailable, null);

        return new SharedBaseSummary(baseName, entry.Root, SharedAvailability.Unavailable, null);
    }
}
