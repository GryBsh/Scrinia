namespace Scrinia.Core.Migration;

/// <summary>Retained tracking evidence could not be proven safe to use or discard.</summary>
internal sealed class TrackingRecoveryRequiredException(
    string message, string? journalPath = null, Exception? innerException = null)
    : IOException(message, innerException)
{
    internal string? JournalPath { get; } = journalPath;
}

