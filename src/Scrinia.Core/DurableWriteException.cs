namespace Scrinia.Core;

/// <summary>Describes a failed durable single-file replacement.</summary>
public sealed class DurableWriteException : IOException
{
    public string Operation { get; }
    public string Destination { get; }
    public string? AppendOperationId { get; }

    public DurableWriteException(string operation, string destination, Exception innerException,
        string? appendOperationId = null)
        : base($"Durable {operation} failed for '{destination}'.", innerException)
    {
        Operation = operation;
        Destination = destination;
        AppendOperationId = appendOperationId;
    }
}
