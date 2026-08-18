using System.Reflection;
using FluentAssertions;
using Scrinia.Server.Auth;

namespace Scrinia.Server.Tests.Auth;

public class PermissionVocabularyTests
{
    [Fact]
    public void All_ContainsEveryDeclaredConstant()
    {
        var declared = typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        // Guards the hazard this class exists to remove: a constant added without
        // being added to All would be invisible to every coverage check built on All.
        Permissions.All.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void NoServerSourceFileUsesABarePermissionLiteral()
    {
        // Enumerates from the source tree, not from a list of known call sites.
        string serverRoot = TestPaths.RepoRoot("src", "Scrinia.Server");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.EndsWith("Permissions.cs", StringComparison.Ordinal))
                continue;

            string text = File.ReadAllText(file);
            foreach (string perm in Permissions.All)
                if (text.Contains($"\"{perm}\"", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: \"{perm}\"");
        }

        offenders.Should().BeEmpty(
            "permission names must come from the Permissions constants so a rename is a compile error");
    }
}
