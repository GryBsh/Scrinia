using System.Text;
using System.Text.Json;

namespace Scrinia.TestProcess;

internal static class TestProcessProtocol
{
    internal static async Task WriteEventAsync(string eventName)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event", eventName);
            writer.WriteEndObject();
        }
        await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    internal static async Task WaitForContinueAsync(CancellationToken ct)
    {
        string? command = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
        if (!string.Equals(command, "continue", StringComparison.Ordinal))
            throw new InvalidDataException($"Expected control line 'continue', received '{command ?? "<EOF>"}'.");
    }
}
