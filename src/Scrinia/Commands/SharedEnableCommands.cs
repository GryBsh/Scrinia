using System.Text.Json;
using ConsoleAppFramework;
using Scrinia.Core.Configuration;

namespace Scrinia.Commands;

/// <summary>CLI adapter for enabling and disabling registered shared bases.</summary>
public sealed class SharedEnableCommands
{
    public int Enable([Argument] string name, bool json = false) => Render("shared enable", Operations().Enable(name), json);
    public int Disable([Argument] string name, bool json = false) => Render("shared disable", Operations().Disable(name), json);

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
        else if (result.Succeeded) Console.WriteLine(command);
        else Console.Error.WriteLine($"{error}: {result.Exception?.Message}");
        return (int)result.ExitClass;
    }
}
