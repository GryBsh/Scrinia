using System.Text.Json.Serialization;
using Scrinia.Core.Models;

namespace Scrinia.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(MemoryAddress))]
[JsonSerializable(typeof(MemoryAddressKind))]
[JsonSerializable(typeof(MemoryWrite))]
[JsonSerializable(typeof(MemoryError))]
[JsonSerializable(typeof(MemoryAddressError))]
[JsonSerializable(typeof(MemorySearchRequest))]
[JsonSerializable(typeof(MemoryListRequest))]
[JsonSerializable(typeof(ArtifactEntry))]
internal partial class PluginClientJsonContext : JsonSerializerContext;
