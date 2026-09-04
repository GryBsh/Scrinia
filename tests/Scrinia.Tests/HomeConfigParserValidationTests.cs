using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class HomeConfigParserValidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scrinia-home-parser-tests", Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> InvalidDocuments =>
    [
        ["{"],
        ["{\"schemaVersion\":1,\"schemaVersion\":1,\"sharedBases\":{}}"],
        ["{\"schemaVersion\":1,\"sharedBases\":{},\"sharedBases\":{}}"],
        ["{\"sharedBases\":{}}"],
        ["{\"schemaVersion\":1}"],
        ["{\"schemaVersion\":2,\"sharedBases\":{}}"],
        ["{\"schemaVersion\":\"1\",\"sharedBases\":{}}"],
        ["{\"schemaVersion\":1,\"sharedBases\":{\"team\":{}}}"],
        ["{\"schemaVersion\":1,\"sharedBases\":{\"team\":{\"root\":\"relative\"}}}"],
    ];

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Load_InvalidDocument_FailsClosed(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "config.json"), json);

        Action load = () => new HomeConfigStore().Load(_directory);

        load.Should().Throw<ScriniaConfigurationException>();
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyV1Registry()
    {
        HomeConfig config = new HomeConfigStore().Load(_directory);

        config.SchemaVersion.Should().Be(1);
        config.SharedBases.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
