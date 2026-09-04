using System.Text.Json.Serialization;

namespace Scrinia.Core.Configuration;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ConfigJsonContext : JsonSerializerContext;
