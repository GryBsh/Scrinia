using System.Reflection;
using ConsoleAppFramework;
using Scrinia.Commands;
using Scrinia.Core;

if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
{
    string version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0] ?? "unknown";
    Console.WriteLine($"Scrinia {version} — Persistent, portable memory for AI agents.");
    Console.WriteLine("(c) Nick Daniels. Licensed under BSD-3-Clause.");
    Console.WriteLine();
}

var app = ConsoleApp.Create();
app.Add<ScriniaCommands>();
app.Add<BundleCommands>("bundle");
await app.RunAsync(args);

// Event sinks run detached so writes return promptly, which means a short-lived command can
// finish while its indexing is still in flight. Wait for it here rather than letting process
// exit abandon a half-written vector file.
if (!await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10)))
{
    Console.Error.WriteLine(
        "[scrinia:warn] Background indexing did not finish within 10s and was cancelled. " +
        "Some memories may not appear in semantic search until the next write or `scri reindex`.");
}
