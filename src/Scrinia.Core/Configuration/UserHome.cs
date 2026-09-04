namespace Scrinia.Core.Configuration;

/// <summary>Resolves the one user-level home directory used by Scrinia.</summary>
public static class UserHome
{
    public static string Resolve() =>
        Resolve(Environment.GetEnvironmentVariable("SCRINIA_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string Resolve(string? scriniaHome, string localApplicationData)
    {
        if (scriniaHome is not null)
        {
            if (scriniaHome.Length == 0 || !Path.IsPathFullyQualified(scriniaHome))
                throw new ScriniaConfigurationException("SCRINIA_HOME must be a non-empty absolute path.");

            return scriniaHome;
        }

        if (string.IsNullOrEmpty(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
            throw new ScriniaConfigurationException("LocalApplicationData is not a usable absolute path.");

        return Path.Combine(localApplicationData, "Scrinia");
    }
}

public sealed class ScriniaConfigurationException : Exception
{
    public ScriniaConfigurationException(string message) : base(message) { }
    public ScriniaConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}
