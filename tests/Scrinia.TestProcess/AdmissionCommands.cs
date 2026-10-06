using Scrinia.Core;

namespace Scrinia.TestProcess;

internal static class AdmissionCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "hold") return 1;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var admission = new WorkspaceAdmission(args[1], recover: null);
        using var held = admission.EnterRestore(TimeSpan.FromSeconds(10), deadline.Token);
        await TestProcessProtocol.WriteEventAsync("ready").ConfigureAwait(false);
        await TestProcessProtocol.WaitForContinueAsync(deadline.Token).ConfigureAwait(false);
        await TestProcessProtocol.WriteEventAsync("completed").ConfigureAwait(false);
        return 0;
    }
}
