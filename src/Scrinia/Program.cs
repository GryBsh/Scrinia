using ConsoleAppFramework;
using Scrinia.Commands;

if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
{
    Console.WriteLine("Scrinia 0.4.0 — Persistent Memory for LLMs");
    Console.WriteLine("(c) Nick Daniels. Licensed under BSD-3-Clause.");
    Console.WriteLine();
}

var app = ConsoleApp.Create();
app.Add<ScriniaCommands>();
await app.RunAsync(args);
