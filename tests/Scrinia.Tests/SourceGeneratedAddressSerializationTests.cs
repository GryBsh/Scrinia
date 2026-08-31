using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Commands;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class SourceGeneratedAddressSerializationTests
{
    [Fact]
    public void Production_contexts_register_their_owned_contracts()
    {
        AssertRegistered(BundleJsonContext.Default,
            typeof(MemoryAddress), typeof(MemoryAddressKind), typeof(MemoryWrite),
            typeof(MemoryError), typeof(MemoryAddressError), typeof(MemorySearchRequest),
            typeof(MemoryListRequest));
        AssertRegistered(BundleJsonContext.Default,
            typeof(BundleIndex), typeof(BundleManifest), typeof(Dictionary<string, List<string>>),
            typeof(ArtifactEntry));
        AssertRegistered(ScriniaMcpJsonContext.Default,
            typeof(SkillFileMeta), typeof(AgentFileMeta), typeof(Dictionary<string, string[]>),
            typeof(ArtifactEntry), typeof(MemoryAddress));
        AssertRegistered(CliJsonContext.Default,
            typeof(CliErrorOutput), typeof(MemoryAddress));
        AssertRegistered(ConfigJsonContext.Default, typeof(Dictionary<string, string>));
        AssertRegistered(PluginClientJsonContext.Default, typeof(Dictionary<string, double>));
    }

    [Fact]
    public void Production_contexts_round_trip_canonical_persistent_and_ephemeral_addresses()
    {
        foreach (JsonSerializerContext context in AllContexts())
        {
            JsonTypeInfo<MemoryAddress> typeInfo = TypeInfo<MemoryAddress>(context);
            foreach (string raw in new[] { "/topic/note", "/temp/scratch", "~scratch" })
            {
                MemoryAddress address = MemoryAddress.Parse(raw);
                string json = JsonSerializer.Serialize(address, typeInfo);
                MemoryAddress roundTrip = JsonSerializer.Deserialize(json, typeInfo);

                roundTrip.ToString().Should().Be(address.ToString());
                roundTrip.Kind.Should().Be(address.Kind);
            }
        }
    }

    [Fact]
    public void Address_deserialization_rejects_noncanonical_and_legacy_uri_values()
    {
        foreach (JsonSerializerContext context in AllContexts())
        {
            JsonTypeInfo<MemoryAddress> typeInfo = TypeInfo<MemoryAddress>(context);

            Action noncanonical = () => JsonSerializer.Deserialize("\"/temp/scratch\"", typeInfo);
            Action uri = () => JsonSerializer.Deserialize("\"file:///tmp/scratch\"", typeInfo);

            noncanonical.Should().NotThrow();
            uri.Should().Throw<JsonException>();
            JsonSerializer.Deserialize("\"/temp/scratch\"", typeInfo).ToString()
                .Should().Be("~scratch");
        }
    }

    [Fact]
    public void Artifact_and_bundle_contracts_round_trip_without_uri_properties()
    {
        ArtifactEntry entry = new(
            MemoryAddress.Parse("~scratch"), "artifacts/ephemeral/scratch.nmp2", 12, 1,
            DateTimeOffset.UnixEpoch, Attributes: new Dictionary<string, string[]> { ["tag"] = ["x"] });
        BundleIndex bundle = new([entry]);

        string entryJson = JsonSerializer.Serialize(entry, TypeInfo<ArtifactEntry>(BundleJsonContext.Default));
        string bundleJson = JsonSerializer.Serialize(bundle, TypeInfo<BundleIndex>(BundleJsonContext.Default));

        entryJson.Should().Contain("\"address\": \"~scratch\"");
        entryJson.Should().Contain("\"artifactKey\":");
        entryJson.Should().NotContain("\"uri\"");
        entryJson.Should().NotContain("\"artifactUri\"");
        bundleJson.Should().NotContain("\"uri\"");
        bundleJson.Should().NotContain("\"artifactUri\"");
    }

    [Fact]
    public void Typed_requests_and_errors_round_trip_through_production_metadata()
    {
        MemoryError error = new(MemoryErrorCategory.InvalidWriteContent, "bad_content", "Bad content", "content", 1, 2);
        MemorySearchRequest search = new("query") { Address = MemoryAddress.Parse("/topic/note") };
        MemoryListRequest list = new() { Prefix = MemoryAddress.Parse("~scratch"), Scopes = ["local"] };
        MemoryWrite write = new(
            MemoryAddress.Parse("/topic/note"), [new ReadOnlyMemory<byte>("body"u8.ToArray())],
            new Dictionary<string, IReadOnlyList<string>> { ["tag"] = ["x"] }, ["keyword"]);

        foreach (JsonSerializerContext context in new JsonSerializerContext[]
                 {
                     BundleJsonContext.Default, CliJsonContext.Default,
                     ConfigJsonContext.Default, PluginClientJsonContext.Default
                 })
        {
            MemoryError errorRoundTrip = RoundTrip(error, TypeInfo<MemoryError>(context));
            MemorySearchRequest searchRoundTrip = RoundTrip(search, TypeInfo<MemorySearchRequest>(context));
            MemoryListRequest listRoundTrip = RoundTrip(list, TypeInfo<MemoryListRequest>(context));
            MemoryWrite writeRoundTrip = RoundTrip(write, TypeInfo<MemoryWrite>(context));
            MemoryAddress addressRoundTrip = RoundTrip(search.Address.Value, TypeInfo<MemoryAddress>(context));
            MemoryAddressKind kindRoundTrip = RoundTrip(addressRoundTrip.Kind, TypeInfo<MemoryAddressKind>(context));
            MemoryAddressError addressErrorRoundTrip = RoundTrip(
                new MemoryAddressError(MemoryErrorCategory.MalformedAddress, "malformed_address",
                    "Malformed address", "address"), TypeInfo<MemoryAddressError>(context));
            ArtifactEntry entryRoundTrip = RoundTrip(
                new ArtifactEntry(MemoryAddress.Parse("/topic/entry"), "artifacts/topic/entry.nmp2",
                    4, 1, DateTimeOffset.UnixEpoch), TypeInfo<ArtifactEntry>(context));

            errorRoundTrip.Should().Be(error);
            searchRoundTrip.Address!.Value.ToString().Should().Be("/topic/note");
            listRoundTrip.Prefix!.Value.ToString().Should().Be("~scratch");
            writeRoundTrip.Address.ToString().Should().Be("/topic/note");
            writeRoundTrip.Content.Single().Span.SequenceEqual("body"u8).Should().BeTrue();
            addressRoundTrip.Should().Be(search.Address.Value);
            kindRoundTrip.Should().Be(addressRoundTrip.Kind);
            addressErrorRoundTrip.Category.Should().Be(MemoryErrorCategory.MalformedAddress);
            entryRoundTrip.ArtifactKey.Should().Be("artifacts/topic/entry.nmp2");
        }
    }

    [Fact]
    public async Task File_store_round_trip_uses_the_private_durable_context()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-json-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ArtifactEntry written;
            using (var store = new FileMemoryStore(root))
            {
                written = await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/context/note"), [new ReadOnlyMemory<byte>("body"u8.ToArray())], new Dictionary<string, IReadOnlyList<string>>(), []));
            }

            using var coldStore = new FileMemoryStore(root);
            ArtifactEntry reloaded = coldStore.LoadIndex("local").Should().ContainSingle().Subject;
            reloaded.Address.Should().Be(written.Address);
            reloaded.ArtifactKey.Should().Be(written.ArtifactKey);
            reloaded.ContentPreview.Should().Be(written.ContentPreview);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Categorized_error_serialization_uses_production_category_translation_path()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "scrinia-json-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            using var output = new StringWriter();
            TextWriter previous = Console.Out;
            Console.SetOut(output);
            try
            {
                int exit = await new ScriniaCommands().Show("/temp/scratch", workspace, json: true);
                exit.Should().Be(1);
            }
            finally
            {
                Console.SetOut(previous);
            }

            CliErrorOutput translated = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliErrorOutput)!;
            translated.Category.Should().Be(nameof(MemoryErrorCategory.ForbiddenAddressKind));
            translated.Code.Should().Be("forbidden_address_kind");
            translated.Field.Should().Be("address");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static IEnumerable<JsonSerializerContext> AllContexts()
    {
        yield return BundleJsonContext.Default;
        yield return ScriniaMcpJsonContext.Default;
        yield return CliJsonContext.Default;
        yield return ConfigJsonContext.Default;
        yield return PluginClientJsonContext.Default;
    }

    private static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerContext context) =>
        (JsonTypeInfo<T>)context.GetTypeInfo(typeof(T))!;

    private static void AssertRegistered(JsonSerializerContext context, params Type[] expectedTypes)
    {
        foreach (Type expectedType in expectedTypes)
            context.GetTypeInfo(expectedType).Should().NotBeNull(
                $"{context.GetType().Name} owns {expectedType.Name} and must register it for trimming-safe serialization");
    }

    private static T RoundTrip<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(value, typeInfo), typeInfo)!;
}
