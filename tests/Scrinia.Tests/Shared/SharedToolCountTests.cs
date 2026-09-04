using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedToolCountTests
{
    private static readonly string[] ExpectedNames =
    [
        "shared_bases", "shared_search", "shared_list", "shared_recall", "shared_facts",
        "shared_remember", "shared_append", "shared_forget", "shared_compact", "shared_link",
        "shared_refresh"
    ];

    [Fact]
    public void Registers_exactly_eleven_prefixed_methods_and_no_forbidden_methods()
    {
        var methods = typeof(SharedTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var toolNames = methods
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        toolNames.Should().Equal(ExpectedNames.OrderBy(name => name, StringComparer.Ordinal));
        methods.Should().HaveCount(ExpectedNames.Length);
        methods.All(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null).Should().BeTrue();
        methods.SelectMany(method => method.GetCustomAttributes<DescriptionAttribute>()).Should().NotBeEmpty();
    }
}
