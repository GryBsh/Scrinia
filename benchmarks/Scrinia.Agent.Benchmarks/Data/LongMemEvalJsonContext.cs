using System.Text.Json.Serialization;

namespace Scrinia.Agent.Benchmarks;

[JsonSerializable(typeof(IReadOnlyList<LearningSession>), TypeInfoPropertyName = "IReadOnlyListLearningSession")]
[JsonSerializable(typeof(RawLongMemEvalCase[]))]
internal partial class LongMemEvalJsonContext : JsonSerializerContext;
