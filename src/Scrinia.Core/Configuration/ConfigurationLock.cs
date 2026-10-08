using System.Security.Cryptography;
using System.Text;

namespace Scrinia.Core.Configuration;

internal static class ConfigurationLock
{
    public static string GetPath(string configPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        string identity = Path.GetFullPath(configPath);
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        string key = Convert.ToHexString(digest);
        return Path.Combine(Path.GetTempPath(), "scrinia-config-locks", key + ".lock");
    }
}
