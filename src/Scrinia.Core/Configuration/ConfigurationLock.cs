using System.Security.Cryptography;
using System.Text;

namespace Scrinia.Core.Configuration;

internal static class ConfigurationLock
{
    public static string GetPath(string configPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(configPath)));
        string key = Convert.ToHexString(digest);
        return Path.Combine(Path.GetTempPath(), "scrinia-config-locks", key + ".lock");
    }
}
