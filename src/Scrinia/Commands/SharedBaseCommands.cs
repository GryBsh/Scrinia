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
        if (json)
        {
            var output = ConfigurationJsonResult.Success("shared base list", false,
                bases: bases.Select(baseEntry => new SharedBaseJsonItem(baseEntry.Name, baseEntry.Root, "valid")).ToArray());
            Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.ConfigurationJsonResult));
        }
        else foreach (SharedBaseEntry entry in bases) Console.WriteLine($"{entry.Name}\t{entry.Root}\tproject-git");
        return 0;
    }

    private static SharedBaseOperations Operations()
    {
        WorkspaceSetup.Configure(null);
        string home = UserHome.Resolve();
        return new SharedBaseOperations(new SharedBaseRegistry(home, WorkspaceSetup.Root), home, WorkspaceSetup.Root);
    }

    internal static int Render(string command, SharedBaseResult result, bool json)
    {
        ConfigurationJsonResult output = result.Succeeded
            ? ConfigurationJsonResult.Success(command, result.Base is not null, @base: result.Base?.Name,
                manifest: command == "shared base add" ? [".scrinia/.gitignore"] : null,
                gitCommands: command == "shared base add" && result.Base is not null
                    ? TrackingCommands.RegistrationArgv(result.Base.Root, [".scrinia/.gitignore"])
                    : TrackingCommands.InspectionArgv(result.Base?.Root ?? WorkspaceSetup.Root))
            : ConfigurationJsonResult.Failure(command, result.ErrorCode, result.Exception?.Message);
        if (json) Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.ConfigurationJsonResult));
        else if (result.Succeeded) Console.WriteLine(result.Base is null ? command : $"{command}: {result.Base.Name}");
        else Console.Error.WriteLine($"{output.ErrorCode}: {result.Exception?.Message}");
        return (int)result.ExitClass;
    }
}
