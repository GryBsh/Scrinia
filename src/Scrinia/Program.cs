using ConsoleAppFramework;
using Scrinia.Commands;

var app = ConsoleApp.Create();
app.Add<ScriniaCommands>();
await app.RunAsync(args);
