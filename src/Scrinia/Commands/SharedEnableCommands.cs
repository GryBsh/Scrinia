using System.Text.Json;
using ConsoleAppFramework;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;

namespace Scrinia.Commands;

/// <summary>CLI adapter for enabling and disabling registered shared bases.</summary>
public sealed class SharedEnableCommands
{
    public int Enable([Argument] string name, bool json = false) => Run("shared enable", () => Operations().Enable(name), json);
    public int Disable([Argument] string name, bool json = false) => Run("shared disable", () => Operations().Disable(name), json);

    private static int Run(string command, Func<SharedBaseResult> operation, bool json)
    {
        try { return Render(command, operation(), json); }
        catch (TrackingRecoveryRequiredException exception)
        {
            ConfigurationResult result = ConfigResults.FromException(exception);
            return Render(command, new SharedBaseResult(result.ErrorCode, result.ExitClass, Exception: exception), json);
        }
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
            ? ConfigurationJsonResult.Success(command, result.Base is not null, @base: result.Base?.Name)
            : ConfigurationJsonResult.Failure(command, result.ErrorCode, result.Exception?.Message);
        if (json) Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.ConfigurationJsonResult));
        else if (result.Succeeded) Console.WriteLine(command);
        else Console.Error.WriteLine($"{output.ErrorCode}: {result.Exception?.Message}");
        return (int)result.ExitClass;
    }
}
