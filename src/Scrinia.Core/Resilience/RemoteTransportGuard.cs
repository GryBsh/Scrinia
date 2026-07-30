namespace Scrinia.Core.Resilience;

/// <summary>
/// Thrown when an API key would be transmitted over a non-TLS transport to a
/// non-loopback host and the caller has not opted in to insecure transport.
/// </summary>
public sealed class InsecureTransportException : Exception
{
    public InsecureTransportException(string message) : base(message) { }
}

/// <summary>
/// Guards remote CLI connections against sending a bearer API key in cleartext.
/// A key may cross the wire only over HTTPS, or to a loopback host (where it never
/// leaves the machine), or when the caller explicitly opts in to insecure transport.
/// </summary>
public static class RemoteTransportGuard
{
    /// <param name="remote">The remote server URL.</param>
    /// <param name="hasApiKey">True when a non-empty API key would be sent.</param>
    /// <param name="allowInsecure">True to downgrade a refusal to a warning.</param>
    /// <param name="warn">Sink for the one-line warning emitted on opt-in.</param>
    /// <exception cref="InsecureTransportException">
    /// A key would be sent over non-HTTPS to a non-loopback host without opt-in.
    /// </exception>
    public static void Validate(Uri remote, bool hasApiKey, bool allowInsecure, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(warn);

        if (!hasApiKey) return;
        if (string.Equals(remote.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return;
        if (remote.IsLoopback) return;

        string target = remote.GetLeftPart(UriPartial.Authority);
        if (allowInsecure)
        {
            warn($"[scrinia:warn] Sending the API key over an unencrypted connection to {target}. " +
                 "Use an https:// URL to protect it.");
            return;
        }

        throw new InsecureTransportException(
            $"Refusing to send the API key over an unencrypted connection to {target}. " +
            "Use an https:// URL, connect to a loopback host, or pass --allow-insecure-transport to override.");
    }
}
