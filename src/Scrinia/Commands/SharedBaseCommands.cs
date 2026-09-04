using System.Text.Json;
using ConsoleAppFramework;
using Scrinia.Core.Configuration;

namespace Scrinia.Commands;

/// <summary>CLI adapter for the user-visible shared-base registry commands.</summary>
public sealed class SharedBaseCommands
{
    public int Add([Argument] string name, [Argument] string root, bool json = false) => Render("shared base add", Operations().Add(name, root), json);
    public int Remove([Argument] string name, bool json = false) => Render("shared base remove", Operations().Remove(name), json);

    public int List(bool all = false, bool json = false)
    {
        IReadOnlyList<SharedBaseEntry> bases = Operations().List(all);
        if (json) Console.WriteLine(JsonSerializer.Serialize(new CliSharedBaseListOutput(bases), CliJsonContext.Default.CliSharedBaseListOutput));
        else foreach (SharedBaseEntry entry in bases) Console.WriteLine($"{entry.Name}\t{entry.Root}\tproject-git");
        return 0;
    }

    private static SharedBaseOperations Operations()
    {
        WorkspaceSetup.Configure(null);
        string home = UserHome.Resolve();
        return new SharedBaseOperations(new SharedBaseRegistry(home, WorkspaceSetup.Root), home, WorkspaceSetup.Root);
    }

    private static int Render(string command, SharedBaseResult result, bool json)
    {
        string? error = result.Succeeded ? null : result.ErrorCode.ToString().ToUpperInvariant();
        if (json) Console.WriteLine(JsonSerializer.Serialize(new CliSharedBaseOutput(command, result.Base, error), CliJsonContext.Default.CliSharedBaseOutput));
        else if (result.Succeeded) Console.WriteLine(result.Base is null ? command : $"{command}: {result.Base.Name}");
        else Console.Error.WriteLine($"{error}: {result.Exception?.Message}");
        return (int)result.ExitClass;
    }
}

internal sealed record CliSharedBaseOutput(string Command, SharedBaseEntry? Base, string? ErrorCode);
internal sealed record CliSharedBaseListOutput(IReadOnlyList<SharedBaseEntry> Bases);
