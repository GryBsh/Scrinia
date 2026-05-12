using System.Reflection;
using ConsoleAppFramework;
using Scrinia.Commands;

if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
{
    string version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0] ?? "unknown";
    Console.WriteLine($"Scrinia {version} — Cognitive toolkit for LLMs.");
    Console.WriteLine("(c) Nick Daniels. Licensed under BSD-3-Clause.");
    Console.WriteLine();
}

var app = ConsoleApp.Create();
app.Add<ScriniaCommands>();
await app.RunAsync(args);
