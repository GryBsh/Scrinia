using System.Text.Json.Serialization;
using Scrinia.Core.Models;
using Scrinia.Core.Embeddings;

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
[JsonSerializable(typeof(EmbeddingsStatusResponse))]
[JsonSerializable(typeof(EmbeddingsIndexRequest))]
[JsonSerializable(typeof(EmbeddingsSearchRequest))]
[JsonSerializable(typeof(EmbeddingsRemoveRequest))]
[JsonSerializable(typeof(EmbeddingsOperationResponse))]
[JsonSerializable(typeof(EmbeddingsSearchResponse))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
internal partial class PluginClientJsonContext : JsonSerializerContext;
