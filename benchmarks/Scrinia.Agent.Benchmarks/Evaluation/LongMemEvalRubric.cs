using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public static class LongMemEvalRubric
{
    public const string UpstreamRevision = "d6dc8b50a2d9ac0c99485ea28fa5755c62414c34";
    private static readonly string[] TemplateKeys = ["standard", "temporal", "update", "preference", "abstention"];
    private static readonly Lazy<(RubricDocument Document, string Hash)> Resource = new(Load);
    public static string ContentHash => Resource.Value.Hash;

    public static string Render(EvaluationInput evaluation, AnswerInput input, string candidate)
    {
        if (string.IsNullOrWhiteSpace(evaluation.ReferenceAnswer) || string.IsNullOrWhiteSpace(input.Question))
            throw new InvalidDataException("Question and reference answer are required for grading.");
        string key = evaluation.Abstention ? "abstention" : evaluation.Category switch
        {
            "single-session-user" or "single-session-assistant" or "multi-session" => "standard",
            "temporal-reasoning" => "temporal",
            "knowledge-update" => "update",
            "single-session-preference" => "preference",
            _ => throw new InvalidDataException("Unsupported LongMemEval grading category.")
        };
        var parts = Resource.Value.Document.Templates[key].Text.Split("{}", StringSplitOptions.None);
        return string.Concat(parts[0], input.Question, parts[1], evaluation.ReferenceAnswer, parts[2], candidate, parts[3]);
    }

    public static bool? ParseVerdict(string text) => text.Trim().ToLowerInvariant() switch
    { "yes" => true, "no" => false, _ => null };

    private static (RubricDocument, string) Load()
    {
        using var stream = typeof(LongMemEvalRubric).Assembly.GetManifestResourceStream("Scrinia.Agent.Benchmarks.rubrics.json")
            ?? throw new InvalidDataException("Embedded LongMemEval rubric resource is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var content = bytes.ToArray();
        var document = JsonSerializer.Deserialize(content, RecordJsonContext.Default.RubricDocument)
            ?? throw new InvalidDataException("Embedded rubric resource is invalid.");
        if (document.UpstreamRevision != UpstreamRevision || document.Templates.Count != 5 ||
            TemplateKeys.Any(k => !document.Templates.ContainsKey(k)))
            throw new InvalidDataException("Embedded rubric identity is invalid.");
        foreach (var template in document.Templates.Values)
            if (template.Text.Split("{}", StringSplitOptions.None).Length != 4 ||
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(template.Text))) != template.Sha256)
                throw new InvalidDataException("Embedded rubric template digest or placeholders are invalid.");
        return (document, Convert.ToHexStringLower(SHA256.HashData(content)));
    }
}
internal sealed record RubricDocument(string UpstreamRevision, Dictionary<string, RubricTemplate> Templates);
internal sealed record RubricTemplate(string Text, string Sha256);
