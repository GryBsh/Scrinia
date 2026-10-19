using Scrinia.Agent.Benchmarks;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
return await new BenchmarkApplication().RunAsync(args, cancellation.Token);
