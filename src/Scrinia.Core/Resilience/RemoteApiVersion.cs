namespace Scrinia.Core.Resilience;

/// <summary>
/// The Scrinia remote HTTP API's version contract — the single place both ends of the wire
/// reference, so the header name and expected value can never drift into two hand-matched string
/// literals (the exact class of defect the remote-write-correctness branch has spent the day
/// fixing elsewhere).
///
/// <para>Nothing is deployed over HTTP today — client and server always ship from the same commit
/// — so there is no real population of mixed-version servers to negotiate with. This exists purely
/// to fail closed the moment that stops being true: a server that doesn't understand a filter
/// parameter (<c>excludeTopics</c>, <c>pathPrefix</c>, …) silently ignores it rather than
/// rejecting it, so an unversioned or mismatched server must never be trusted implicitly.</para>
/// </summary>
public static class RemoteApiVersion
{
    /// <summary>Response header the server stamps on every response under the stores API group,
    /// and the client checks on every response it receives.</summary>
    public const string HeaderName = "X-Scrinia-Api-Version";

    /// <summary>The version this build of client and server both speak.</summary>
    public const string Current = "2";
}

/// <summary>
/// Thrown when a remote store response is missing <see cref="RemoteApiVersion.HeaderName"/> or
/// carries a version this client does not recognise. Both cases fail closed deliberately — a
/// server that simply forgets to stamp the header must not silently reopen the hole an unversioned
/// server represents (see <see cref="RemoteApiVersion"/>).
/// </summary>
public sealed class RemoteApiVersionMismatchException : Exception
{
    public RemoteApiVersionMismatchException(string message) : base(message) { }
}

/// <summary>Validates the API version header on a response from a Scrinia remote store.</summary>
public static class RemoteApiVersionGuard
{
    /// <summary>
    /// Checks <paramref name="response"/> for <see cref="RemoteApiVersion.HeaderName"/> and throws
    /// if it is absent or does not match <see cref="RemoteApiVersion.Current"/>. Call this on every
    /// response the remote HTTP store receives — both the synchronous and asynchronous send
    /// paths — before branching on status code, so a server that doesn't speak this contract is
    /// refused before its response (success or error) is trusted for anything.
    /// </summary>
    /// <exception cref="RemoteApiVersionMismatchException">
    /// The header is absent, or its value does not equal <see cref="RemoteApiVersion.Current"/>.
    /// </exception>
    public static void Validate(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Headers.TryGetValues(RemoteApiVersion.HeaderName, out var values))
        {
            throw new RemoteApiVersionMismatchException(
                $"Remote store did not send the '{RemoteApiVersion.HeaderName}' response header. " +
                "Refusing to trust a response from a server that does not identify its API version " +
                "— an older or unversioned server can silently ignore filter parameters (e.g. " +
                "excludeTopics, pathPrefix) instead of rejecting them, which would return an " +
                "unfiltered result set that looks filtered.");
        }

        string? actual = values.FirstOrDefault();
        if (!string.Equals(actual, RemoteApiVersion.Current, StringComparison.Ordinal))
        {
            throw new RemoteApiVersionMismatchException(
                $"Remote store API version mismatch: this client speaks '{RemoteApiVersion.Current}' " +
                $"but the server reported '{actual ?? "(none)"}' via '{RemoteApiVersion.HeaderName}'. " +
                "Refusing to trust its response rather than risk silently misinterpreting it.");
        }
    }
}
