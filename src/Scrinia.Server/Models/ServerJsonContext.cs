using System.Text.Json.Serialization;

namespace Scrinia.Server.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreRequest))]
[JsonSerializable(typeof(StoreResponse))]
[JsonSerializable(typeof(AppendRequest))]
[JsonSerializable(typeof(AppendResponse))]
[JsonSerializable(typeof(ListResponse))]
[JsonSerializable(typeof(MemoryListItem))]
[JsonSerializable(typeof(ShowResponse))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(SearchResultItem))]
[JsonSerializable(typeof(ChunkResponse))]
[JsonSerializable(typeof(CopyRequest))]
[JsonSerializable(typeof(ExportRequest))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(HealthCheck))]
[JsonSerializable(typeof(HealthCheck[]))]
[JsonSerializable(typeof(CreateKeyRequest))]
[JsonSerializable(typeof(CreateKeyResponse))]
[JsonSerializable(typeof(KeySummaryDto))]
[JsonSerializable(typeof(KeySummaryDto[]))]
[JsonSerializable(typeof(PluginInfo))]
[JsonSerializable(typeof(PluginInfo[]))]
public partial class ServerJsonContext : JsonSerializerContext;
