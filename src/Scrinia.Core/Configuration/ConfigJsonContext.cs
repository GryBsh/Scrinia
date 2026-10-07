using System.Text.Json.Serialization;

namespace Scrinia.Core.Configuration;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
// Metadata mode preserves null byte arrays; the generated fast writer can turn them into empty strings.
[JsonSerializable(typeof(Scrinia.Core.Migration.TrackingJournal), GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class ConfigJsonContext : JsonSerializerContext;
