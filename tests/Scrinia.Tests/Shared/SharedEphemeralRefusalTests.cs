using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Core.Models;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedEphemeralRefusalTests
{
    [Theory]
    [InlineData("~name")]
    [InlineData("/temp/name")]
    public void Ephemeral_path_destination_and_prefix_are_refused_before_store_or_fingerprint_for_every_supported_spelling(string raw)
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-task3-ephemeral-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".scrinia"));
        try
        {
            var entry = new SharedBaseEntry { Name = "alpha", Root = root };
            var server = new SharedServer(
                new WorkspaceConfig(new Dictionary<string, string> { [WorkspaceConfig.EnabledKey] = "alpha" }),
                new SingleEntryRegistry(entry),
                new SharedBaseRuntimeFactory());

            Action parse = () => SharedServer.ParsePersistentAddress(raw);

            parse.Should().Throw<SharedAddressException>()
                .Which.Code.Should().Be("SHARED_EPHEMERAL_NOT_SUPPORTED");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Persistent_address_is_canonicalized_by_the_boundary_parser()
    {
        SharedServer.ParsePersistentAddress(" /notes/one ").Value.Should().Be("/notes/one");
    }

    private sealed class SingleEntryRegistry(SharedBaseEntry entry) : ISharedBaseRegistry
    {
        public SharedBaseResult Add(string name, string root) => throw new NotSupportedException();
        public SharedBaseResult Remove(string name) => throw new NotSupportedException();
        public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) => [entry];
        public SharedBaseResult Enable(string name) => throw new NotSupportedException();
        public SharedBaseResult Disable(string name) => throw new NotSupportedException();
        public SharedBaseEntry? Lookup(string name) => entry;
        public IReadOnlyList<SharedBaseCollision> Collisions() => [];
    }
}
