using System.Text.Json.Serialization;
using Scrinia.Core.Models;

namespace Scrinia.Commands;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(MemoryAddress))]
[JsonSerializable(typeof(MemoryAddressKind))]
[JsonSerializable(typeof(MemoryWrite))]
[JsonSerializable(typeof(MemoryError))]
[JsonSerializable(typeof(MemoryAddressError))]
[JsonSerializable(typeof(MemorySearchRequest))]
[JsonSerializable(typeof(MemoryListRequest))]
[JsonSerializable(typeof(ArtifactEntry))]
internal partial class ConfigJsonContext : JsonSerializerContext;
