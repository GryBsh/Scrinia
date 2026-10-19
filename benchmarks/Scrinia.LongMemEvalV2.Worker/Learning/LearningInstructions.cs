using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Protocol;
namespace Scrinia.LongMemEvalV2.Worker.Learning;

public static class LearningInstructions
{
    public static readonly HashSet<string> AllowedTools = new(["remember","recall","search","list"], StringComparer.Ordinal);
    public static string Text { get; } = Load();
    private static string Load()
    {
        using var stream = typeof(LearningInstructions).Assembly.GetManifestResourceStream(
            "Scrinia.LongMemEvalV2.Worker.Learning.learning-instructions.txt")
            ?? throw new InvalidDataException("LEARNING_INSTRUCTIONS_MISSING");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }
    public static void VerifyContext(LearningConfiguration settings, string actualCatalogHash)
    {
        if (settings.CatalogHash != actualCatalogHash
            || settings.InstructionsHash != SourceCatalog.Hash(Encoding.UTF8.GetBytes(Text))
            || settings.ProcessorRevision != "c202236235762e1c871ad0ccb60c8ee5ba337b9a")
            throw new InvalidDataException("LEARNING_ACCOUNTING_MISMATCH");
    }
    public static Action<string,AIFunctionArguments> Guard(string trajectoryHash, IEnumerable<string> sourceIds)
    {
        var sources = sourceIds.ToHashSet(StringComparer.Ordinal);
        return (name,args) =>
        {
            if (!AllowedTools.Contains(name)) throw new InvalidDataException("LEARNING_TOOL_FORBIDDEN");
            if (name != "remember") return;
            var path = Value(args,"path");
            string prefix = "/derived/" + trajectoryHash + "/";
            if (path.ValueKind != JsonValueKind.String || path.GetString() is not { } address
                || !address.StartsWith(prefix,StringComparison.Ordinal)
                || address.Length <= prefix.Length || address[prefix.Length..].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || Value(args,"description").ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(Value(args,"description").GetString()))
                throw new InvalidDataException("LEARNING_WRITE_FORBIDDEN");
            var replace = Value(args,"replace");
            if (replace.ValueKind != JsonValueKind.Undefined && replace.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("LEARNING_REPLACEMENT_FORBIDDEN");
            var content = Value(args,"content");
            var attributes = Value(args,"attributes");
            if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0
                || content.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString()))
                || attributes.ValueKind != JsonValueKind.Object
                || !attributes.TryGetProperty("source",out var references) || references.ValueKind != JsonValueKind.Array
                || references.GetArrayLength() == 0 || references.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || !sources.Contains(x.GetString()!)))
                throw new InvalidDataException("LEARNING_SOURCE_REFERENCE_INVALID");
        };
    }
    private static JsonElement Value(AIFunctionArguments args,string name)
    {
        if (!args.TryGetValue(name,out var value)) return default;
        if (value is JsonElement element) return element;
        if (value is string text) return JsonSerializer.SerializeToElement(text,WorkerJsonContext.Default.String);
        if (value is string[] items) return JsonSerializer.SerializeToElement(items,WorkerJsonContext.Default.StringArray);
        if (value is Dictionary<string,string[]> attributes) return JsonSerializer.SerializeToElement(attributes,WorkerJsonContext.Default.DictionaryStringStringArray);
        if (value is bool flag) return flag ? JsonDocument.Parse("true").RootElement : JsonDocument.Parse("false").RootElement;
        throw new InvalidDataException("LEARNING_ARGUMENT_INVALID");
    }
}
