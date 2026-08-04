using System.Runtime.CompilerServices;
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Drains <see cref="BackgroundWork"/> once, when the test host exits.
///
/// <para>The production equivalent lives in <c>Program.cs</c>; the test host has no such exit
/// path of its own, so without this a suite can finish with sink work still running against
/// stores and temp directories that no longer exist. That is the state the intermittent
/// "Test Run Aborted" was observed in — every test reported passing, then the host died during
/// shutdown.</para>
///
/// <para>Deliberately once-per-process rather than per-test: <see cref="BackgroundWork"/> is a
/// process-wide registry, so a per-test drain would serialize the whole parallel suite on
/// unrelated work. See the note in <c>TestHelpers.StoreScope.Dispose</c>.</para>
/// </summary>
internal static class TestHostLifetime
{
    [ModuleInitializer]
    internal static void Register()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                // Short budget: ProcessExit handlers are time-limited, and anything still
                // backing off at this point is writing to a deleted temp store anyway. The
                // drain cancels it, which is the point — abandoning it is what caused trouble.
                BackgroundWork.DrainAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // An exception escaping a ProcessExit handler terminates the process — which is
                // precisely the failure mode this file exists to prevent.
                Console.Error.WriteLine(
                    $"[scrinia:warn] Test host drain failed: {ex.GetType().Name}: {ex.Message}");
            }
        };
    }
}
