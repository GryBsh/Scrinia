using System.Runtime.CompilerServices;

namespace Scrinia.Agent.Benchmarks;

// Only messages explicitly authored at trusted validation boundaries reach the CLI.
internal static class InputDiagnostics
{
    private sealed record Diagnostic(string Message);
    private static readonly ConditionalWeakTable<Exception, Diagnostic> Messages = new();
    internal static T Mark<T>(T error, string message) where T : Exception
    {
        Messages.Add(error, new(message));
        return error;
    }
    internal static InvalidDataException Invalid(string message) => Mark(new InvalidDataException(message), message);
    internal static string? Get(Exception error) => Messages.TryGetValue(error, out var diagnostic) ? diagnostic.Message : null;
}
